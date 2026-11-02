namespace Homeostat.Signals;

/// <summary>
/// A single observation about a part of the system, for example the result of a health check
/// or a metric crossing a threshold.
/// </summary>
/// <param name="Source">Logical name of the observed component (for example "weather-feed").</param>
/// <param name="Severity">How bad the observation is.</param>
/// <param name="Timestamp">When the observation was made.</param>
public sealed record Signal(string Source, SignalSeverity Severity, DateTimeOffset Timestamp)
{
    /// <summary>Optional human-readable detail.</summary>
    public string? Message { get; init; }

    /// <summary>Optional key/value attributes, for example the name of a metric.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}
