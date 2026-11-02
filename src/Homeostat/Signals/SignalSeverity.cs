namespace Homeostat.Signals;

/// <summary>
/// How bad an observation is. Values are ordered: a higher value means a worse state.
/// </summary>
public enum SignalSeverity
{
    /// <summary>Working normally.</summary>
    Healthy = 0,

    /// <summary>Working, but slower or with partial failures.</summary>
    Degraded = 1,

    /// <summary>Not working.</summary>
    Unhealthy = 2,
}
