using System.Diagnostics;
using Homeostat.Conditions;
using Homeostat.Detectors;
using Homeostat.Healing;
using Homeostat.Healing.Actions;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

/// <summary>
/// The rest of the rules of "Homeostat is never what keeps the application down": each test breaks one more piece.
/// </summary>
public class ReliabilityGapTests
{
    // ---------------------------------------------------------------- restarts

    [Fact]
    public async Task Restart_limits_hold_when_every_replica_restarts_at_the_same_moment()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-05T10:00:00Z"));
        var collection = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(time);
        collection.AddHomeostat();
        await using var services = collection.BuildServiceProvider();
        time.Advance(TimeSpan.FromHours(1));   // past any minimum uptime
        var limits = new RestartLimits(TimeSpan.Zero, MaxRestarts: 3, TimeSpan.FromHours(1));
        var context = new HealingContext("pool-exhausted", new Signal("pool", SignalSeverity.Unhealthy, time.GetUtcNow()), 0, "restart",
            time.GetUtcNow(), services, () => throw new NotSupportedException());

        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            try
            {
                await RestartGuard.EnsureAllowedAsync(context, limits, CancellationToken.None);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })));

        Assert.Equal(3, attempts.Count(allowed => allowed));
    }

    [Fact]
    public async Task Refused_restarts_do_not_block_the_windows_after_them()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-05T10:00:00Z"));
        var collection = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(time);
        collection.AddHomeostat();
        await using var services = collection.BuildServiceProvider();
        time.Advance(TimeSpan.FromHours(1));
        var limits = new RestartLimits(TimeSpan.Zero, MaxRestarts: 3, TimeSpan.FromHours(1));

        async Task<int> AllowedOf(int attempts)
        {
            var context = new HealingContext("pool-exhausted", new Signal("pool", SignalSeverity.Unhealthy, time.GetUtcNow()), 0, "restart",
                time.GetUtcNow(), services, () => throw new NotSupportedException());
            var allowed = 0;
            for (var i = 0; i < attempts; i++)
            {
                try
                {
                    await RestartGuard.EnsureAllowedAsync(context, limits, CancellationToken.None);
                    allowed++;
                }
                catch (InvalidOperationException)
                {
                }
            }

            return allowed;
        }

        Assert.Equal(3, await AllowedOf(5));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await AllowedOf(5));   // the previous window already used the limit
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(3, await AllowedOf(5));   // the refused attempts of the last window do not count
    }

    [Fact]
    public async Task A_condition_that_cleared_and_counts_again_is_healed_for_the_verifier()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-05T10:00:00Z"));
        await using var services = new ServiceCollection().BuildServiceProvider();
        var context = new HealingContext("pool-exhausted", new Signal("pool", SignalSeverity.Unhealthy, time.GetUtcNow()), 0, "restart",
            time.GetUtcNow(), services, () => new ConditionSnapshot { Name = "pool-exhausted", Status = ConditionStatus.Pending });
        var verifier = new ConditionClearedVerifier(time, Options.Create(new HomeostatOptions()));

        // Pending after an action: the episode the action fixed is over (a new one has started counting).
        Assert.Equal(VerificationOutcome.Healed, await verifier.VerifyAsync(context, TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task A_hosted_service_is_not_started_again_while_the_application_stops()
    {
        var service = new CountingService();
        var lifetime = new StoppingLifetime();
        var collection = new ServiceCollection().AddLogging().AddSingleton<IHostApplicationLifetime>(lifetime);
        await using var services = collection.BuildServiceProvider();
        var action = new RestartHostedServiceAction<CountingService>([service], services, NullLogger<RestartHostedServiceAction<CountingService>>.Instance);
        var context = new HealingContext("worker-stuck", new Signal("worker", SignalSeverity.Unhealthy, DateTimeOffset.UtcNow), 0, "restart",
            DateTimeOffset.UtcNow, services, () => throw new NotSupportedException());

        await action.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(1, service.Stops);
        Assert.Equal(0, service.Starts);   // stopped by the restart, not started again: the host is stopping it

        lifetime.Running();
        await action.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(1, service.Starts);
    }

    [Fact]
    public async Task A_hosted_service_whose_stop_hangs_is_started_again()
    {
        var service = new CountingService { StopHangs = true };
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var action = new RestartHostedServiceAction<CountingService>([service], services, NullLogger<RestartHostedServiceAction<CountingService>>.Instance);
        var context = new HealingContext("worker-stuck", new Signal("worker", SignalSeverity.Unhealthy, DateTimeOffset.UtcNow), 0, "restart",
            DateTimeOffset.UtcNow, services, () => throw new NotSupportedException());

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));   // the step's timeout
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.ExecuteAsync(context, timeout.Token));
        Assert.Equal(1, service.Starts);
    }

    private sealed class CountingService : IHostedService
    {
        public int Starts;
        public int Stops;
        public bool StopHangs;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Stops);
            return StopHangs ? Task.Delay(TimeSpan.FromSeconds(60), CancellationToken.None) : Task.CompletedTask;   // ignores the token
        }
    }

    private sealed class StoppingLifetime : IHostApplicationLifetime
    {
        private CancellationTokenSource _stopping = new();

        public StoppingLifetime() => _stopping.Cancel();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Running() => _stopping = new CancellationTokenSource();

        public void StopApplication() => _stopping.Cancel();
    }

    // ---------------------------------------------------------------- abandoned calls

    [Fact]
    public async Task A_verifier_that_never_returns_is_not_started_again_while_it_runs()
    {
        var verifier = new CountingSilentVerifier();
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .Configure(o => o.DefaultCooldown = TimeSpan.FromSeconds(1))
                .AddCondition("feed-down", c => c.ForSource("feed")
                    .Heal(p => p.With("renew", _ => { }).VerificationTimeout(TimeSpan.FromSeconds(5)).UseVerifier<CountingSilentVerifier>())),
            services => services.AddSingleton(verifier));

        host.Publish("feed", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Inconclusive, (await host.WaitForRunAsync("feed-down")).Outcome);

        host.Time.Advance(TimeSpan.FromSeconds(2));
        host.Publish("feed", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Inconclusive, (await host.WaitForRunAsync("feed-down")).Outcome);

        Assert.Equal(1, verifier.Calls);
    }

    internal sealed class CountingSilentVerifier : IHealingVerifier
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return new TaskCompletionSource<VerificationOutcome>().Task;   // never returns, ignores its token
        }
    }

    [Fact]
    public async Task A_detector_that_hangs_is_not_called_again_until_it_returns()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.DetectionInterval = TimeSpan.FromSeconds(1))
            .AddDetector<HangingDetector>()
            .AddCondition("custom-down", c => c.ForSource("custom")));

        // Many detection intervals pass (simulated time advances by itself): the hung call is not started again.
        await PollyControlTests.WaitUntilAsync(() => HangingDetector.Calls == 1);
        var start = host.Time.GetUtcNow();
        await PollyControlTests.WaitUntilAsync(() => host.Time.GetUtcNow() - start > TimeSpan.FromSeconds(30));
        Assert.Equal(1, HangingDetector.Calls);
    }

    internal sealed class HangingDetector : ISignalDetector
    {
        private static int s_calls;   // created by the container: counted here (only one test uses it)

        public static int Calls => Volatile.Read(ref s_calls);

        public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref s_calls);
            return new ValueTask(new TaskCompletionSource().Task);   // never returns, ignores its token
        }
    }

    // ---------------------------------------------------------------- stores

    [Fact]
    public async Task Seasonal_baselines_do_not_hold_startup_or_shutdown_when_the_store_never_answers()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.AddHomeostat(h => h
            .UseStore(_ => new SilentDocuments())
            .AddCondition("latency-seasonal", c => c.ForSource("latency").WhenSeasonallyHigh(4, Seasonality.Daily())));
        using var host = builder.Build();

        var watch = Stopwatch.StartNew();
        await host.StartAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"startup took {watch.Elapsed}");

        watch.Restart();
        await host.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"shutdown took {watch.Elapsed}");
    }

    /// <summary>An in-memory store whose documents (seasonal baselines) never answer and ignore their token.</summary>
    private sealed class SilentDocuments : IHomeostatStore
    {
        public ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeGroupBudgetAsync(group, now, limit, window, cancellationToken);

        public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _inner.GetGroupBudgetUsedAsync(group, now, window, cancellationToken);

        public ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetGroupPauseAsync(group, pause, decidedAt, cancellationToken);

        private readonly InMemoryHomeostatStore _inner = new();

        public bool SupportsDocuments => true;

        public ValueTask<string?> GetDocumentAsync(string key, CancellationToken cancellationToken = default) => new(new TaskCompletionSource<string?>().Task);

        public ValueTask SetDocumentAsync(string key, string document, CancellationToken cancellationToken = default) => new(new TaskCompletionSource().Task);

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => _inner.GetPlanStateAsync(key, cancellationToken);

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => _inner.UpdatePlanStateAsync(key, update, cancellationToken);

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _inner.GetBudgetUsedAsync(now, window, cancellationToken);

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => _inner.AppendRunAsync(run, maxRuns, cancellationToken);

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => _inner.GetRecentRunsAsync(maxRuns, cancellationToken);

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => _inner.GetControlStateAsync(cancellationToken);

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetPauseAsync(pause, decidedAt, cancellationToken);

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => _inner.TryClaimSwitchAsync(state, cancellationToken);

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => _inner.AddApprovalAsync(approval, cancellationToken);

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) => _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => _inner.RemoveApprovalAsync(id, cancellationToken);
    }

    [Fact]
    public async Task A_store_file_locked_at_startup_is_opened_once_the_lock_is_released()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-{Guid.NewGuid():N}.json");
        try
        {
            var other = new FileHomeostatStore(path);   // holds the file
            await using var host = await HomeostatTestHost.StartAsync(h => h
                .UseFileStore(path)
                .AddCondition("feed-down", c => c.ForSource("feed")));
            Assert.True(host.Control.IsPaused);

            other.Dispose();

            // Tried again every 10 seconds (real time); the refresh then reads the state, and Homeostat no longer counts
            // as paused.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (host.Control.IsPaused)
            {
                await host.Services.GetRequiredService<Engine.SharedControl>().TryRefreshAsync(CancellationToken.None);
                await Task.Delay(500, limit.Token);
            }

            Assert.False(host.Control.IsPaused);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_change_the_store_could_not_save_does_not_keep_its_decision_time()
    {
        var store = new FailingSaves();
        var t0 = DateTimeOffset.Parse("2026-11-05T10:00:00Z");

        // A resume at t2 that cannot be saved, then the pause decided at t1 (deferred earlier) written again: the
        // failed resume must not make the store refuse it.
        store.Failing = true;
        await Assert.ThrowsAsync<IOException>(async () => await store.TrySetPauseAsync(null, t0.AddSeconds(2)));
        store.Failing = false;
        Assert.True(await store.TrySetPauseAsync(new PauseState { At = t0.AddSeconds(1) }, t0.AddSeconds(1)));

        store.Failing = true;
        await Assert.ThrowsAsync<IOException>(async () => await store.TrySetSwitchAsync("serve-cached", new SwitchState { Name = "serve-cached", At = t0.AddSeconds(4) }, t0.AddSeconds(4)));
        store.Failing = false;
        Assert.True(await store.TrySetSwitchAsync("serve-cached", null, t0.AddSeconds(3)));
    }

    /// <summary>A memory store whose saves can fail, as the file store's when the disk does.</summary>
    private sealed class FailingSaves : MemoryStore
    {
        public bool Failing { get; set; }

        protected override void OnChanged()
        {
            if (Failing)
            {
                throw new IOException("disk full");
            }
        }
    }
}
