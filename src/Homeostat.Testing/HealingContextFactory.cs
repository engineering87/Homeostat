using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Testing;

/// <summary>Creates <see cref="HealingContext"/> instances for unit testing actions and verifiers.</summary>
public static class HealingContextFactory
{
    /// <param name="conditionName">Condition name.</param>
    /// <param name="triggeringSignal">Defaults to an unhealthy signal from source <c>"test"</c>.</param>
    /// <param name="services">Defaults to an empty service provider.</param>
    /// <param name="conditionState">What <see cref="HealingContext.GetConditionState"/> returns. Defaults to an active condition.</param>
    /// <param name="stepName">Step name.</param>
    /// <param name="step">Zero-based step index.</param>
    /// <param name="now">Start time of the step. Defaults to the current time.</param>
    public static HealingContext Create(
        string conditionName = "test-condition",
        Signal? triggeringSignal = null,
        IServiceProvider? services = null,
        ConditionSnapshot? conditionState = null,
        string stepName = "test-step",
        int step = 0,
        DateTimeOffset? now = null)
    {
        var startedAt = now ?? DateTimeOffset.UtcNow;
        var signal = triggeringSignal ?? new Signal("test", SignalSeverity.Unhealthy, startedAt);
        var state = conditionState
            ?? new ConditionSnapshot(conditionName, ConditionStatus.Active, startedAt, startedAt, signal.Timestamp, signal);

        return new HealingContext(
            conditionName,
            signal,
            step,
            stepName,
            startedAt,
            services ?? new ServiceCollection().BuildServiceProvider(),
            () => state);
    }
}
