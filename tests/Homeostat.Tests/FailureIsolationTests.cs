using System.Diagnostics;
using Homeostat.Consumers;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homeostat.Tests;

/// <summary>
/// Homeostat must never leave the application degraded because Homeostat itself, or its store, failed: a switch it
/// turned on must come back off, a pause must take effect, a broken configuration must not stop the application.
/// </summary>
public class FailureIsolationTests
{
    private static Task<HomeostatTestHost> StartInstanceAsync(
        string instance, IHomeostatStore store, Action<HomeostatBuilder>? configure = null, HomeostatTestHostOptions? options = null)
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
            options: options);

    // A refresh joins a read already in flight, which the periodic refresh may have started before the change the test
    // made: a second refresh starts after it, so the view is at least as new as the call.
    private static async Task RefreshAsync(HomeostatTestHost host)
    {
        var control = host.Services.GetRequiredService<Engine.SharedControl>();
        await control.TryRefreshAsync(CancellationToken.None);
        await control.TryRefreshAsync(CancellationToken.None);
    }

    [Fact]
    public async Task With_the_store_down_turning_off_and_pausing_take_effect_at_once_and_are_written_when_it_recovers()
    {
        var store = new OutageStore();
        await using var host = await StartInstanceAsync("a", store);
        await host.Switches.TurnOnAsync("serve-cached", SwitchScope.Application, "on-call");
        Assert.True(host.Switches.IsOn("serve-cached"));

        store.Down = true;

        // Changes that make Homeostat do less never fail and apply here at once.
        await host.Switches.TurnOffAsync("serve-cached", "on-call");
        Assert.False(host.Switches.IsOn("serve-cached"));
        await host.Control.PauseAsync(by: "on-call");
        Assert.True(host.Control.IsPaused);

        // Changes that would let Homeostat act need the store: they fail instead of acting on a guess.
        await Assert.ThrowsAnyAsync<Exception>(async () => await host.Control.ResumeAsync());
        Assert.True(host.Control.IsPaused);

        // A failing refresh does not undo the local changes.
        await RefreshAsync(host);
        Assert.False(host.Switches.IsOn("serve-cached"));
        Assert.True(host.Control.IsPaused);

        store.Down = false;
        await RefreshAsync(host);
        var written = await store.GetControlStateAsync();
        Assert.NotNull(written.Pause);
        Assert.DoesNotContain(written.Switches, s => s.Name == "serve-cached");
        Assert.False(host.Switches.IsOn("serve-cached"));
        Assert.True(host.Control.IsPaused);
    }

    [Fact]
    public async Task A_release_deferred_during_an_outage_survives_the_failed_refreshes()
    {
        var store = new OutageStore();
        await using var host = await StartInstanceAsync("a", store, h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.TurnOnSwitch("serve-cached").UseVerifier<EngineTests.AlwaysHealedVerifier>().OncePerCluster())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");
        Assert.True(host.Switches.IsOn("serve-cached"));

        // The store goes down, then the condition clears: the release applies here and waits to be written.
        store.Down = true;
        host.Publish("feed", SignalSeverity.Healthy);
        await PollyControlTests.WaitUntilAsync(() => !host.Switches.IsOn("serve-cached"));
        await RefreshAsync(host);
        await RefreshAsync(host);

        store.Down = false;
        await RefreshAsync(host);
        Assert.False(host.Switches.IsOn("serve-cached"));
        Assert.DoesNotContain((await store.GetControlStateAsync()).Switches, s => s.Name == "serve-cached");
    }

    [Fact]
    public async Task A_pause_made_while_a_resume_is_being_written_stands()
    {
        var store = new SlowResumeStore();
        await using var host = await StartInstanceAsync("a", store);
        await host.Control.PauseAsync(by: "deploy");
        host.Time.Advance(TimeSpan.FromSeconds(1));

        // The resume reaches a slow store; meanwhile someone pauses again, later.
        var resuming = host.Control.ResumeAsync().AsTask();
        await PollyControlTests.WaitUntilAsync(() => store.ResumeWaiting);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.Control.PauseAsync(by: "on-call");
        store.Resumes.Set();
        await resuming;

        Assert.True(host.Control.IsPaused);
        await RefreshAsync(host);
        Assert.True(host.Control.IsPaused);
        Assert.Equal("on-call", (await store.GetControlStateAsync()).Pause?.By);
    }

    /// <summary>An in-memory store (with decision order) whose resumes wait until told to go on.</summary>
    private sealed class SlowResumeStore : IHomeostatStore
    {
        public ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeGroupBudgetAsync(group, now, limit, window, cancellationToken);

        public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _inner.GetGroupBudgetUsedAsync(group, now, window, cancellationToken);

        public ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetGroupPauseAsync(group, pause, decidedAt, cancellationToken);

        private readonly InMemoryHomeostatStore _inner = new();

        public ManualResetEventSlim Resumes { get; } = new(false);

        public volatile bool ResumeWaiting;

        public async ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        {
            if (pause is null)
            {
                ResumeWaiting = true;
                await Task.Run(() => Resumes.Wait(TimeSpan.FromSeconds(20)), CancellationToken.None);
            }

            return await _inner.TrySetPauseAsync(pause, decidedAt, CancellationToken.None);
        }

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => _inner.GetPlanStateAsync(key, cancellationToken);

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => _inner.UpdatePlanStateAsync(key, update, cancellationToken);

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _inner.GetBudgetUsedAsync(now, window, cancellationToken);

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => _inner.AppendRunAsync(run, maxRuns, cancellationToken);

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => _inner.GetRecentRunsAsync(maxRuns, cancellationToken);

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => _inner.GetControlStateAsync(cancellationToken);

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => _inner.TryClaimSwitchAsync(state, cancellationToken);

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => _inner.AddApprovalAsync(approval, cancellationToken);

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) => _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => _inner.RemoveApprovalAsync(id, cancellationToken);
    }

    [Fact]
    public async Task A_refused_claim_does_not_drop_a_pending_switch_off()
    {
        var store = new OutageStore();

        // A still clock: the periodic refresh does not run behind the test's back (it would write the pending off before
        // the claim, which the claim, decided later, would then rightly override).
        await using var host = await StartInstanceAsync("a", store, options: new HomeostatTestHostOptions { AutoAdvanceStep = null });
        await host.Switches.TurnOnAsync("serve-cached", SwitchScope.Application, "on-call");
        host.Time.Advance(TimeSpan.FromSeconds(1));

        // The person turns it off while the store is down: applied here, waiting to be written.
        store.Down = true;
        await host.Switches.TurnOffAsync("serve-cached", "on-call");
        Assert.False(host.Switches.IsOn("serve-cached"));

        // The store answers again, but before the next refresh a condition tries to claim the switch, which the store
        // still shows as the person's: refused. The pending off must stand.
        store.Down = false;
        await ((HomeostatSwitches)host.Switches).TurnOnForConditionAsync("serve-cached", "feed-down", CancellationToken.None);
        await RefreshAsync(host);

        Assert.False(host.Switches.IsOn("serve-cached"));
        Assert.DoesNotContain((await store.GetControlStateAsync()).Switches, s => s.Name == "serve-cached");
    }

    [Fact]
    public async Task A_switch_turned_on_by_an_instance_that_is_gone_is_turned_off_by_another()
    {
        var store = new InMemoryHomeostatStore();
        static void Plan(HomeostatBuilder h) => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.TurnOnSwitch("serve-cached").UseVerifier<EngineTests.AlwaysHealedVerifier>().OncePerCluster()));

        // Each test host has its own simulated clock: they move only when the test moves them, so the two instances agree
        // on the time, as synchronized clocks would (decisions about a switch are ordered by time).
        var still = new HomeostatTestHostOptions { AutoAdvanceStep = null };
        var b = await StartInstanceAsync("b", store, Plan, still);
        await using (b)
        {
            await using (var a = await StartInstanceAsync("a", store, Plan, still))
            {
                a.Publish("feed", SignalSeverity.Unhealthy);
                await a.WaitForRunAsync("feed-down");
                Assert.Contains((await store.GetControlStateAsync()).Switches, s => s.Name == "serve-cached");
            }

            // Instance a stopped (or crashed) with its condition still active: nobody would turn the switch off. Instance
            // b does, once the switch is no longer renewed (at once if a marked it abandoned when it stopped).
            b.Time.Advance(TimeSpan.FromMinutes(2));

            // Moving b's clock also wakes its periodic refresh, which sweeps on its own: whichever of the two releases the
            // switch, the other may find its write still running and leave it to the next refresh. Drive refreshes and
            // sweeps as the periodic loop would, until the store no longer has the switch.
            var sweeps = (HomeostatSwitches)b.Switches;
            for (var i = 0; i < 200 && (await store.GetControlStateAsync()).Switches.Any(s => s.Name == "serve-cached"); i++)
            {
                await RefreshAsync(b);
                await sweeps.MaintainOwnershipAsync(CancellationToken.None);
                await Task.Delay(10);
            }

            Assert.False(b.Switches.IsOn("serve-cached"));
            Assert.DoesNotContain((await store.GetControlStateAsync()).Switches, s => s.Name == "serve-cached");
        }
    }

    [Fact]
    public async Task HoldAtMost_turns_the_switch_off_even_if_the_condition_never_clears()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.TurnOnSwitch("shed-maps").HoldAtMost(TimeSpan.FromMinutes(10)).UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");
        Assert.True(host.Switches.IsOn("shed-maps"));

        await Task.Delay(100);   // lets the hold timer start before the clock jumps
        host.Time.Advance(TimeSpan.FromMinutes(11));
        await PollyControlTests.WaitUntilAsync(() => !host.Switches.IsOn("shed-maps"));
        Assert.False(host.Switches.IsOn("shed-maps"));
    }

    [Fact]
    public async Task An_action_whose_previous_call_is_still_running_is_not_started_again()
    {
        var calls = 0;
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o =>
            {
                o.DryRun = false;
                o.DefaultCooldown = TimeSpan.FromSeconds(1);
            })
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.With("stuck", (_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return new TaskCompletionSource().Task;   // never returns, ignores its token
                }).ActionTimeout(TimeSpan.FromSeconds(5)))));

        host.Publish("feed", SignalSeverity.Unhealthy);
        Assert.IsType<TimeoutException>(Assert.Single((await host.WaitForRunAsync("feed-down")).Steps).Error);

        host.Time.Advance(TimeSpan.FromSeconds(2));
        host.Publish("feed", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => host.CompletedRuns.Count >= 2);

        Assert.IsType<InvalidOperationException>(Assert.Single(host.CompletedRuns[1].Steps).Error);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task A_store_file_locked_by_another_process_does_not_stop_the_application()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-{Guid.NewGuid():N}.json");
        try
        {
            using var other = new FileHomeostatStore(path);   // holds the file
            await using var host = await HomeostatTestHost.StartAsync(h => h
                .UseFileStore(path)
                .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }))));

            // The application started; Homeostat fails closed until it can open its store.
            Assert.True(host.Control.IsPaused);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_invalid_condition_in_configuration_does_not_stop_the_application()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:feed-down:Source"] = "feed",
            ["Conditions:feed-down:Plan:Steps:0:Action"] = "no-such-action",
        }).Build();

        await using var host = await HomeostatTestHost.StartAsync(h => h.AddConditionsFromConfiguration(config.GetSection("Conditions")));

        Assert.Empty(host.Control.GetConditions());
        Assert.True(host.Publish("feed", SignalSeverity.Unhealthy));
    }

    [Fact]
    public async Task A_consumer_whose_pause_hangs_is_resumed_once_the_call_returns()
    {
        var consumer = new HangingConsumer("ingest");
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddPausableConsumer(_ => consumer));
        var consumers = host.Services.GetRequiredService<IHomeostatConsumers>();
        var paused = ConsumerSwitches.Paused("ingest");

        host.Switches.TurnOn(paused);
        await PollyControlTests.WaitUntilAsync(() => consumer.PauseCalls == 1);

        // The pause blocks past its timeout: the state is unknown. Then the problem clears.
        host.Time.Advance(HomeostatConsumers.CallTimeout + TimeSpan.FromSeconds(1));
        host.Switches.TurnOff(paused);

        consumer.Release();
        await PollyControlTests.WaitUntilAsync(() =>
        {
            host.Time.Advance(TimeSpan.FromSeconds(10));   // past the retry delays
            return consumer.ResumeCalls >= 1;
        });
        Assert.False(consumers.IsPaused("ingest"));
    }

    [Fact]
    public async Task The_health_check_answers_in_time_when_the_store_blocks_its_caller()
    {
        var store = new BlockingStore();
        try
        {
            await using var host = await HomeostatTestHost.StartAsync(
                h => h.UseStore(_ => store),
                services => services.AddHealthChecks().AddHomeostatCheck());

            var watch = Stopwatch.StartNew();
            var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"the health check took {watch.Elapsed}");
            Assert.NotEqual(HealthStatus.Unhealthy, report.Entries["homeostat"].Status);
        }
        finally
        {
            store.Unblock();
        }
    }

    [Fact]
    public async Task A_switch_claimed_after_its_step_timed_out_is_released()
    {
        var store = new SlowClaimStore();
        await using var host = await StartInstanceAsync("a", store, h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.TurnOnSwitch("serve-cached").ActionTimeout(TimeSpan.FromSeconds(1)).OncePerCluster())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");
        Assert.False(Assert.Single(run.Steps).Succeeded);

        // The store takes the claim only now, long after the step gave up: nobody owns it, so it must not stay on.
        store.Claims.Set();
        await PollyControlTests.WaitUntilAsync(() => store.Claimed);
        await PollyControlTests.WaitUntilAsync(() => !store.GetControlStateAsync().AsTask().Result.Switches.Any(s => s.Name == "serve-cached"));
    }

    /// <summary>A store that holds switch claims until told to take them, as a store that answers late.</summary>
    private sealed class SlowClaimStore : IHomeostatStore
    {
        public ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeGroupBudgetAsync(group, now, limit, window, cancellationToken);

        public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _inner.GetGroupBudgetUsedAsync(group, now, window, cancellationToken);

        public ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetGroupPauseAsync(group, pause, decidedAt, cancellationToken);

        private readonly InMemoryHomeostatStore _inner = new();

        public ManualResetEventSlim Claims { get; } = new(false);

        public volatile bool Claimed;

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => _inner.GetPlanStateAsync(key, cancellationToken);

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => _inner.UpdatePlanStateAsync(key, update, cancellationToken);

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _inner.GetBudgetUsedAsync(now, window, cancellationToken);

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => _inner.AppendRunAsync(run, maxRuns, cancellationToken);

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => _inner.GetRecentRunsAsync(maxRuns, cancellationToken);

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => _inner.GetControlStateAsync(cancellationToken);

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetPauseAsync(pause, decidedAt, cancellationToken);

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

        public async ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default)
        {
            await Task.Run(() => Claims.Wait(TimeSpan.FromSeconds(20)), CancellationToken.None);   // ignores the token
            var claimed = await _inner.TryClaimSwitchAsync(state, CancellationToken.None);
            Claimed = true;
            return claimed;
        }

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
            => _inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => _inner.AddApprovalAsync(approval, cancellationToken);

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default)
            => _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => _inner.RemoveApprovalAsync(id, cancellationToken);
    }

    private sealed class HangingConsumer(string name) : IPausableConsumer
    {
        private readonly ManualResetEventSlim _block = new(false);
        private int _pauses;
        private int _resumes;

        public string Name { get; } = name;

        public int PauseCalls => Volatile.Read(ref _pauses);

        public int ResumeCalls => Volatile.Read(ref _resumes);

        public void Release() => _block.Set();

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            // Blocks before returning its task, and ignores the token: the worst a consumer can do.
            if (Interlocked.Increment(ref _pauses) == 1)
            {
                _block.Wait(TimeSpan.FromSeconds(30));
            }

            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resumes);
            return Task.CompletedTask;
        }
    }

    /// <summary>A store whose reads block the calling thread (a synchronous client behind an async signature).</summary>
    private sealed class BlockingStore : IHomeostatStore
    {
        public ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) { Block(); return _inner.TryConsumeGroupBudgetAsync(group, now, limit, window, cancellationToken); }

        public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) { Block(); return _inner.GetGroupBudgetUsedAsync(group, now, window, cancellationToken); }

        public ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Block(); return _inner.TrySetGroupPauseAsync(group, pause, decidedAt, cancellationToken); }

        private readonly InMemoryHomeostatStore _inner = new();
        private readonly ManualResetEventSlim _gate = new(false);

        public void Unblock() => _gate.Set();

        private void Block() => _gate.Wait(TimeSpan.FromSeconds(30));

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) { Block(); return _inner.GetPlanStateAsync(key, cancellationToken); }

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) { Block(); return _inner.UpdatePlanStateAsync(key, update, cancellationToken); }

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) { Block(); return _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken); }

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) { Block(); return _inner.GetBudgetUsedAsync(now, window, cancellationToken); }

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) { Block(); return _inner.AppendRunAsync(run, maxRuns, cancellationToken); }

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) { Block(); return _inner.GetRecentRunsAsync(maxRuns, cancellationToken); }

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) { Block(); return _inner.GetControlStateAsync(cancellationToken); }

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Block(); return _inner.TrySetPauseAsync(pause, decidedAt, cancellationToken); }

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Block(); return _inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken); }

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) { Block(); return _inner.TryClaimSwitchAsync(state, cancellationToken); }

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Block(); return _inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken); }

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) { Block(); return _inner.AddApprovalAsync(approval, cancellationToken); }

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) { Block(); return _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken); }

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) { Block(); return _inner.RemoveApprovalAsync(id, cancellationToken); }
    }

    /// <summary>An in-memory store whose control state can become unreachable, as during a network outage.</summary>
    private sealed class OutageStore : IHomeostatStore
    {
        public ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return _inner.TryConsumeGroupBudgetAsync(group, now, limit, window, cancellationToken); }

        public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return _inner.GetGroupBudgetUsedAsync(group, now, window, cancellationToken); }

        public ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return _inner.TrySetGroupPauseAsync(group, pause, decidedAt, cancellationToken); }

        private readonly InMemoryHomeostatStore _inner = new();

        public volatile bool Down;

        private void Check()
        {
            if (Down)
            {
                throw new IOException("store down");
            }
        }

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) { Check(); return _inner.GetPlanStateAsync(key, cancellationToken); }

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) { Check(); return _inner.UpdatePlanStateAsync(key, update, cancellationToken); }

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken); }

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return _inner.GetBudgetUsedAsync(now, window, cancellationToken); }

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) { Check(); return _inner.AppendRunAsync(run, maxRuns, cancellationToken); }

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) { Check(); return _inner.GetRecentRunsAsync(maxRuns, cancellationToken); }

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) { Check(); return _inner.GetControlStateAsync(cancellationToken); }

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return _inner.TrySetPauseAsync(pause, decidedAt, cancellationToken); }

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return _inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken); }

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) { Check(); return _inner.TryClaimSwitchAsync(state, cancellationToken); }

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return _inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken); }

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) { Check(); return _inner.AddApprovalAsync(approval, cancellationToken); }

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) { Check(); return _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken); }

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) { Check(); return _inner.RemoveApprovalAsync(id, cancellationToken); }
    }
}
