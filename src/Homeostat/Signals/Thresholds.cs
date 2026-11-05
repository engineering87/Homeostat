namespace Homeostat.Signals;

/// <summary>
/// The rule that turns a value and its thresholds into a severity for metric signals (the presets built on them
/// included) and event counter signals; call signals compare the same way (at or above) in their own code. It is not
/// the rule of every built-in signal: probes, Kafka lag and the memory cache compare strictly (above, below), as each
/// documents (decision I4 in docs/decisions.md).
/// </summary>
internal static class Thresholds
{
    /// <summary>
    /// Unhealthy at or beyond an unhealthy threshold, Degraded at or beyond a degraded one, Healthy otherwise. A missing
    /// threshold never matches.
    /// </summary>
    public static SignalSeverity Severity(double value, double? degradedAbove, double? unhealthyAbove, double? degradedBelow = null, double? unhealthyBelow = null)
    {
        if (value >= unhealthyAbove || value <= unhealthyBelow)
        {
            return SignalSeverity.Unhealthy;
        }

        return value >= degradedAbove || value <= degradedBelow ? SignalSeverity.Degraded : SignalSeverity.Healthy;
    }
}
