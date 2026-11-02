using System.Net;
using Homeostat.Signals;

namespace Homeostat.Http;

/// <summary>Settings for the signals of one HTTP client.</summary>
public sealed class HttpClientSignalOptions
{
    /// <summary>Signal source name. Defaults to <c>"http:{client name}"</c>.</summary>
    public string? Source { get; set; }

    /// <summary>Failure ratio (0 to 1) at or above which the signal is <see cref="SignalSeverity.Degraded"/>.</summary>
    public double DegradedFailureRatio { get; set; } = 0.1;

    /// <summary>Failure ratio (0 to 1) at or above which the signal is <see cref="SignalSeverity.Unhealthy"/>.</summary>
    public double UnhealthyFailureRatio { get; set; } = 0.5;

    /// <summary>95th percentile latency at or above which the signal is at least <see cref="SignalSeverity.Degraded"/>. <c>null</c> ignores latency.</summary>
    public TimeSpan? DegradedLatency { get; set; }

    /// <summary>95th percentile latency at or above which the signal is <see cref="SignalSeverity.Unhealthy"/>. <c>null</c> ignores latency.</summary>
    public TimeSpan? UnhealthyLatency { get; set; }

    /// <summary>
    /// Minimum number of requests in a sample to judge the client. With fewer requests, the sample is
    /// reported as healthy only if none failed; otherwise no signal is published.
    /// </summary>
    public int MinimumRequests { get; set; } = 10;

    /// <summary>
    /// Decides whether a response counts as a failure. By default: 5xx, 408 (Request Timeout) and
    /// 429 (Too Many Requests). Exceptions always count as failures.
    /// </summary>
    public Func<HttpResponseMessage, bool> IsFailure { get; set; } = DefaultIsFailure;

    /// <summary>
    /// Whether cancelled requests count as failures. Default <c>true</c>, because <see cref="HttpClient"/>
    /// reports its own timeout as a cancellation, and from inside the handler pipeline a timeout cannot be
    /// told apart from a cancellation by the caller. Set it to <c>false</c> if your callers cancel requests
    /// often for reasons unrelated to the remote service, and enforce timeouts in a way that throws.
    /// </summary>
    public bool CountCancellationsAsFailures { get; set; } = true;

    private static bool DefaultIsFailure(HttpResponseMessage response)
        => (int)response.StatusCode >= 500
           || response.StatusCode == HttpStatusCode.RequestTimeout
           || response.StatusCode == HttpStatusCode.TooManyRequests;
}
