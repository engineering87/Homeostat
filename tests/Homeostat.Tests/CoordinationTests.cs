using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

/// <summary>Approval, suppression, switch reset and the control API.</summary>
public class CoordinationTests
{
    private static Task<HomeostatTestHost> StartAsync(Action<HomeostatBuilder> configure)
        => HomeostatTestHost.StartAsync(configure, services => services
            .AddSingleton<EngineTests.Recorder>()
            .AddSingleton<TimerHolder>());

    private static int Executions(HomeostatTestHost host)
        => host.Services.GetRequiredService<EngineTests.Recorder>().Executions;

    // ---------------------------------------------------------------- approval

    [Fact]
    public async Task Approved_step_runs()
    {
        await using var host = await StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.With<EngineTests.RecordingAction>().RequireApproval().VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var request = await host.WaitForApprovalAsync("feed-down");

        Assert.Equal(0, Executions(host));
        Assert.Equal(nameof(EngineTests.RecordingAction), request.StepName);
        Assert.Equal(TimeSpan.FromMinutes(15), request.ExpiresAt - request.RequestedAt);
        Assert.True(host.Approvals.Approve(request.Id, "on-call"));

        var run = await host.WaitForRunAsync("feed-down");
        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal(1, Executions(host));
        Assert.Empty(host.Approvals.GetPending());
        Assert.False(host.Approvals.Approve(request.Id), "an answered request cannot be answered again");
    }

    [Fact]
    public async Task Rejected_step_stops_the_run_without_escalating()
    {
        await using var host = await StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p
                .With<EngineTests.RecordingAction>().RequireApproval()
                .Then<EngineTests.RecordingAction>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var request = await host.WaitForApprovalAsync("feed-down");
        Assert.True(host.Approvals.Reject(request.Id, "on-call"));

        var run = await host.WaitForRunAsync("feed-down");
        Assert.Equal(HealingRunOutcome.NotApproved, run.Outcome);
        Assert.Empty(run.Steps);
        Assert.Equal(0, Executions(host));
    }

    [Fact]
    public async Task Unanswered_approval_expires()
    {
        await using var host = await StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.With<EngineTests.RecordingAction>().RequireApproval(TimeSpan.FromMinutes(1)))));

        host.Publish("feed", SignalSeverity.Unhealthy);

        var run = await host.WaitForRunAsync("feed-down");
        Assert.Equal(HealingRunOutcome.NotApproved, run.Outcome);
        Assert.Equal(0, Executions(host));
        Assert.Empty(host.Approvals.GetPending());
    }

    [Fact]
    public async Task Approval_events_reach_observers()
    {
        var observer = new ApprovalObserver();
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .AddCondition("feed-down", c => c
                    .ForSource("feed")
                    .Heal(p => p.With("act", _ => { }).RequireApproval().VerifyWith<EngineTests.AlwaysHealedVerifier>()))
                .AddObserver<ApprovalObserver>(),
            services => services.AddSingleton(observer));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var request = await host.WaitForApprovalAsync("feed-down");
        host.Approvals.Approve(request.Id, "on-call");
        await host.WaitForRunAsync("feed-down");

        Assert.Equal(request, Assert.Single(observer.Requested));
        var resolution = Assert.Single(observer.Resolved);
        Assert.Equal(ApprovalStatus.Approved, resolution.Status);
        Assert.Equal("on-call", resolution.ResolvedBy);
    }

    [Fact]
    public void RequireApproval_needs_a_previous_step()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddHomeostat(h => h.AddCondition("x", c => c.ForSource("x").Heal(p => p.RequireApproval()))));
    }

    // ---------------------------------------------------------------- suppression

    [Fact]
    public async Task Suppressed_condition_is_not_healed_while_its_cause_is_active()
    {
        await using var host = await StartAsync(h => h
            .Configure(o => o.DefaultCooldown = TimeSpan.Zero)
            .AddCondition("network-down", c => c.ForSource("network"))
            .AddCondition("feed-down", c => c
                .ForSource("feed")
                .SuppressedBy("network-down")
                .Heal(p => p.With<EngineTests.RecordingAction>().VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("network", SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync("network-down", ConditionStatus.Active);
        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync("feed-down", ConditionStatus.Active);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Empty(host.CompletedRuns);
        Assert.Equal("network-down", host.Control.GetConditions().Single(c => c.Name == "feed-down").SuppressedBy);

        host.Publish("network", SignalSeverity.Healthy);
        await host.WaitForConditionAsync("network-down", ConditionStatus.Inactive);
        host.Publish("feed", SignalSeverity.Unhealthy);

        var run = await host.WaitForRunAsync("feed-down");
        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Null(host.Control.GetConditions().Single(c => c.Name == "feed-down").SuppressedBy);
    }

    [Fact]
    public async Task A_run_stops_when_its_cause_becomes_active()
    {
        await using var host = await StartAsync(h => h
            .AddCondition("network-down", c => c.ForSource("network"))
            .AddCondition("feed-down", c => c
                .ForSource("feed")
                .SuppressedBy("network-down")
                .Heal(p => p.With<EngineTests.RecordingAction>().RequireApproval())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var request = await host.WaitForApprovalAsync("feed-down");

        host.Publish("network", SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync("network-down", ConditionStatus.Active);
        host.Approvals.Approve(request.Id);

        var run = await host.WaitForRunAsync("feed-down");
        Assert.Equal(HealingRunOutcome.Suppressed, run.Outcome);
        Assert.Equal(0, Executions(host));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("cycle")]
    public async Task Invalid_suppression_fails_at_startup(string kind)
    {
        Action<HomeostatBuilder> configure = kind switch
        {
            "missing" => h => h.AddCondition("a", c => c.ForSource("a").SuppressedBy("nope")),
            "self" => h => h.AddCondition("a", c => c.ForSource("a").SuppressedBy("a")),
            _ => h => h
                .AddCondition("a", c => c.ForSource("a").SuppressedBy("b"))
                .AddCondition("b", c => c.ForSource("b").SuppressedBy("c"))
                .AddCondition("c", c => c.ForSource("c").SuppressedBy("a")),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => HomeostatTestHost.StartAsync(configure));
    }

    // ---------------------------------------------------------------- switches

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Switch_is_turned_off_on_clear_only_when_requested(bool turnOffWhenCleared)
    {
        await using var host = await StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.TurnOnSwitch("serve-cached", turnOffWhenCleared).VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");
        Assert.True(host.Switches.IsOn("serve-cached"));

        host.Publish("feed", SignalSeverity.Healthy);
        await host.WaitForConditionAsync("feed-down", ConditionStatus.Inactive);

        // The switch is reset just after the status changes: poll instead of sleeping a fixed time.
        await WaitUntilAsync(() => host.Switches.IsOn("serve-cached") == !turnOffWhenCleared, TimeSpan.FromMilliseconds(turnOffWhenCleared ? 5000 : 200));
        Assert.Equal(!turnOffWhenCleared, host.Switches.IsOn("serve-cached"));
    }

    [Fact]
    public async Task Switch_reset_leaves_switches_that_homeostat_did_not_turn_on()
    {
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("feed-down", c => c
                .ForSource("feed")
                .Heal(p => p.TurnOnSwitch("serve-cached", turnOffWhenCleared: true))),
            options: new HomeostatTestHostOptions { DryRun = true });

        host.Switches.TurnOn("serve-cached");   // by hand
        host.Publish("feed", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.DryRun, (await host.WaitForRunAsync("feed-down")).Outcome);
        host.Publish("feed", SignalSeverity.Healthy);
        await host.WaitForConditionAsync("feed-down", ConditionStatus.Inactive);
        await Task.Delay(100);

        Assert.True(host.Switches.IsOn("serve-cached"));
    }

    [Fact]
    public async Task Shared_switch_stays_on_until_the_last_condition_clears()
    {
        await using var host = await StartAsync(h => h
            .AddCondition("a", c => c.ForSource("a")
                .Heal(p => p.TurnOnSwitch("serve-cached", turnOffWhenCleared: true).VerifyWith<EngineTests.AlwaysHealedVerifier>()))
            .AddCondition("b", c => c.ForSource("b")
                .Heal(p => p.TurnOnSwitch("serve-cached", turnOffWhenCleared: true).VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("a", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("a");
        host.Publish("b", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("b");

        host.Publish("a", SignalSeverity.Healthy);
        await host.WaitForConditionAsync("a", ConditionStatus.Inactive);
        await Task.Delay(100);
        Assert.True(host.Switches.IsOn("serve-cached"), "b still needs it");

        host.Publish("b", SignalSeverity.Healthy);
        await WaitUntilAsync(() => !host.Switches.IsOn("serve-cached"), TimeSpan.FromSeconds(5));
        Assert.False(host.Switches.IsOn("serve-cached"));
    }

    // ---------------------------------------------------------------- review fixes

    [Fact]
    public async Task Approved_step_does_not_run_if_the_condition_cleared_meanwhile()
    {
        await using var host = await StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.With<EngineTests.RecordingAction>().RequireApproval())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var request = await host.WaitForApprovalAsync("feed-down");
        host.Publish("feed", SignalSeverity.Healthy);

        var run = await host.WaitForRunAsync("feed-down");
        Assert.Equal(HealingRunOutcome.ConditionCleared, run.Outcome);
        Assert.Equal(0, Executions(host));
        Assert.False(host.Approvals.Approve(request.Id), "the request was withdrawn when the condition cleared");
    }

    [Fact]
    public async Task Waiting_for_approval_does_not_block_other_plans()
    {
        await using var host = await StartAsync(h => h
            .AddCondition("risky", c => c.ForSource("risky").Heal(p => p.With("failover", _ => { }).RequireApproval()))
            .AddCondition("simple", c => c.ForSource("simple")
                .Heal(p => p.With<EngineTests.RecordingAction>().VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("risky", SignalSeverity.Unhealthy);
        await host.WaitForApprovalAsync("risky");
        host.Publish("simple", SignalSeverity.Unhealthy);

        var run = await host.WaitForRunAsync("simple");
        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Single(host.Approvals.GetPending());
    }

    [Fact]
    public async Task After_a_rejection_the_plan_waits_until_the_condition_clears()
    {
        await using var host = await StartAsync(h => h
            .Configure(o => o.DefaultCooldown = TimeSpan.Zero)
            .AddCondition("feed-down", c => c
                .ForSource("feed")
                .Heal(p => p.With<EngineTests.RecordingAction>().RequireApproval())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        host.Approvals.Reject((await host.WaitForApprovalAsync("feed-down")).Id, "on-call");
        Assert.Equal(HealingRunOutcome.NotApproved, (await host.WaitForRunAsync("feed-down")).Outcome);

        host.Publish("feed", SignalSeverity.Unhealthy);
        await Task.Delay(200);
        Assert.Empty(host.Approvals.GetPending());

        host.Publish("feed", SignalSeverity.Healthy);
        await host.WaitForConditionAsync("feed-down", ConditionStatus.Inactive);
        host.Publish("feed", SignalSeverity.Unhealthy);
        Assert.Equal("feed-down", (await host.WaitForApprovalAsync("feed-down")).ConditionName);
    }

    [Fact]
    public async Task Stopping_the_host_closes_pending_approvals()
    {
        var observer = new ApprovalObserver();
        var host = await HomeostatTestHost.StartAsync(
            h => h
                .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("act", _ => { }).RequireApproval()))
                .AddObserver<ApprovalObserver>(),
            services => services.AddSingleton(observer));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForApprovalAsync("feed-down");
        await host.DisposeAsync();

        Assert.Equal(ApprovalStatus.Cancelled, Assert.Single(observer.Resolved).Status);
    }

    [Fact]
    public async Task Verification_waits_for_ClearAfter_when_recovery_is_in_progress()
    {
        await using var host = await StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .ClearAfter(TimeSpan.FromMinutes(2))
            .Heal(p => p
                .With("renew", (context, _) =>
                {
                    // The fix works: from now on the feed reports healthy every 10 simulated seconds.
                    var publisher = context.Services.GetRequiredService<ISignalPublisher>();
                    var time = context.Services.GetRequiredService<TimeProvider>();
                    var timer = time.CreateTimer(_ => publisher.TryPublish("feed", SignalSeverity.Healthy), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
                    context.Services.GetRequiredService<TimerHolder>().Timer = timer;
                    return Task.CompletedTask;
                })
                .Then<EngineTests.RecordingAction>()
                .VerificationTimeout(TimeSpan.FromMinutes(1)))));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal("renew", Assert.Single(run.Steps).Name);
        Assert.Equal(0, Executions(host));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition() && !cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal sealed class TimerHolder : IDisposable
    {
        public ITimer? Timer { get; set; }

        public void Dispose() => Timer?.Dispose();
    }

    // ---------------------------------------------------------------- control and verifiers

    [Fact]
    public async Task Control_reports_recent_runs_and_budget()
    {
        await using var host = await StartAsync(h => h
            .Configure(o => o.MaxActionsPerWindow = 3)
            .AddCondition("feed-down", c => c
                .ForSource("feed")
                .Heal(p => p.With<EngineTests.RecordingAction>().VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        Assert.Empty(host.Control.GetRecentRuns());
        Assert.False(host.Control.IsDryRun);

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(run, Assert.Single(host.Control.GetRecentRuns()));
        var budget = host.Control.GetBudget();
        Assert.Equal(1, budget.Used);
        Assert.Equal(3, budget.Limit);
        Assert.Equal(2, budget.Remaining);
        Assert.False(budget.IsExhausted);
    }

    [Fact]
    public async Task A_throwing_verifier_makes_the_step_inconclusive()
    {
        await using var host = await StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.With("first", _ => { }).Then("second", _ => { }).VerifyWith<ThrowingVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(HealingRunOutcome.Inconclusive, run.Outcome);
        var step = Assert.Single(run.Steps);
        Assert.True(step.Succeeded);
        Assert.Equal(VerificationOutcome.Inconclusive, step.Verification);
        Assert.IsType<InvalidOperationException>(step.Error);
    }

    internal sealed class ThrowingVerifier : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Broken verifier.");
    }

    internal sealed class ApprovalObserver : IHealingObserver
    {
        private readonly object _gate = new();
        private readonly List<PendingApproval> _requested = [];
        private readonly List<ApprovalResolution> _resolved = [];

        public IReadOnlyList<PendingApproval> Requested { get { lock (_gate) { return _requested.ToArray(); } } }

        public IReadOnlyList<ApprovalResolution> Resolved { get { lock (_gate) { return _resolved.ToArray(); } } }

        public void OnApprovalRequested(PendingApproval approval) { lock (_gate) { _requested.Add(approval); } }

        public void OnApprovalResolved(ApprovalResolution resolution) { lock (_gate) { _resolved.Add(resolution); } }
    }
}
