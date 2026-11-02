using Homeostat.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Turns metrics into signals.</summary>
public static class HomeostatMetricExtensions
{
    /// <summary>
    /// Publishes a signal for a <see cref="System.Diagnostics.Metrics"/> instrument (the API used by OpenTelemetry
    /// metrics, ASP.NET Core, HttpClient, Kestrel, the runtime and many libraries) at every detection interval.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="instrument">Instrument name, for example <c>"http.client.request.duration"</c>.</param>
    /// <param name="configure">Statistic, filters and thresholds.</param>
    public static HomeostatBuilder AddMetricSignal(this HomeostatBuilder builder, string instrument, Action<MetricSignalBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        ArgumentNullException.ThrowIfNull(configure);

        var metric = new MetricSignalBuilder(instrument);
        configure(metric);
        builder.Services.AddSingleton(metric.Build());
        builder.Services.AddSignalDetector<MetricSignalDetector>();
        return builder;
    }

    /// <summary>
    /// Publishes the share of server requests answered with a 5xx status, from the ASP.NET Core
    /// <c>http.server.request.duration</c> metric. No signal is published for intervals with fewer than
    /// <paramref name="minimumRequests"/> requests.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="source">Signal source.</param>
    /// <param name="degradedAbove">Error ratio (0 to 1) from which the signal is Degraded.</param>
    /// <param name="unhealthyAbove">Error ratio (0 to 1) from which the signal is Unhealthy.</param>
    /// <param name="minimumRequests">Minimum requests per interval.</param>
    public static HomeostatBuilder AddServerErrorRateSignal(
        this HomeostatBuilder builder,
        string source = "http-server-errors",
        double degradedAbove = 0.01,
        double unhealthyAbove = 0.05,
        int minimumRequests = 20)
        => builder.AddMetricSignal("http.server.request.duration", m => m
            .FromMeter("Microsoft.AspNetCore.Hosting")
            .Source(source)
            .RatioOf((_, tags) => tags.TryGetValue("http.response.status_code", out var status) && IsServerError(status))
            .DegradedAbove(degradedAbove)
            .UnhealthyAbove(unhealthyAbove)
            .MinimumCount(minimumRequests));

    /// <summary>
    /// Publishes the 95th percentile of server request duration, in seconds, from the ASP.NET Core
    /// <c>http.server.request.duration</c> metric.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="source">Signal source.</param>
    /// <param name="degradedAboveSeconds">p95 (seconds) from which the signal is Degraded.</param>
    /// <param name="unhealthyAboveSeconds">p95 (seconds) from which the signal is Unhealthy.</param>
    /// <param name="minimumRequests">Minimum requests per interval.</param>
    public static HomeostatBuilder AddServerLatencySignal(
        this HomeostatBuilder builder,
        string source = "http-server-latency",
        double degradedAboveSeconds = 0.5,
        double unhealthyAboveSeconds = 2,
        int minimumRequests = 20)
        => builder.AddMetricSignal("http.server.request.duration", m => m
            .FromMeter("Microsoft.AspNetCore.Hosting")
            .Source(source)
            .Statistic(MetricStatistic.P95)
            .DegradedAbove(degradedAboveSeconds)
            .UnhealthyAbove(unhealthyAboveSeconds)
            .MinimumCount(minimumRequests));

    private static bool IsServerError(object? status) => status switch
    {
        int code => code >= 500,
        long code => code >= 500,
        string text => int.TryParse(text, out var code) && code >= 500,
        _ => false,
    };
}
