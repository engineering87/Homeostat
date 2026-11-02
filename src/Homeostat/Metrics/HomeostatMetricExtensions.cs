using Homeostat.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// Publishes a signal for an <c>EventCounter</c>, for libraries that report through event counters rather than
    /// metrics, for example the connection pool counters of <c>Microsoft.Data.SqlClient</c>.
    /// Only the counters are enabled, not the library's tracing events.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="eventSource">Name of the event source, for example <c>"Microsoft.Data.SqlClient.EventSource"</c>.</param>
    /// <param name="counter">Name of the counter, for example <c>"number-of-active-connections"</c>.</param>
    /// <param name="configure">Source, interval, transform and thresholds.</param>
    /// <example>
    /// <code>
    /// h.AddEventCounterSignal("Microsoft.Data.SqlClient.EventSource", "number-of-active-connections",
    ///     o => { o.Source = "sql-pool"; o.Transform = v => v / 100; o.DegradedAbove = 0.8; o.UnhealthyAbove = 0.95; });
    /// </code>
    /// </example>
    public static HomeostatBuilder AddEventCounterSignal(
        this HomeostatBuilder builder, string eventSource, string counter, Action<EventCounterSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(counter);

        var options = new EventCounterSignalOptions();
        configure?.Invoke(options);
        if (options.IntervalSeconds is < 1 or > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), options.IntervalSeconds, "IntervalSeconds must be between 1 and 3600.");
        }

        if (new[] { options.DegradedAbove, options.UnhealthyAbove, options.DegradedBelow, options.UnhealthyBelow }.Any(t => t is { } v && !double.IsFinite(v))
            || options.DegradedAbove > options.UnhealthyAbove || options.DegradedBelow < options.UnhealthyBelow)
        {
            throw new ArgumentException(
                $"Counter '{counter}': thresholds must be finite, DegradedAbove at most UnhealthyAbove and DegradedBelow at least UnhealthyBelow.", nameof(configure));
        }

        builder.Services.AddSingleton(new EventCounterRegistration(eventSource, counter, options.Source ?? $"counter:{eventSource}:{counter}", options));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, EventCounterSignalHost>());
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
        string source = "http-server:errors",
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
        string source = "http-server:latency",
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
