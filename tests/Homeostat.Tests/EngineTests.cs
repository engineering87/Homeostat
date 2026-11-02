using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class EngineTests
{
    private const string Condition = "feed-down";
    private const string Source = "feed";

    private static Task<HomeostatTestHost> StartAsync(Action<HealingPlanBuilder> plan, bool dryRun = false)
        => HomeostatTestHost.StartAsync(
            h => h.AddCondition(Condition, c => c.ForSource(Source).Heal(plan)),
            services => services.AddSingleton<Recorder>(),
            new HomeostatTestHostOptions { DryRun = dryRun });

    [Fact]
    public async Task Runs_the_plan_when_a_condition_activates()
    {
        await using var host = await StartAsync(p => p.With<RecordingAction>().VerifyWith<AlwaysHealedVerifier>());

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal(1, host.Services.GetRequiredService<Recorder>().Executions);
        var step = Assert.Single(run.Steps);
        Assert.Equal(nameof(RecordingAction), step.Name);
        Assert.Equal(VerificationOutcome.Healed, step.Verification);
    }

    [Fact]
    public async Task Escalates_when_an_action_throws()
    {
        await using var host = await StartAsync(p => p
            .With<FailingAction>()
            .Then<RecordingAction>()
            .VerifyWith<AlwaysHealedVerifier>());

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Collection(
            run.Steps,
            s => { Assert.False(s.Succeeded); Assert.IsType<InvalidOperationException>(s.Error); Assert.Null(s.Verification); },
            s => { Assert.True(s.Succeeded); Assert.Equal(VerificationOutcome.Healed, s.Verification); });
    }

    [Fact]
    public async Task Escalates_when_verification_says_not_healed()
    {
        await using var host = await StartAsync(p => p
            .With("first", _ => { })
            .Then("second", _ => { })
            .VerifyWith<HealedOnSecondStepVerifier>());

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal(
            new VerificationOutcome?[] { VerificationOutcome.NotHealed, VerificationOutcome.Healed },
            run.Steps.Select(s => s.Verification));
    }

    [Fact]
    public async Task Default_verifier_reports_healed_when_the_condition_clears()
    {
        await using var host = await StartAsync(p => p.With("publish-recovery", (context, _) =>
        {
            context.Services.GetRequiredService<ISignalPublisher>().TryPublish(Source, SignalSeverity.Healthy);
            return Task.CompletedTask;
        }));

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        await host.WaitForConditionAsync(Condition, ConditionStatus.Inactive);
    }

    [Fact]
    public async Task Default_verifier_is_inconclusive_without_new_signals_and_does_not_escalate()
    {
        await using var host = await StartAsync(p => p
            .With("first", _ => { })
            .Then("second", _ => { }));

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.Equal(HealingRunOutcome.Inconclusive, run.Outcome);
        var step = Assert.Single(run.Steps);
        Assert.Equal("first", step.Name);
    }

    [Fact]
    public async Task Dry_run_executes_nothing()
    {
        await using var host = await StartAsync(p => p.With<RecordingAction>(), dryRun: true);

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.Equal(HealingRunOutcome.DryRun, run.Outcome);
        Assert.Empty(run.Steps);
        Assert.Equal(0, host.Services.GetRequiredService<Recorder>().Executions);
    }

    [Fact]
    public async Task Paused_host_does_not_start_runs()
    {
        await using var host = await StartAsync(p => p.With<RecordingAction>().VerifyWith<AlwaysHealedVerifier>());
        host.Control.Pause();

        host.Publish(Source, SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync(Condition, ConditionStatus.Active);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Empty(host.CompletedRuns);
        Assert.Equal(0, host.Services.GetRequiredService<Recorder>().Executions);
    }

    internal sealed class Recorder
    {
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public void Executed() => Interlocked.Increment(ref _executions);
    }

    internal sealed class RecordingAction(Recorder recorder) : IHealingAction
    {
        public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
        {
            recorder.Executed();
            return Task.CompletedTask;
        }
    }

    internal sealed class FailingAction : IHealingAction
    {
        public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated failure.");
    }

    internal sealed class AlwaysHealedVerifier : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(VerificationOutcome.Healed);
    }

    internal sealed class HealedOnSecondStepVerifier : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(context.Step == 0 ? VerificationOutcome.NotHealed : VerificationOutcome.Healed);
    }
}
