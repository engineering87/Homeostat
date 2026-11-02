using System.Net;
using Homeostat.Signals;

namespace Homeostat.Http;

/// <summary>Settings for the signals of one HTTP client.</summary>
public sealed class HttpClientSignalOptions
{
    /// <summary>Signal source name. Defaults to <c>"http:{client name}"</c>.</summary>
    public string? Source { get; set; }

    /// <summary>Failure ratio (0 to 1) at or above which the signal is <see cref="SignalSeverity.Degraded"/>. Defaults to 0.1.</summary>
    public double DegradedFailureRatio { get; set; } = 0.1;

    /// <summary>Failure ratio (0 to 1) at or above which the signal is <see cref="SignalSeverity.Unhealthy"/>. Defaults to 0.5.</summary>
    public double UnhealthyFailureRatio { get; set; } = 0.5;

    /// <summary>95th percentile latency at or above which the signal is at least <see cref="SignalSeverity.Degraded"/>. <c>null</c> ignores latency.</summary>
    public TimeSpan? DegradedLatency { get; set; }

    /// <summary>95th percentile latency at or above which the signal is <see cref="SignalSeverity.Unhealthy"/>. <c>null</c> ignores latency.</summary>
    public TimeSpan? UnhealthyLatency { get; set; }

    /// <summary>
    /// Minimum number of requests in a sample to judge the client. With fewer requests, the sample is
    /// reported as healthy only if none failed; otherwise no signal is published. Defaults to 10.
    /// </summary>
    public int MinimumRequests { get; set; } = 10;

    /// <summary>
    /// Decides whether a response counts as a failure. By default: 5xx, 408 (Request Timeout), 429 (Too Many
    /// Requests), and gRPC calls that fail with a server-side status (see <see cref="IsGrpcFailure"/>). Exceptions
    /// always count as failures.
    /// </summary>
    public Func<HttpResponseMessage, bool> IsFailure { get; set; } = DefaultIsFailure;

    /// <summary>
    /// Whether canceled requests count as failures. Default <c>true</c>, because <see cref="HttpClient"/>
    /// reports its own timeout as a cancellation, and from inside the handler pipeline a timeout cannot be
    /// told apart from a cancellation by the caller. Set it to <c>false</c> if your callers cancel requests
    /// often for reasons unrelated to the remote service, and enforce timeouts in a way that throws.
    /// </summary>
    public bool CountCancellationsAsFailures { get; set; } = true;

    /// <summary>
    /// Whether a gRPC response (content type <c>application/grpc</c>, through <c>Grpc.Net.Client</c> on an
    /// <see cref="HttpClient"/> from the factory) reports a failure of the service: gRPC answers HTTP 200 and puts the
    /// outcome in the <c>grpc-status</c> header, so the HTTP status alone never shows it. Counts the statuses that mean
    /// the service is in trouble: Unknown (2), DeadlineExceeded (4), ResourceExhausted (8), Internal (13),
    /// Unavailable (14) and DataLoss (15); errors in the request (NotFound, InvalidArgument...) do not count.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <returns><c>true</c> for a gRPC response that failed with one of those statuses.</returns>
    /// <remarks>
    /// The status is read from the response headers, where gRPC puts it when a call fails before any message (the
    /// usual case for these statuses). A call that fails after streaming messages reports it in the trailers, which
    /// arrive after the handler has returned, so it is not counted.
    /// </remarks>
    public static bool IsGrpcFailure(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Content?.Headers.ContentType?.MediaType is not { } mediaType
            || !mediaType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase)
            || !response.Headers.TryGetValues("grpc-status", out var values))
        {
            return false;
        }

        return int.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var status)
            && status is 2 or 4 or 8 or 13 or 14 or 15;
    }

    private static bool DefaultIsFailure(HttpResponseMessage response)
        => (int)response.StatusCode >= 500
           || response.StatusCode == HttpStatusCode.RequestTimeout
           || response.StatusCode == HttpStatusCode.TooManyRequests
           || IsGrpcFailure(response);
}
