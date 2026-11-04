using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

            // Instance a stopped (or crashed) with its condition still active: nobody would turn the switch off.
            await RefreshAsync(b);
            Assert.True(b.Switches.IsOn("serve-cached"));

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
