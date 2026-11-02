using Homeostat.Conditions;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class ConditionTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ConditionTracker Create(Action<ConditionBuilder> configure)
    {
        var builder = new ConditionBuilder("test", new ServiceCollection());
        configure(builder);
        return new ConditionTracker(builder.Build());
    }

    private static Signal Sig(string source, SignalSeverity severity, double seconds)
        => new(source, severity, T0.AddSeconds(seconds));

    [Fact]
    public void Activates_immediately_when_no_sustain_is_required()
    {
        var tracker = Create(c => c.ForSource("weather-feed"));

        Assert.Equal(ConditionTransition.Activated, tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 0)));
        Assert.Equal(ConditionStatus.Active, tracker.Snapshot().Status);
    }

    [Fact]
    public void Stays_pending_until_the_sustain_duration_has_elapsed()
    {
        var tracker = Create(c => c.ForSource("weather-feed").SustainedFor(TimeSpan.FromSeconds(30)));

        Assert.Equal(ConditionTransition.None, tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 0)));
        Assert.Equal(ConditionStatus.Pending, tracker.Snapshot().Status);

        Assert.Equal(ConditionTransition.None, tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 29)));
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 30)));
    }

    [Fact]
    public void A_healthy_signal_resets_a_pending_condition()
    {
        var tracker = Create(c => c.ForSource("weather-feed").SustainedFor(TimeSpan.FromSeconds(30)));

        tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 0));
        tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 10));
        tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 20));

        // The pending window restarted at second 20, so second 40 is not enough.
        Assert.Equal(ConditionTransition.None, tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 40)));
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 50)));
    }

    [Fact]
    public void Ignores_signals_from_other_sources()
    {
        var tracker = Create(c => c.ForSource("weather-feed"));

        Assert.Equal(ConditionTransition.None, tracker.Observe(Sig("air-quality", SignalSeverity.Unhealthy, 0)));
        Assert.Equal(ConditionStatus.Inactive, tracker.Snapshot().Status);
        Assert.Null(tracker.Snapshot().LastSignal);
    }

    [Fact]
    public void Reports_still_active_and_then_cleared()
    {
        var tracker = Create(c => c.ForSource("weather-feed"));

        tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 0));
        Assert.Equal(ConditionTransition.StillActive, tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 5)));
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 10)));
        Assert.Equal(ConditionStatus.Inactive, tracker.Snapshot().Status);
    }

    [Fact]
    public void Degraded_does_not_trigger_the_default_threshold()
    {
        var tracker = Create(c => c.ForSource("weather-feed"));

        Assert.Equal(ConditionTransition.None, tracker.Observe(Sig("weather-feed", SignalSeverity.Degraded, 0)));
        Assert.Equal(ConditionStatus.Inactive, tracker.Snapshot().Status);
    }

    [Fact]
    public void Build_requires_a_source_or_an_explicit_clear_rule()
    {
        var builder = new ConditionBuilder("unbounded", new ServiceCollection())
            .When(s => s.Severity == SignalSeverity.Unhealthy);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void ClearAfter_keeps_the_condition_active_until_recovery_has_lasted_long_enough()
    {
        var tracker = Create(c => c.ForSource("weather-feed").ClearAfter(TimeSpan.FromSeconds(30)));

        Assert.Equal(ConditionTransition.Activated, tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 0)));

        Assert.Equal(ConditionTransition.None, tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 10)));
        var snapshot = tracker.Snapshot();
        Assert.Equal(ConditionStatus.Active, snapshot.Status);
        Assert.Equal(T0.AddSeconds(10), snapshot.ClearingSince);

        Assert.Equal(ConditionTransition.None, tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 39)));
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 40)));
        Assert.Equal(ConditionStatus.Inactive, tracker.Snapshot().Status);
        Assert.Null(tracker.Snapshot().ClearingSince);
    }

    [Fact]
    public void ClearAfter_restarts_when_the_problem_comes_back()
    {
        var tracker = Create(c => c.ForSource("weather-feed").ClearAfter(TimeSpan.FromSeconds(30)));

        tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 0));
        tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 10));
        Assert.Equal(ConditionTransition.StillActive, tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 20)));
        Assert.Null(tracker.Snapshot().ClearingSince);

        Assert.Equal(ConditionTransition.None, tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 30)));
        Assert.Equal(ConditionTransition.None, tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 50)));
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 60)));
    }

    [Fact]
    public void ClearAfter_does_not_delay_resetting_a_pending_condition()
    {
        var tracker = Create(c => c.ForSource("weather-feed")
            .SustainedFor(TimeSpan.FromSeconds(30))
            .ClearAfter(TimeSpan.FromMinutes(5)));

        tracker.Observe(Sig("weather-feed", SignalSeverity.Unhealthy, 0));
        tracker.Observe(Sig("weather-feed", SignalSeverity.Healthy, 10));

        Assert.Equal(ConditionStatus.Inactive, tracker.Snapshot().Status);
    }

    [Fact]
    public void ClearAfter_rejects_negative_durations()
    {
        var builder = new ConditionBuilder("test", new ServiceCollection());
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.ClearAfter(TimeSpan.FromSeconds(-1)));
    }
}
