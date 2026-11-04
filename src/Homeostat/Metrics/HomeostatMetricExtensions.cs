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
        builder.Services.AddHomeostatDetector<MetricSignalDetector>();
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
        if (options.Interval < TimeSpan.FromSeconds(1) || options.Interval > TimeSpan.FromHours(1) || options.Interval.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), options.Interval, "Interval must be a whole number of seconds between 1 second and 1 hour.");
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
    /// Publishes the share of server requests answered with a 5xx status (0 to 1), from the ASP.NET Core
    /// <c>http.server.request.duration</c> metric. No signal is published for intervals with fewer than
    /// <see cref="ServerSignalOptions.MinimumRequests"/> requests.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Source, thresholds and minimum requests; optional.</param>
    /// <example><code>h.AddServerErrorRateSignal(o => o.UnhealthyAbove = 0.02)</code></example>
    public static HomeostatBuilder AddServerErrorRateSignal(this HomeostatBuilder builder, Action<ServerErrorRateSignalOptions>? configure = null)
    {
        var options = new ServerErrorRateSignalOptions();
        configure?.Invoke(options);
        return builder.AddMetricSignal("http.server.request.duration", m => m
            .FromMeter("Microsoft.AspNetCore.Hosting")
            .Source(options.Source)
            .RatioOf((_, tags) => tags.TryGetValue("http.response.status_code", out var status) && IsServerError(status))
            .DegradedAbove(options.DegradedAbove)
            .UnhealthyAbove(options.UnhealthyAbove)
            .MinimumCount(options.MinimumRequests));
    }

    /// <summary>
    /// Publishes the 95th percentile of server request duration, in seconds (the signal's value), from the ASP.NET Core
    /// <c>http.server.request.duration</c> metric.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Source, thresholds and minimum requests; optional.</param>
    /// <example><code>h.AddServerLatencySignal(o => o.UnhealthyAbove = TimeSpan.FromSeconds(1))</code></example>
    public static HomeostatBuilder AddServerLatencySignal(this HomeostatBuilder builder, Action<ServerLatencySignalOptions>? configure = null)
    {
        var options = new ServerLatencySignalOptions();
        configure?.Invoke(options);
        return builder.AddMetricSignal("http.server.request.duration", m => m
            .FromMeter("Microsoft.AspNetCore.Hosting")
            .Source(options.Source)
            .Statistic(MetricStatistic.P95)
            .DegradedAbove(options.DegradedAbove.TotalSeconds)
            .UnhealthyAbove(options.UnhealthyAbove.TotalSeconds)
            .MinimumCount(options.MinimumRequests));
    }

    private static bool IsServerError(object? status) => status switch
    {
        int code => code >= 500,
        long code => code >= 500,
        string text => int.TryParse(text, out var code) && code >= 500,
        _ => false,
    };
}
