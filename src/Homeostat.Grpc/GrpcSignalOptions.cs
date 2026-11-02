using Grpc.Core;
using Homeostat.Calls;

namespace Homeostat.Grpc;

/// <summary>Settings of the signals of one gRPC client (see <c>AddHomeostatGrpcSignals</c>).</summary>
public sealed class GrpcSignalOptions
{
    /// <summary>Signal source. Defaults to <c>"grpc:{client name}"</c>.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// Decides which call statuses count as failures of the service. Defaults to <see cref="IsServiceFailure"/>: errors
    /// in the request (<c>NotFound</c>, <c>InvalidArgument</c>, <c>PermissionDenied</c>...) and calls canceled by the
    /// caller do not count.
    /// </summary>
    public Func<StatusCode, bool> IsFailure { get; set; } = IsServiceFailure;

    /// <summary>Failure ratio (0 to 1) at or above which the signal is Degraded. Defaults to 0.1.</summary>
    public double DegradedFailureRatio { get; set; } = 0.1;

    /// <summary>Failure ratio (0 to 1) at or above which the signal is Unhealthy. Defaults to 0.5.</summary>
    public double UnhealthyFailureRatio { get; set; } = 0.5;

    /// <summary>
    /// 95th percentile duration of the unary calls at or above which the signal is at least Degraded. <c>null</c> (the
    /// default) ignores durations. Streaming calls have no duration.
    /// </summary>
    public TimeSpan? DegradedLatency { get; set; }

    /// <summary>
    /// 95th percentile duration of the unary calls at or above which the signal is Unhealthy. <c>null</c> (the default)
    /// ignores durations. Streaming calls have no duration.
    /// </summary>
    public TimeSpan? UnhealthyLatency { get; set; }

    /// <summary>Calls an interval needs before its failure ratio is judged (see <see cref="CallSignalOptions.MinimumCalls"/>). Defaults to 10.</summary>
    public int MinimumCalls { get; set; } = 10;

    /// <summary>
    /// Whether a status means that the service is in trouble: <c>Unknown</c>, <c>DeadlineExceeded</c>,
    /// <c>ResourceExhausted</c>, <c>Internal</c>, <c>Unavailable</c> or <c>DataLoss</c>.
    /// </summary>
    /// <param name="status">The status of a call.</param>
    /// <returns><c>true</c> for those statuses.</returns>
    public static bool IsServiceFailure(StatusCode status) => status is StatusCode.Unknown or StatusCode.DeadlineExceeded
        or StatusCode.ResourceExhausted or StatusCode.Internal or StatusCode.Unavailable or StatusCode.DataLoss;
}
