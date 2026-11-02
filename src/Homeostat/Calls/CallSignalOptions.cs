using Homeostat.Signals;

namespace Homeostat.Calls;

/// <summary>Thresholds of the signal that a <see cref="CallRecorder"/> publishes at every detection interval.</summary>
public sealed class CallSignalOptions
{
    /// <summary>Failure ratio (0 to 1) at or above which the signal is <see cref="SignalSeverity.Degraded"/>. Defaults to 0.1.</summary>
    public double DegradedFailureRatio { get; set; } = 0.1;

    /// <summary>Failure ratio (0 to 1) at or above which the signal is <see cref="SignalSeverity.Unhealthy"/>. Defaults to 0.5.</summary>
    public double UnhealthyFailureRatio { get; set; } = 0.5;

    /// <summary>95th percentile duration at or above which the signal is at least <see cref="SignalSeverity.Degraded"/>. <c>null</c> (the default) ignores durations.</summary>
    public TimeSpan? DegradedLatency { get; set; }

    /// <summary>95th percentile duration at or above which the signal is <see cref="SignalSeverity.Unhealthy"/>. <c>null</c> (the default) ignores durations.</summary>
    public TimeSpan? UnhealthyLatency { get; set; }

    /// <summary>
    /// Calls an interval needs before its failure ratio is judged. With fewer calls, an interval without failures is
    /// reported as healthy and an interval with failures publishes nothing. Defaults to 10.
    /// </summary>
    public int MinimumCalls { get; set; } = 10;

    internal CallSignalOptions Clone() => new()
    {
        DegradedFailureRatio = DegradedFailureRatio,
        UnhealthyFailureRatio = UnhealthyFailureRatio,
        DegradedLatency = DegradedLatency,
        UnhealthyLatency = UnhealthyLatency,
        MinimumCalls = MinimumCalls,
    };

    internal void Validate(string source)
    {
        if (DegradedFailureRatio is <= 0 or > 1 || UnhealthyFailureRatio is <= 0 or > 1 || DegradedFailureRatio > UnhealthyFailureRatio)
        {
            throw new ArgumentOutOfRangeException(nameof(DegradedFailureRatio),
                $"Call signals '{source}': the failure ratios must be in (0, 1], the degraded one at most the unhealthy one.");
        }

        if (DegradedLatency is { } d && d <= TimeSpan.Zero || UnhealthyLatency is { } u && u <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DegradedLatency), $"Call signals '{source}': latency thresholds must be positive.");
        }

        if (MinimumCalls < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumCalls), $"Call signals '{source}': MinimumCalls must be at least 1.");
        }
    }
}
