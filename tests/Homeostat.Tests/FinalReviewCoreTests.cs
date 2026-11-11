using System.Diagnostics;
using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

/// <summary>Findings of the final review of the core package.</summary>
public class FinalReviewCoreTests
{
    private static Task<HomeostatTestHost> StartInstanceAsync(string instance, IHomeostatStore store, Action<HomeostatBuilder>? configure = null)
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
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

    private static async Task RefreshAsync(HomeostatTestHost host)
    {
        var control = host.Services.GetRequiredService<Engine.SharedControl>();
        await control.TryRefreshAsync(CancellationToken.None);
        await control.TryRefreshAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- 1: timed-out unsafe writes on a stale view

    [Fact]
    public async Task A_resume_that_times_out_pauses_again_even_when_this_instance_did_not_show_the_pause()
    {
        var store = new GateStore();
        await using var host = await StartInstanceAsync("b", store);
        await PollyControlTests.WaitUntilAsync(() => !host.Control.IsPaused);

        // Paused on another instance: this one has not read it yet.
        await store.Inner.TrySetPauseAsync(new PauseState { By = "deploy", At = host.Time.GetUtcNow() }, host.Time.GetUtcNow());
        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(host.Control.IsPaused);

        // The resume reaches a slow store and the caller gives up (as at the write timeout).
        store.HoldUnsafeWrites = true;
        using var giveUp = new CancellationTokenSource();
        var resuming = host.Control.ResumeAsync("on-call", giveUp.Token).AsTask();
        await PollyControlTests.WaitUntilAsync(() => store.UnsafeWriteWaiting);
        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resuming);

        // The store then runs the late resume: the pause decided again after it stands, as the caller was told.
        store.Release.Set();
        await store.UnsafeWriteDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull((await store.GetControlStateAsync()).Pause);
        Assert.True(host.Control.IsPaused);
    }

    [Fact]
    public async Task A_turn_on_that_times_out_turns_the_switch_off_again_even_when_this_instance_showed_it_on()
    {
        var store = new GateStore();
        await using var host = await StartInstanceAsync("b", store);
        await host.Switches.TurnOnAsync("serve-cached", SwitchScope.Application, "on-call");
        host.Time.Advance(TimeSpan.FromSeconds(1));

        // Turned off on another instance: this one still shows it on.
        await store.Inner.TrySetSwitchAsync("serve-cached", null, host.Time.GetUtcNow());
        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(host.Switches.IsOn("serve-cached"));

        store.HoldUnsafeWrites = true;
        using var giveUp = new CancellationTokenSource();
        var turning = host.Switches.TurnOnAsync("serve-cached", SwitchScope.Application, "on-call", giveUp.Token).AsTask();
        await PollyControlTests.WaitUntilAsync(() => store.UnsafeWriteWaiting);
        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turning);

        store.Release.Set();
        await store.UnsafeWriteDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain((await store.GetControlStateAsync()).Switches, s => s.Name == "serve-cached");
        Assert.False(host.Switches.IsOn("serve-cached"));
    }

    // ---------------------------------------------------------------- 2: window bound with past timestamps

    [Fact]
    public void A_window_stays_bounded_when_a_lagging_source_interleaves_past_timestamps()
    {
        var window = TimeSpan.FromMinutes(30);
        var series = new SignalSeries(window);
        var start = DateTimeOffset.Parse("2020-03-01T10:00:00Z");
        for (var i = 0; i < 40_000; i++)
        {
            var at = start.AddMilliseconds(10 * i);
            series.Add(new Signal("latency", SignalSeverity.Healthy, at) { Value = 1 });
            series.Add(new Signal("latency", SignalSeverity.Healthy, at.AddSeconds(-5)) { Value = 2 });   // a source 5 s behind
        }

        Assert.True(series.Count <= SignalSeries.HardLimit, $"{series.Count} points kept");
        Assert.True(series.Count <= SignalSeries.MaxPoints * 1.07, $"{series.Count} points kept");
        Assert.Equal(2, series.Latest);
    }

    // ---------------------------------------------------------------- 3: no escalation over a running abandoned action

    [Fact]
    public async Task A_run_whose_undo_is_refused_because_the_action_still_runs_does_not_escalate()
    {
        var next = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .Configure(o => o.DryRun = false)
                .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                    .With("switch-to-backup", (_, _) =>
                    {
                        started.TrySetResult();
                        return new TaskCompletionSource().Task;   // never ends, ignores its token
                    })
                    .ActionTimeout(TimeSpan.FromMinutes(1))
                    .UndoWith("switch-to-primary", (_, _) => Task.CompletedTask)
                    .Then("restart-feed", (_, _) =>
                    {
                        Interlocked.Increment(ref next);
                        return Task.CompletedTask;
                    }))),
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

        host.Publish("feed", SignalSeverity.Unhealthy);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var running = host.WaitForRunAsync("feed-down", TimeSpan.FromSeconds(20));
        while (!running.IsCompleted)
        {
            host.Time.Advance(TimeSpan.FromMinutes(1));
            await Task.WhenAny(running, Task.Delay(200));
        }

        var run = await running;
        Assert.Equal(HealingRunOutcome.Failed, run.Outcome);
        var step = Assert.Single(run.Steps);
        Assert.False(step.UndoSucceeded);
        Assert.Equal(0, Volatile.Read(ref next));
    }

    // ---------------------------------------------------------------- 4: final baseline save follows the host

    [Fact]
    public async Task The_final_baseline_save_ends_with_the_hosts_shutdown_token()
    {
        var store = new GateStore { Documents = true, SetDocument = () => new TaskCompletionSource().Task };
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .UseStore(_ => store)
                .AddCondition("latency-seasonal", c => c.ForSource("latency").WhenSeasonallyHigh(4, Seasonality.Daily())),
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

        var keeper = host.Services.GetRequiredService<SeasonalBaselineKeeper>();
        await keeper.Ready.WaitAsync(TimeSpan.FromSeconds(20));
        for (var i = 0; i < 10; i++)
        {
            host.Signals.TryPublish("latency", SignalSeverity.Healthy, null, 10 + i);
        }

        var conditions = host.Services.GetRequiredService<ConditionSet>();
        await PollyControlTests.WaitUntilAsync(() => conditions.Trackers.SelectMany(t => t.Definition.Baselines).Any(b => b.Changes > 0));

        // The host's shutdown timeout is over almost at once: the hanging save must not hold the shutdown for StoreWait.
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        await keeper.StopAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"stopping took {watch.Elapsed}");
        Assert.Equal(1, store.SetDocumentCalls);
    }

    // ---------------------------------------------------------------- 5: answers to withdrawn requests

    [Fact]
    public async Task An_approval_withdrawn_while_the_store_failed_cannot_be_answered_afterwards()
    {
        var store = new GateStore();
        await using var host = await StartInstanceAsync("a", store, h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.With("switch-to-backup", (_, _) => Task.CompletedTask).RequireApproval(TimeSpan.FromHours(1)))));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var request = await host.WaitForApprovalAsync("feed-down");

        // The condition clears while the store cannot record the withdrawal: it stays pending there.
        store.FailAnswers = true;
        var running = host.WaitForRunAsync("feed-down", TimeSpan.FromSeconds(20));
        host.Publish("feed", SignalSeverity.Healthy);
        await running;
        store.FailAnswers = false;
        Assert.Contains(await host.Approvals.GetPendingAsync(), r => r.Id == request.Id);

        // Nobody waits for it any more: approving it would run nothing, so it is refused.
        Assert.False(await host.Approvals.ApproveAsync(request.Id, "on-call"));
    }

    // ---------------------------------------------------------------- 6: no cooldown for a run that did nothing

    [Fact]
    public async Task A_run_stopped_before_its_first_step_does_not_start_the_cooldown()
    {
        var store = new GateStore();
        HomeostatTestHost? self = null;
        var pauseOnce = 1;
        store.OnRecentRuns = async () =>
        {
            // Paused between the start of the run and its first step.
            if (Interlocked.Exchange(ref pauseOnce, 0) == 1)
            {
                await self!.Control.PauseAsync(by: "on-call");
            }
        };

        await using var host = await StartInstanceAsync("a", store, h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.With("switch-to-backup", (_, _) => Task.CompletedTask)
                    .UseVerifier<EngineTests.AlwaysHealedVerifier>()
                    .Adaptive()
                    .Cooldown(TimeSpan.FromHours(1)))));
        self = host;
        await PollyControlTests.WaitUntilAsync(() => !host.Control.IsPaused);

        var first = host.WaitForRunAsync("feed-down", TimeSpan.FromSeconds(20));
        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await first;
        Assert.Equal(HealingRunOutcome.Paused, run.Outcome);
        Assert.Empty(run.Steps);
        Assert.Null((await store.GetPlanStateAsync("a/feed-down")).LastRunEndedAt);

        // Resumed (decided after the pause): the next triggering signal starts the plan at once, not an hour later.
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.Control.ResumeAsync("on-call");
        Assert.False(host.Control.IsPaused);
        host.Publish("feed", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => host.CompletedRuns.Count >= 2);
        Assert.Equal(HealingRunOutcome.Healed, host.CompletedRuns[1].Outcome);
    }

    // ---------------------------------------------------------------- 7: HoldAtMost of an owner that is gone

    [Fact]
    public async Task HoldAtMost_is_enforced_by_another_instance_when_the_owner_is_gone_and_the_condition_stays_active()
    {
        var store = new InMemoryHomeostatStore();
        await using var b = await StartInstanceAsync("b", store, h => h
            .Configure(o => o.DryRun = true)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.TurnOnSwitch("serve-cached").HoldAtMost(TimeSpan.FromMinutes(10)).OncePerCluster())));

        // Instance a claimed the switch for the condition, then crashed: its HoldAtMost timer is gone with it.
        var claimedAt = b.Time.GetUtcNow();
        Assert.True(await store.TryClaimSwitchAsync(new SwitchState
        {
            Name = "serve-cached", Condition = "feed-down", Instance = "a", At = claimedAt, RenewedAt = claimedAt,
            RenewInterval = TimeSpan.FromSeconds(15),
        }));
        b.Publish("feed", SignalSeverity.Unhealthy);
        await b.WaitForConditionAsync("feed-down", ConditionStatus.Active);

        var sweeps = (HomeostatSwitches)b.Switches;

        // Gone, but within its limit, with the condition active here: it stays on.
        b.Time.Advance(TimeSpan.FromMinutes(2));
        await RefreshAsync(b);
        await sweeps.MaintainOwnershipAsync(CancellationToken.None);
        Assert.Contains((await store.GetControlStateAsync()).Switches, s => s.Name == "serve-cached");

        // Past its limit: turned off although the condition is still active.
        b.Time.Advance(TimeSpan.FromMinutes(9));
        b.Publish("feed", SignalSeverity.Unhealthy);
        Assert.Equal(ConditionStatus.Active, b.Control.GetConditions().Single(c => c.Name == "feed-down").Status);
        for (var i = 0; i < 200 && (await store.GetControlStateAsync()).Switches.Any(s => s.Name == "serve-cached"); i++)
        {
            await RefreshAsync(b);
            await sweeps.MaintainOwnershipAsync(CancellationToken.None);
            await Task.Delay(10);
        }

        Assert.DoesNotContain((await store.GetControlStateAsync()).Switches, s => s.Name == "serve-cached");
        Assert.False(b.Switches.IsOn("serve-cached"));
    }

    // ---------------------------------------------------------------- 8: run records without steps

    [Fact]
    public async Task A_run_record_without_steps_does_not_break_adaptive_plans_nor_the_coverage_report()
    {
        var store = new InMemoryHomeostatStore();
        var at = DateTimeOffset.Parse("2020-03-01T09:00:00Z");
        await store.AppendRunAsync(
            new HealingRunRecord { ConditionName = "feed-down", Outcome = HealingRunOutcome.NotHealed, StartedAt = at, CompletedAt = at, Steps = null! },
            50);

        await using var host = await StartInstanceAsync("a", store, h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.With("switch-to-backup", (_, _) => Task.CompletedTask)
                    .UseVerifier<EngineTests.AlwaysHealedVerifier>()
                    .Adaptive(minimumAttempts: 1))));
        await PollyControlTests.WaitUntilAsync(() => !host.Control.IsPaused);

        var running = host.WaitForRunAsync("feed-down", TimeSpan.FromSeconds(20));
        host.Publish("feed", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await running).Outcome);

        var coverage = await host.Control.GetCoverageAsync(at.AddHours(-1));
        Assert.Contains("switch-to-backup", coverage.Conditions.Single(c => c.Name == "feed-down").StepsRun);
    }

    /// <summary>An in-memory store (with decision order) with hooks: held unsafe writes, failing answers, documents.</summary>
    private sealed class GateStore : IHomeostatStore
    {
        public InMemoryHomeostatStore Inner { get; } = new();

        public volatile bool HoldUnsafeWrites;

        public volatile bool UnsafeWriteWaiting;

        public volatile bool FailAnswers;

        public ManualResetEventSlim Release { get; } = new(false);

        public TaskCompletionSource UnsafeWriteDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<Task>? OnRecentRuns { get; set; }

        public bool Documents { get; init; }

        public Func<Task>? SetDocument { get; init; }

        private int _setDocumentCalls;

        public int SetDocumentCalls => Volatile.Read(ref _setDocumentCalls);

        private async ValueTask<bool> UnsafeAsync(Func<ValueTask<bool>> write)
        {
            if (!HoldUnsafeWrites)
            {
                return await write();
            }

            UnsafeWriteWaiting = true;
            await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(20)), CancellationToken.None);
            try
            {
                return await write();
            }
            finally
            {
                UnsafeWriteDone.TrySetResult();
            }
        }

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
            => pause is null ? UnsafeAsync(() => Inner.TrySetPauseAsync(null, decidedAt, CancellationToken.None)) : Inner.TrySetPauseAsync(pause, decidedAt, cancellationToken);

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
            => state is not null ? UnsafeAsync(() => Inner.TrySetSwitchAsync(name, state, decidedAt, CancellationToken.None)) : Inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default)
            => FailAnswers ? throw new InvalidOperationException("store down") : Inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

        public async ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default)
        {
            if (OnRecentRuns is { } hook)
            {
                await hook();
            }

            return await Inner.GetRecentRunsAsync(maxRuns, cancellationToken);
        }

        public bool SupportsDocuments => Documents;

        public ValueTask<string?> GetDocumentAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);

        public ValueTask SetDocumentAsync(string key, string document, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _setDocumentCalls);
            return SetDocument is { } set ? new ValueTask(set()) : ValueTask.CompletedTask;
        }

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => Inner.GetPlanStateAsync(key, cancellationToken);

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => Inner.UpdatePlanStateAsync(key, update, cancellationToken);

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => Inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => Inner.GetBudgetUsedAsync(now, window, cancellationToken);

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => Inner.AppendRunAsync(run, maxRuns, cancellationToken);

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => Inner.GetControlStateAsync(cancellationToken);

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => Inner.TryClaimSwitchAsync(state, cancellationToken);

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => Inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => Inner.AddApprovalAsync(approval, cancellationToken);

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default)
            => FailAnswers ? throw new InvalidOperationException("store down") : Inner.RemoveApprovalAsync(id, cancellationToken);
    }
}
