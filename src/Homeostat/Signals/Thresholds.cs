namespace Homeostat.Signals;

/// <summary>The one rule that turns a value and its thresholds into a severity, for every built-in signal.</summary>
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
