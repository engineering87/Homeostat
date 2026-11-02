using Homeostat.Probes;

namespace Homeostat.Data;

/// <summary>
/// Settings of a database probe registered with <c>AddDatabaseProbe</c>. Thresholds are in milliseconds of round trip:
/// opening the connection (from the pool, when pooling is on) and running <see cref="CommandText"/>.
/// </summary>
public sealed class DatabaseProbeOptions : ProbeOptions
{
    /// <summary>
    /// Query run on each probe. Defaults to <c>SELECT 1</c>; use <c>SELECT 1 FROM DUAL</c> for Oracle. Set it to
    /// <c>null</c> to only open the connection.
    /// </summary>
    public string? CommandText { get; set; } = "SELECT 1";
}
