using System.Collections.Concurrent;
using System.Diagnostics;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Redis;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Homeostat.Tests;

/// <summary>
/// The shared control state (pause, switches, approvals) and its stores after an independent review: refused safe
/// changes are reported, unreadable records fail closed, waits on the store are bounded, and late writes are cleaned up.
/// </summary>
public class ControlReviewTests
{
    private static readonly HomeostatTestHostOptions Still = new() { AutoAdvanceStep = null };

    private static Task<HomeostatTestHost> StartInstanceAsync(
        IHomeostatStore store, LogCollector? logs = null, Action<HomeostatBuilder>? configure = null, string instance = "a")
        => HomeostatTestHost.StartAsync(
            h =>
            {
                h.UseStore(_ => store).Configure(o =>
                {
                    o.InstanceId = instance;
                    o.ControlRefreshInterval = TimeSpan.FromSeconds(5);
                });
                configure?.Invoke(h);
            },
            services =>
            {
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            },
            Still);

    private static Task RefreshAsync(HomeostatTestHost host)
        => host.Services.GetRequiredService<Engine.SharedControl>().TryRefreshAsync(CancellationToken.None);

    // ------------------------------------------------------------------ C1: refused safe changes are reported

    [Fact]
    public async Task A_pause_refused_for_a_later_resume_is_logged_as_a_warning_naming_it()
    {
        var store = new InMemoryHomeostatStore();
        var logs = new LogCollector();
        await using var host = await StartInstanceAsync(store, logs);

        // Another instance, whose clock is a minute ahead, resumed: its decision is later than this pause.
        Assert.True(await store.TrySetPauseAsync(null, host.Time.GetUtcNow().AddMinutes(1)));
        await host.Control.PauseAsync(by: "on-call");

        var warning = Assert.Single(logs.Warnings(), m => m.Contains("refused a change (pause)", StringComparison.Ordinal));
        Assert.Contains("resumed by a decision taken after this pause", warning);

        // The store is right: once read, the pause no longer applies here.
        await RefreshAsync(host);
        Assert.False(host.Control.IsPaused);
    }

    [Fact]
    public async Task A_deferred_pause_refused_for_a_later_resume_is_logged_as_a_warning()
    {
        var store = new HookStore();
        var logs = new LogCollector();
        await using var host = await StartInstanceAsync(store, logs);

        store.Down = true;
        await host.Control.PauseAsync(by: "on-call");   // deferred
        Assert.True(host.Control.IsPaused);

        // Meanwhile a resume decided later reaches the store.
        Assert.True(await store.Inner.TrySetPauseAsync(null, host.Time.GetUtcNow().AddMinutes(1)));
        store.Down = false;
        await RefreshAsync(host);

        await PollyControlTests.WaitUntilAsync(() => logs.Warnings().Any(m => m.Contains("refused a change (pause)", StringComparison.Ordinal)));
        await RefreshAsync(host);
        Assert.False(host.Control.IsPaused);
    }

    [Fact]
    public async Task A_deferred_turn_off_superseded_by_a_later_turn_on_is_logged_as_a_warning()
    {
        var store = new HookStore();
        var logs = new LogCollector();
        await using var host = await StartInstanceAsync(store, logs);
        await host.Switches.TurnOnAsync("serve-cached", SwitchScope.Application, "on-call");

        store.Down = true;
        await host.Switches.TurnOffAsync("serve-cached", "on-call");   // deferred
        Assert.False(host.Switches.IsOn("serve-cached"));

        var later = host.Time.GetUtcNow().AddMinutes(1);
        Assert.True(await store.Inner.TrySetSwitchAsync("serve-cached", new SwitchState { Name = "serve-cached", By = "release-manager", At = later }, later));
        store.Down = false;
        await RefreshAsync(host);

        var warning = Assert.Single(logs.Warnings(), m => m.Contains("refused a change (switch:serve-cached)", StringComparison.Ordinal));
        Assert.Contains("by release-manager", warning);
        Assert.True(host.Switches.IsOn("serve-cached"));
    }

    // ------------------------------------------------------------------ C2, C3: unreadable pause records fail closed

    [Fact]
    public async Task An_unreadable_Redis_pause_record_counts_as_a_pause()
    {
        using var redis = RedisFixture.Connect();
        var prefix = RedisFixture.Prefix();
        var store = new RedisHomeostatStore(redis, Options.Create(new RedisStoreOptions { KeyPrefix = prefix }), Options.Create(new HomeostatOptions()));

        await redis.GetDatabase().StringSetAsync($"{prefix}{{control}}:pause", "{ not json");
        var pause = (await store.GetControlStateAsync()).Pause;
        Assert.NotNull(pause);
        Assert.Null(pause!.Until);
        Assert.Contains("unreadable", pause.By!);

        // A person resumes on purpose: the record is replaced.
        Assert.True(await store.TrySetPauseAsync(null, DateTimeOffset.UtcNow));
        Assert.Null((await store.GetControlStateAsync()).Pause);
    }

    [Fact]
    public async Task An_unreadable_store_file_starts_the_store_paused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ \"pause\": ");
            using (var store = new FileHomeostatStore(path))
            {
                var pause = (await store.GetControlStateAsync()).Pause;
                Assert.NotNull(pause);
                Assert.Null(pause!.Until);
                Assert.Contains("store file unreadable", pause.By!);
                Assert.True(File.Exists(path + ".corrupt"));
            }

            // Written to the new file: it survives a restart, until a person resumes.
            using (var reopened = new FileHomeostatStore(path))
            {
                Assert.NotNull((await reopened.GetControlStateAsync()).Pause);
                Assert.True(await reopened.TrySetPauseAsync(null, DateTimeOffset.UtcNow));
            }

            using var resumed = new FileHomeostatStore(path);
            Assert.Null((await resumed.GetControlStateAsync()).Pause);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".corrupt");
            File.Delete(path + ".lock");
        }
    }

    // ------------------------------------------------------------------ C4: the owner's renewal period

    [Fact]
    public async Task An_owner_that_renews_less_often_keeps_its_switch()
    {
        var store = new InMemoryHomeostatStore();
        await using var host = await StartInstanceAsync(store, instance: "b");
        var renewed = host.Time.GetUtcNow() - TimeSpan.FromMinutes(3);

        // Instance a renews every 5 minutes; this instance would call a claim three minutes old abandoned.
        Assert.True(await store.TryClaimSwitchAsync(new SwitchState
        {
            Name = "serve-cached", Condition = "feed-down", Instance = "a", At = renewed, RenewedAt = renewed, RenewInterval = TimeSpan.FromMinutes(5),
        }));

        // A claim written by an older version, without the period: judged by this instance's own, as before.
        Assert.True(await store.TryClaimSwitchAsync(new SwitchState { Name = "degrade", Condition = "feed-down", Instance = "a", At = renewed, RenewedAt = renewed }));

        await RefreshAsync(host);
        await ((HomeostatSwitches)host.Switches).MaintainOwnershipAsync(CancellationToken.None);

        var switches = (await store.GetControlStateAsync()).Switches;
        Assert.Contains(switches, s => s.Name == "serve-cached");
        Assert.DoesNotContain(switches, s => s.Name == "degrade");
    }

    [Fact]
    public async Task A_claim_records_the_renewal_period_of_its_owner()
    {
        var store = new InMemoryHomeostatStore();
        await using var host = await StartInstanceAsync(store);
        await ((HomeostatSwitches)host.Switches).TurnOnForConditionAsync("serve-cached", "feed-down", CancellationToken.None);

        var claim = Assert.Single((await store.GetControlStateAsync()).Switches);
        Assert.Equal(TimeSpan.FromSeconds(15), claim.RenewInterval);   // 3 refresh intervals
    }

    // ------------------------------------------------------------------ C5: a refused claim with the switch off fails the step

    [Fact]
    public async Task A_turn_on_step_whose_claim_is_refused_with_the_switch_off_fails()
    {
        var store = new InMemoryHomeostatStore();
        await using var host = await StartInstanceAsync(store);
        var switches = (HomeostatSwitches)host.Switches;

        // Turned off by a decision later than the step's claim.
        Assert.True(await store.TrySetSwitchAsync("serve-cached", null, host.Time.GetUtcNow().AddMinutes(1)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await switches.TurnOnForConditionAsync("serve-cached", "feed-down", CancellationToken.None));
        Assert.Contains("serve-cached", error.Message);
        Assert.False(host.Switches.IsOn("serve-cached"));

        // On for a person: the step does what it should, the switch is on.
        await host.Switches.TurnOnAsync("degrade", SwitchScope.Application, "on-call");
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await switches.TurnOnForConditionAsync("degrade", "feed-down", CancellationToken.None);
        Assert.True(host.Switches.IsOn("degrade"));
    }

    // ------------------------------------------------------------------ C8, C9, C14: approvals

    [Fact]
    public async Task Listing_pending_approvals_does_not_block_the_caller_when_the_store_blocks()
    {
        var store = new HookStore();
        await using var host = await StartInstanceAsync(store);

        store.BlockReads = true;
        try
        {
            var watch = Stopwatch.StartNew();
            var listing = host.Approvals.GetPendingAsync().AsTask();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the caller was held {watch.Elapsed}");
            Assert.False(listing.IsCompleted);

            store.BlockReads = false;
            store.Reads.Set();
            Assert.Empty(await listing);
        }
        finally
        {
            store.Reads.Set();
        }
    }

    [Fact]
    public async Task An_answer_that_timed_out_but_was_recorded_succeeds()
    {
        var store = new HookStore();
        await using var host = await StartInstanceAsync(store);
        await AddRequestAsync(store, host, "r1");

        store.HangAfterResolve = true;
        try
        {
            Assert.True(await host.Approvals.ApproveAsync("r1", "on-call"));
        }
        finally
        {
            store.Resolves.Set();
        }

        Assert.Equal(ApprovalStatus.Approved, Assert.Single((await store.GetControlStateAsync()).Approvals).Status);
    }

    [Fact]
    public async Task An_answer_that_timed_out_and_is_still_pending_reports_an_unknown_outcome()
    {
        var store = new HookStore();
        await using var host = await StartInstanceAsync(store);
        await AddRequestAsync(store, host, "r1");

        store.HangBeforeResolve = true;
        try
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(async () => await host.Approvals.ApproveAsync("r1", "on-call"));
            Assert.Contains("unknown", error.Message);
        }
        finally
        {
            store.Resolves.Set();
        }
    }

    [Fact]
    public async Task A_request_written_after_its_registration_timed_out_is_removed()
    {
        var store = new HookStore();
        await using var host = await StartInstanceAsync(store);
        var approvals = host.Services.GetRequiredService<Engine.HomeostatApprovals>();

        store.HoldAdds = true;
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => approvals.RegisterAsync(Request(host, "late"), giveUp.Token));

        // The store writes it only now, with nobody waiting: it is removed once the write ends.
        store.Adds.Set();
        await PollyControlTests.WaitUntilAsync(() => store.Added);
        await PollyControlTests.WaitUntilAsync(() => store.Inner.GetControlStateAsync().AsTask().Result.Approvals.Count == 0);
    }

    [Fact]
    public async Task The_memory_store_drops_requests_a_day_after_they_expired()
    {
        var store = new InMemoryHomeostatStore();
        var t0 = DateTimeOffset.Parse("2026-11-05T10:00:00Z");
        PendingApproval At(string id, DateTimeOffset requested) => new()
        {
            Id = id, ConditionName = "feed-down", Step = 0, StepName = "renew", RequestedAt = requested, ExpiresAt = requested.AddMinutes(5),
            TriggeringSignal = new Signal("feed", SignalSeverity.Unhealthy, requested),
        };

        await store.AddApprovalAsync(new ApprovalRecord { Request = At("old", t0), Instance = "a" });
        await store.AddApprovalAsync(new ApprovalRecord { Request = At("recent", t0.AddHours(20)), Instance = "a" });
        await store.AddApprovalAsync(new ApprovalRecord { Request = At("new", t0.AddDays(1).AddMinutes(10)), Instance = "a" });

        var ids = (await store.GetControlStateAsync()).Approvals.Select(a => a.Request.Id).ToArray();
        Assert.Equal(["recent", "new"], ids);
    }

    [Fact]
    public async Task Removing_a_Redis_approval_removes_its_answer_too()
    {
        using var redis = RedisFixture.Connect();
        var prefix = RedisFixture.Prefix();
        var store = new RedisHomeostatStore(redis, Options.Create(new RedisStoreOptions { KeyPrefix = prefix }), Options.Create(new HomeostatOptions()));
        var now = DateTimeOffset.UtcNow;
        var request = new PendingApproval
        {
            Id = "r1", ConditionName = "feed-down", Step = 0, StepName = "renew", RequestedAt = now, ExpiresAt = now.AddMinutes(5),
            TriggeringSignal = new Signal("feed", SignalSeverity.Unhealthy, now),
        };

        await store.AddApprovalAsync(new ApprovalRecord { Request = request, Instance = "a" });
        Assert.True(await store.TryResolveApprovalAsync("r1", ApprovalStatus.Approved, "on-call"));
        await store.RemoveApprovalAsync("r1");

        var db = redis.GetDatabase();
        Assert.False(await db.HashExistsAsync($"{prefix}{{control}}:approvals", "r1"));
        Assert.False(await db.HashExistsAsync($"{prefix}{{control}}:answers", "r1"));
    }

    // ------------------------------------------------------------------ C10: an older resume does not hide a newer pause

    [Fact]
    public async Task A_pause_written_while_an_older_resume_returns_stays_shown()
    {
        var store = new HookStore();
        await using var host = await StartInstanceAsync(store);
        await host.Control.PauseAsync(by: "deploy");
        host.Time.Advance(TimeSpan.FromSeconds(1));

        // The store applies the resume but answers late; meanwhile a pause decided later is written (and taken).
        store.HoldResumeReply = true;
        var resuming = host.Control.ResumeAsync().AsTask();
        await PollyControlTests.WaitUntilAsync(() => store.ResumeApplied);
        store.BlockReads = true;   // no read-back may fix the view: what matters is what the resume leaves shown
        try
        {
            host.Time.Advance(TimeSpan.FromSeconds(1));
            await host.Control.PauseAsync(by: "on-call");
            store.ResumeReplies.Set();
            await resuming;

            Assert.True(host.Control.IsPaused);
        }
        finally
        {
            store.BlockReads = false;
            store.Reads.Set();
            store.ResumeReplies.Set();
        }

        await RefreshAsync(host);
        Assert.True(host.Control.IsPaused);
        Assert.Equal("on-call", (await store.GetControlStateAsync()).Pause?.By);
    }

    // ------------------------------------------------------------------ C11: a failed Redis connection is cached

    [Fact]
    public async Task A_failed_Redis_connection_is_not_resolved_again_on_every_call()
    {
        var resolutions = 0;
        IConnectionMultiplexer Resolve()
        {
            Interlocked.Increment(ref resolutions);
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "unreachable");
        }

        var options = Options.Create(new RedisStoreOptions());
        var store = new RedisHomeostatStore(Resolve, options, Options.Create(new HomeostatOptions()));
        var coordinator = new RedisCoordinator(Resolve, options, NullLogger<RedisCoordinator>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await store.GetControlStateAsync());
            await Assert.ThrowsAnyAsync<Exception>(async () => await coordinator.TryAcquireAsync("plan:feed-down", TimeSpan.FromMinutes(1)));
        }

        Assert.Equal(2, Volatile.Read(ref resolutions));   // once for the store, once for the coordinator
    }

    // ------------------------------------------------------------------ C12: the owner's release and its own renewal

    [Fact]
    public async Task The_owners_release_is_not_refused_by_its_own_renewal()
    {
        var store = new HookStore();
        await using var host = await StartInstanceAsync(store);
        var switches = (HomeostatSwitches)host.Switches;
        await switches.TurnOnForConditionAsync("serve-cached", "feed-down", CancellationToken.None);
        await RefreshAsync(host);

        // A renewal is due, and reaches the store late: it holds the switch writes, as a store applies them in order.
        host.Time.Advance(TimeSpan.FromSeconds(15));
        store.HoldRenewals = true;
        var maintaining = Task.Run(() => switches.MaintainOwnershipAsync(CancellationToken.None));
        await PollyControlTests.WaitUntilAsync(() => store.RenewalWaiting);

        // The condition clears here meanwhile, at the very time of the renewal: the release applies on the instance at
        // once (it does not wait for the renewal, S1), and is stamped after it, so the store does not refuse it (S3).
        var releasing = Task.Run(() => switches.ReleaseOwnedAsync("serve-cached", "feed-down", CancellationToken.None));
        await PollyControlTests.WaitUntilAsync(() => !host.Switches.IsOn("serve-cached"));
        Assert.False(releasing.IsCompleted);   // its write is still behind the renewal
        store.Renewals.Set();
        await maintaining;
        Assert.True(await releasing);

        Assert.DoesNotContain((await store.GetControlStateAsync()).Switches, s => s.Name == "serve-cached");
    }

    // ------------------------------------------------------------------ C16: a lease whose acquisition timed out

    [Fact]
    public async Task A_lease_whose_acquisition_timed_out_after_Redis_applied_it_is_released()
    {
        using var redis = RedisFixture.Connect();
        var prefix = RedisFixture.Prefix();

        // Redis applies SET NX, but the reply is lost (a client-side timeout).
        var connection = LostReplies.Wrap(redis);
        var coordinator = new RedisCoordinator(connection, Options.Create(new RedisStoreOptions { KeyPrefix = prefix }), NullLogger<RedisCoordinator>.Instance);
        await Assert.ThrowsAsync<TimeoutException>(async () => await coordinator.TryAcquireAsync("plan:feed-down", TimeSpan.FromMinutes(1)));

        // Released with the acquisition's token: nobody holds the lease, so the plan can run.
        Assert.False(await redis.GetDatabase().KeyExistsAsync($"{prefix}lease:plan:feed-down"));
        await using var lease = await coordinator.TryAcquireAsync("plan:feed-down", TimeSpan.FromMinutes(1));
        Assert.NotNull(lease);
    }

    /// <summary>A connection whose first <c>SET</c> reaches Redis but whose reply never arrives.</summary>
    public class LostReplies : System.Reflection.DispatchProxy
    {
        private object _target = null!;
        private int[] _lost = [0];   // shared by the connection and its databases

        public static IConnectionMultiplexer Wrap(IConnectionMultiplexer connection)
        {
            var proxy = Create<IConnectionMultiplexer, LostReplies>();
            ((LostReplies)(object)proxy)._target = connection;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(_target, args);
            if (result is IDatabase database)
            {
                var proxy = Create<IDatabase, LostReplies>();
                ((LostReplies)(object)proxy)._target = database;
                ((LostReplies)(object)proxy)._lost = _lost;
                return proxy;
            }

            if (targetMethod.Name == nameof(IDatabase.StringSetAsync) && result is Task<bool> set && Interlocked.Exchange(ref _lost[0], 1) == 0)
            {
                return set.ContinueWith<bool>(_ => throw new TimeoutException("reply lost"), TaskScheduler.Default);
            }

            return result;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static PendingApproval Request(HomeostatTestHost host, string id)
    {
        var now = host.Time.GetUtcNow();
        return new PendingApproval
        {
            Id = id, ConditionName = "feed-down", Step = 0, StepName = "renew", RequestedAt = now, ExpiresAt = now.AddMinutes(30),
            TriggeringSignal = new Signal("feed", SignalSeverity.Unhealthy, now),
        };
    }

    private static async Task AddRequestAsync(HookStore store, HomeostatTestHost host, string id)
        => await store.Inner.AddApprovalAsync(new ApprovalRecord { Request = Request(host, id), Instance = "elsewhere" });

    /// <summary>Collects formatted log messages by level.</summary>
    private sealed class LogCollector : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> Warnings() => _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(LogCollector owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner._entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>An in-memory store with switches for the failures each test needs.</summary>
    private sealed class HookStore : IHomeostatStore
    {
        private readonly SemaphoreSlim _switchWrites = new(1, 1);

        public InMemoryHomeostatStore Inner { get; } = new();

        /// <summary>Every control call fails, as during an outage.</summary>
        public volatile bool Down;

        /// <summary>Reads block the calling thread until <see cref="Reads"/> is set.</summary>
        public volatile bool BlockReads;

        public ManualResetEventSlim Reads { get; } = new(false);

        public volatile bool HangBeforeResolve;

        public volatile bool HangAfterResolve;

        public ManualResetEventSlim Resolves { get; } = new(false);

        public volatile bool HoldAdds;

        public volatile bool Added;

        public ManualResetEventSlim Adds { get; } = new(false);

        public volatile bool HoldResumeReply;

        public volatile bool ResumeApplied;

        public ManualResetEventSlim ResumeReplies { get; } = new(false);

        public volatile bool HoldRenewals;

        public volatile bool RenewalWaiting;

        public ManualResetEventSlim Renewals { get; } = new(false);

        private void Check()
        {
            if (Down)
            {
                throw new IOException("store down");
            }
        }

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default)
        {
            Check();
            if (BlockReads)
            {
                Reads.Wait(TimeSpan.FromSeconds(30));
            }

            return Inner.GetControlStateAsync(cancellationToken);
        }

        public async ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        {
            Check();
            var applied = await Inner.TrySetPauseAsync(pause, decidedAt, CancellationToken.None);
            if (pause is null && HoldResumeReply)
            {
                ResumeApplied = true;
                await Task.Run(() => ResumeReplies.Wait(TimeSpan.FromSeconds(20)), CancellationToken.None);
            }

            return applied;
        }

        public async ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        {
            Check();
            await _switchWrites.WaitAsync(CancellationToken.None);
            try
            {
                return await Inner.TrySetSwitchAsync(name, state, decidedAt, CancellationToken.None);
            }
            finally
            {
                _switchWrites.Release();
            }
        }

        public async ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default)
        {
            Check();
            await _switchWrites.WaitAsync(CancellationToken.None);
            try
            {
                var renewal = state.RenewedAt is { } renewed && renewed != state.At;
                if (renewal && HoldRenewals)
                {
                    RenewalWaiting = true;
                    await Task.Run(() => Renewals.Wait(TimeSpan.FromSeconds(20)), CancellationToken.None);
                }

                return await Inner.TryClaimSwitchAsync(state, CancellationToken.None);
            }
            finally
            {
                _switchWrites.Release();
            }
        }

        public async ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        {
            Check();
            await _switchWrites.WaitAsync(CancellationToken.None);
            try
            {
                return await Inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, CancellationToken.None);
            }
            finally
            {
                _switchWrites.Release();
            }
        }

        public async ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default)
        {
            Check();
            if (HoldAdds)
            {
                await Task.Run(() => Adds.Wait(TimeSpan.FromSeconds(20)), CancellationToken.None);   // ignores the token
            }

            await Inner.AddApprovalAsync(approval, CancellationToken.None);
            Added = true;
        }

        public async ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default)
        {
            Check();
            if (HangBeforeResolve)
            {
                await Task.Run(() => Resolves.Wait(TimeSpan.FromSeconds(30)), CancellationToken.None);
            }

            var resolved = await Inner.TryResolveApprovalAsync(id, status, resolvedBy, CancellationToken.None);
            if (HangAfterResolve)
            {
                await Task.Run(() => Resolves.Wait(TimeSpan.FromSeconds(30)), CancellationToken.None);
            }

            return resolved;
        }

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) { Check(); return Inner.RemoveApprovalAsync(id, cancellationToken); }

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => Inner.GetPlanStateAsync(key, cancellationToken);

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => Inner.UpdatePlanStateAsync(key, update, cancellationToken);

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => Inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => Inner.GetBudgetUsedAsync(now, window, cancellationToken);

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => Inner.AppendRunAsync(run, maxRuns, cancellationToken);

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => Inner.GetRecentRunsAsync(maxRuns, cancellationToken);
    }
}
