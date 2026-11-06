using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class AdvancedConditionTests
{
    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ConditionTracker Create(Action<ConditionBuilder> configure)
    {
        var builder = new ConditionBuilder("test", new ServiceCollection());
        configure(builder);
        return new ConditionTracker(builder.Build());
    }

    private static Signal Value(double seconds, double value, SignalSeverity severity = SignalSeverity.Healthy)
        => new("memory", severity, T0.AddSeconds(seconds)) { Value = value };

    [Fact]
    public void Value_thresholds_trigger_on_the_signal_value()
    {
        var tracker = Create(c => c.ForSource("memory").WhenValueAbove(0.9));

        Assert.Equal(ConditionTransition.None, tracker.Observe(Value(0, 0.5)));
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(Value(10, 0.95)));
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(Value(20, 0.6)));
    }

    [Fact]
    public void Value_thresholds_are_not_cleared_by_a_signal_without_a_value()
    {
        var tracker = Create(c => c.ForSource("memory").WhenValueAbove(0.9));
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(Value(0, 0.95)));

        // A healthy signal that carries no value (a probe that could not measure) says nothing about the value.
        Assert.NotEqual(ConditionTransition.Cleared, tracker.Observe(new Signal("memory", SignalSeverity.Healthy, T0.AddSeconds(10))));
        Assert.Equal(ConditionStatus.Active, tracker.Snapshot().Status);
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(Value(20, 0.5)));
    }

    [Fact]
    public void Rising_trend_triggers_before_any_fixed_threshold()
    {
        // +2% per minute, sampled every 30 seconds, far below a 90% threshold.
        // A 4-minute window needs values spanning at least 2 minutes.
        var tracker = Create(c => c.ForSource("memory").WhenRisingFasterThan(0.01, TimeSpan.FromMinutes(4), minimumSamples: 3));

        var transitions = Enumerable.Range(0, 6)
            .Select(i => tracker.Observe(Value(i * 30, 0.30 + i * 0.01)))
            .ToArray();

        Assert.All(transitions.Take(4), t => Assert.Equal(ConditionTransition.None, t));   // less than 2 minutes of values
        Assert.Equal(ConditionTransition.Activated, transitions[4]);

        // Flat again: the slope over the window drops and the condition clears eventually.
        var cleared = Enumerable.Range(6, 40).Select(i => tracker.Observe(Value(i * 30, 0.35))).ToArray();
        Assert.Contains(ConditionTransition.Cleared, cleared);
    }

    [Fact]
    public void Falling_trend_is_the_mirror_rule()
    {
        var tracker = Create(c => c.ForSource("memory").WhenFallingFasterThan(10, TimeSpan.FromMinutes(4), minimumSamples: 3));

        tracker.Observe(Value(0, 1000));
        tracker.Observe(Value(60, 980));
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(Value(120, 900)));
    }

    [Fact]
    public void Trend_rules_ignore_signals_without_a_value()
    {
        var tracker = Create(c => c.ForSource("memory").WhenRisingFasterThan(0.01, TimeSpan.FromMinutes(4), minimumSamples: 3));
        for (var i = 0; i <= 4; i++)
        {
            tracker.Observe(Value(i * 30, 0.30 + i * 0.01));
        }

        Assert.Equal(ConditionStatus.Active, tracker.Snapshot().Status);

        // A signal without a value (a failed probe) neither triggers nor clears: the state stays as it is.
        Assert.NotEqual(ConditionTransition.Cleared, tracker.Observe(new Signal("memory", SignalSeverity.Unhealthy, T0.AddSeconds(150))));
        Assert.Equal(ConditionStatus.Active, tracker.Snapshot().Status);

        // A value that shows the rise has stopped clears it.
        Assert.Equal(ConditionTransition.StillActive, tracker.Observe(Value(180, 0.34)));
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(Value(210, 0.30)));
    }

    [Fact]
    public void Occurrences_clear_only_when_matches_leave_the_window()
    {
        var tracker = Create(c => c.ForSource("memory").Occurrences(2, TimeSpan.FromMinutes(5)));
        var bad = SignalSeverity.Unhealthy;
        var good = SignalSeverity.Healthy;

        tracker.Observe(new Signal("memory", bad, T0));
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(new Signal("memory", bad, T0.AddMinutes(1))));
        Assert.Equal(ConditionTransition.None, tracker.Observe(new Signal("memory", good, T0.AddMinutes(2))));
        Assert.Equal(ConditionStatus.Active, tracker.Snapshot().Status);

        // At minute 6.5 the first match has left the window; at 7 the second too.
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(new Signal("memory", good, T0.AddMinutes(6.5))));
    }

    [Fact]
    public void Composite_rules_cannot_be_mixed_with_signal_rules()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => new ConditionBuilder("a", services).WhenAllActive("b").ForSource("x"));
        Assert.Throws<InvalidOperationException>(() => new ConditionBuilder("a", services).ForSource("x").WhenAnyActive("b"));
    }

    [Fact]
    public void Anomaly_triggers_far_from_the_recent_baseline()
    {
        var tracker = Create(c => c.ForSource("memory").WhenAnomalouslyHigh(4, TimeSpan.FromHours(1), minimumHistory: 20));
        var random = new Random(42);

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(ConditionTransition.None, tracker.Observe(Value(i * 60, 100 + random.NextDouble() * 10)));
        }

        Assert.Equal(ConditionTransition.Activated, tracker.Observe(Value(31 * 60, 200)));
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(Value(32 * 60, 104)));
    }

    [Fact]
    public void Anomaly_needs_history()
    {
        var tracker = Create(c => c.ForSource("memory").WhenAnomalouslyHigh(3, TimeSpan.FromHours(1), minimumHistory: 10));

        tracker.Observe(Value(0, 1));
        Assert.Equal(ConditionTransition.None, tracker.Observe(Value(60, 1000)));
    }

    [Fact]
    public void Occurrences_count_intermittent_failures_within_a_window()
    {
        var tracker = Create(c => c.ForSource("memory").Occurrences(3, TimeSpan.FromMinutes(5)));
        var bad = SignalSeverity.Unhealthy;

        Assert.Equal(ConditionTransition.None, tracker.Observe(new Signal("memory", bad, T0)));
        Assert.Equal(ConditionTransition.None, tracker.Observe(new Signal("memory", SignalSeverity.Healthy, T0.AddMinutes(1))));
        Assert.Equal(ConditionTransition.None, tracker.Observe(new Signal("memory", bad, T0.AddMinutes(2))));
        Assert.Equal(ConditionTransition.None, tracker.Observe(new Signal("memory", SignalSeverity.Healthy, T0.AddMinutes(3))));
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(new Signal("memory", bad, T0.AddMinutes(4))));

        // Old matches leave the window.
        var other = Create(c => c.ForSource("memory").Occurrences(2, TimeSpan.FromMinutes(5)));
        other.Observe(new Signal("memory", bad, T0));
        Assert.Equal(ConditionTransition.None, other.Observe(new Signal("memory", bad, T0.AddMinutes(6))));
    }

    [Fact]
    public async Task Composite_conditions_follow_their_parts()
    {
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .AddCondition("db-slow", c => c.ForSource("db"))
                .AddCondition("cache-down", c => c.ForSource("cache"))
                .AddCondition("platform-degraded", c => c
                    .WhenAllActive("db-slow", "cache-down")
                    .Heal(p => p.With<EngineTests.RecordingAction>().UseVerifier<EngineTests.AlwaysHealedVerifier>()))
                .AddCondition("anything-down", c => c.WhenAnyActive("db-slow", "cache-down")),
            services => services.AddSingleton<EngineTests.Recorder>());

        host.Publish("db", SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync("anything-down", ConditionStatus.Active);
        Assert.Equal(ConditionStatus.Inactive, Status(host, "platform-degraded"));

        host.Publish("cache", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("platform-degraded");
        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);

        host.Publish("db", SignalSeverity.Healthy);
        await host.WaitForConditionAsync("platform-degraded", ConditionStatus.Inactive);
        Assert.Equal(ConditionStatus.Active, Status(host, "anything-down"));

        host.Publish("cache", SignalSeverity.Healthy);
        await host.WaitForConditionAsync("anything-down", ConditionStatus.Inactive);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("cycle")]
    public async Task Invalid_composites_fail_at_startup(string kind)
    {
        Action<HomeostatBuilder> configure = kind switch
        {
            "missing" => h => h.AddCondition("a", c => c.WhenAllActive("nope")),
            "self" => h => h.AddCondition("a", c => c.WhenAnyActive("a")),
            _ => h => h
                .AddCondition("a", c => c.WhenAnyActive("b"))
                .AddCondition("b", c => c.WhenAnyActive("a")),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => HomeostatTestHost.StartAsync(configure));
    }

    private static ConditionStatus Status(HomeostatTestHost host, string name)
        => host.Control.GetConditions().Single(c => c.Name == name).Status;
}
