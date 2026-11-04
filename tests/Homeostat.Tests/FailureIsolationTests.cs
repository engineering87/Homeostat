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
    private static Task<HomeostatTestHost> StartInstanceAsync(string instance, IHomeostatStore store, Action<HomeostatBuilder>? configure = null)
        => HomeostatTestHost.StartAsync(h =>
        {
            h.UseStore(_ => store).Configure(o =>
            {
                o.InstanceId = instance;
                o.ControlRefreshInterval = TimeSpan.FromSeconds(5);
            });
            configure?.Invoke(h);
        });

    private static Task RefreshAsync(HomeostatTestHost host)
        => host.Services.GetRequiredService<Engine.SharedControl>().TryRefreshAsync(CancellationToken.None);

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
    public async Task A_switch_turned_on_by_an_instance_that_is_gone_is_turned_off_by_another()
    {
        var store = new InMemoryHomeostatStore();
        static void Plan(HomeostatBuilder h) => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.TurnOnSwitch("serve-cached").VerifyWith<EngineTests.AlwaysHealedVerifier>().OncePerCluster()));

        var b = await StartInstanceAsync("b", store, Plan);
        await using (b)
        {
            await using (var a = await StartInstanceAsync("a", store, Plan))
            {
                a.Publish("feed", SignalSeverity.Unhealthy);
                await a.WaitForRunAsync("feed-down");
                Assert.Contains((await store.GetControlStateAsync()).Switches, s => s.Name == "serve-cached");
            }

            // Instance a stopped (or crashed) with its condition still active: nobody would turn the switch off. Instance
            // b does, once the switch is no longer renewed (at once if a marked it abandoned when it stopped).
            b.Time.Advance(TimeSpan.FromMinutes(2));
            await RefreshAsync(b);
            await ((HomeostatSwitches)b.Switches).MaintainOwnershipAsync(CancellationToken.None);

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
                .Heal(p => p.TurnOnSwitch("shed-maps").HoldAtMost(TimeSpan.FromMinutes(10)).VerifyWith<EngineTests.AlwaysHealedVerifier>())));

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

        public ValueTask SetPauseAsync(PauseState? pause, CancellationToken cancellationToken = default) => _inner.SetPauseAsync(pause, cancellationToken);

        public ValueTask SetSwitchAsync(string name, SwitchState? state, CancellationToken cancellationToken = default) => _inner.SetSwitchAsync(name, state, cancellationToken);

        public async ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default)
        {
            await Task.Run(() => Claims.Wait(TimeSpan.FromSeconds(20)), CancellationToken.None);   // ignores the token
            var claimed = await _inner.TryClaimSwitchAsync(state, CancellationToken.None);
            Claimed = true;
            return claimed;
        }

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, CancellationToken cancellationToken = default)
            => _inner.TryReleaseSwitchAsync(name, condition, instance, cancellationToken);

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

        public ValueTask SetPauseAsync(PauseState? pause, CancellationToken cancellationToken = default) { Block(); return _inner.SetPauseAsync(pause, cancellationToken); }

        public ValueTask SetSwitchAsync(string name, SwitchState? state, CancellationToken cancellationToken = default) { Block(); return _inner.SetSwitchAsync(name, state, cancellationToken); }

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) { Block(); return _inner.TryClaimSwitchAsync(state, cancellationToken); }

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, CancellationToken cancellationToken = default) { Block(); return _inner.TryReleaseSwitchAsync(name, condition, instance, cancellationToken); }

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) { Block(); return _inner.AddApprovalAsync(approval, cancellationToken); }

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) { Block(); return _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken); }

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) { Block(); return _inner.RemoveApprovalAsync(id, cancellationToken); }
    }

    /// <summary>An in-memory store whose control state can become unreachable, as during a network outage.</summary>
    private sealed class OutageStore : IHomeostatStore
    {
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

        public ValueTask SetPauseAsync(PauseState? pause, CancellationToken cancellationToken = default) { Check(); return _inner.SetPauseAsync(pause, cancellationToken); }

        public ValueTask SetSwitchAsync(string name, SwitchState? state, CancellationToken cancellationToken = default) { Check(); return _inner.SetSwitchAsync(name, state, cancellationToken); }

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) { Check(); return _inner.TryClaimSwitchAsync(state, cancellationToken); }

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, CancellationToken cancellationToken = default) { Check(); return _inner.TryReleaseSwitchAsync(name, condition, instance, cancellationToken); }

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) { Check(); return _inner.AddApprovalAsync(approval, cancellationToken); }

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) { Check(); return _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken); }

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) { Check(); return _inner.RemoveApprovalAsync(id, cancellationToken); }
    }
}
