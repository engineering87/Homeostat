using Homeostat.Healing;
using Homeostat.Polly;

namespace Homeostat;

/// <summary>Polly healing actions.</summary>
public static class HealingPlanBuilderPollyExtensions
{
    /// <summary>
    /// Appends a step that isolates the circuit breakers attached to the manual control <paramref name="control"/>
    /// (see <see cref="Polly.HomeostatPollyServices.CircuitControl"/>), so calls to a dependency known to be down fail
    /// fast instead of waiting for timeouts. It turns on the switch <c>circuit:{control}:isolated</c>.
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="control">Name of the manual control.</param>
    /// <param name="closeWhenCleared">
    /// When <c>true</c> (the default), the circuit breakers close again when the condition clears. Base the condition
    /// on a signal the isolation does not hide, such as a probe of the dependency or an external alert, not on the
    /// circuit breaker's own state.
    /// </param>
    /// <example>
    /// <code>
    /// h.AddLatencyProbe("forecast-provider", (sp, ct) => sp.GetRequiredService&lt;ForecastClient&gt;().PingAsync(ct))
    ///  .AddCondition("forecast-provider-down", c => c.ForSource("forecast-provider").SustainedFor(TimeSpan.FromSeconds(30))
    ///      .Heal(p => p.IsolateCircuit("forecast")));
    /// </code>
    /// </example>
    public static HealingPlanBuilder IsolateCircuit(this HealingPlanBuilder plan, string control, bool closeWhenCleared = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(control);
        return plan.TurnOnSwitch(CircuitControlRegistry.SwitchName(control), closeWhenCleared);
    }
}
