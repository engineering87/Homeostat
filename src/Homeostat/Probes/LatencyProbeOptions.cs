namespace Homeostat.Probes;

/// <summary>
/// Settings of a probe that measures a round trip (<c>AddLatencyProbe</c>, the database, Redis and MongoDB pings).
/// The published value is in milliseconds; <see cref="DegradedLatency"/> and <see cref="UnhealthyLatency"/> set the
/// thresholds as durations, like the other latency settings of Homeostat.
/// </summary>
public class LatencyProbeOptions : ProbeOptions
{
    /// <summary>
    /// Round trip above which the signal is Degraded. The same threshold as <see cref="ProbeOptions.DegradedAbove"/>,
    /// as a duration (the value is in milliseconds).
    /// </summary>
    public TimeSpan? DegradedLatency
    {
        get => DegradedAbove is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        set => DegradedAbove = value?.TotalMilliseconds;
    }

    /// <summary>
    /// Round trip above which the signal is Unhealthy. The same threshold as <see cref="ProbeOptions.UnhealthyAbove"/>,
    /// as a duration (the value is in milliseconds).
    /// </summary>
    public TimeSpan? UnhealthyLatency
    {
        get => UnhealthyAbove is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        set => UnhealthyAbove = value?.TotalMilliseconds;
    }
}
