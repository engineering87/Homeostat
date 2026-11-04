using Homeostat.Probes;

namespace Homeostat.EntityFrameworkCore;

/// <summary>Settings of <c>AddDbContextProbe</c>: the probe settings, in milliseconds of round trip, and the source.</summary>
public sealed class DbContextProbeOptions : LatencyProbeOptions
{
    /// <summary>Signal source. Defaults to <c>"efcore:{context type name}:ping"</c>.</summary>
    public string? Source { get; set; }
}
