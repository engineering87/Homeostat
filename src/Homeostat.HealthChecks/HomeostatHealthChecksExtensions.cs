using Homeostat.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homeostat;

/// <summary>Connects Homeostat with Microsoft.Extensions health checks.</summary>
public static class HomeostatHealthChecksExtensions
{
    /// <summary>
    /// Turns every registered health check into a signal source: the health check name becomes
    /// the signal source, and Healthy / Degraded / Unhealthy map to the same severities. The checks run on the
    /// framework's health check publisher, in the background, whether or not a health endpoint is called.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">How often the checks run; optional.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="HealthCheckSignalOptions.Interval"/> is shorter than one second.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddHealthChecks().AddCheck&lt;WeatherFeedHealthCheck&gt;("weather-feed");
    /// builder.Services.AddHomeostat(h => h
    ///     .AddHealthCheckSignals(o => o.Interval = TimeSpan.FromSeconds(10))
    ///     .AddCondition("weather-feed-down", c => c.ForSource("weather-feed").SustainedFor(TimeSpan.FromMinutes(1))));
    /// </code>
    /// </example>
    public static HomeostatBuilder AddHealthCheckSignals(this HomeostatBuilder builder, Action<HealthCheckSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new HealthCheckSignalOptions();
        configure?.Invoke(options);
        if (options.Interval is { } requested && requested < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(configure), "The health check period must be at least one second.");
        }

        // Registers the hosted service that runs health checks periodically.
        builder.Services.AddHealthChecks();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheckPublisher, HealthCheckSignalPublisher>());

        if (options.Interval is { } value)
        {
            builder.Services.Configure<HealthCheckPublisherOptions>(o => o.Period = value);
        }

        return builder;
    }

    /// <summary>
    /// Adds a health check that reports Homeostat itself: <paramref name="failureStatus"/> (Degraded when null, the
    /// default) while it is paused, its action budget is exhausted, a step waits for approval or a condition is active;
    /// Healthy otherwise. The details are in the result data. This check is never turned into a signal. A store that
    /// cannot be read, and a check that fails or exceeds <paramref name="timeout"/>, are always reported as Degraded,
    /// whatever <paramref name="failureStatus"/>: the store is shared, and an Unhealthy result would take every replica
    /// out of rotation at once.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">Name of the check.</param>
    /// <param name="failureStatus">Status reported when Homeostat needs attention; null means Degraded.</param>
    /// <param name="tags">Additional tags, for example to select the check in a health endpoint.</param>
    /// <param name="timeout">Time allowed for the check; null means none (the check bounds its own store reads).</param>
    /// <example><code>builder.Services.AddHealthChecks().AddHomeostatCheck();</code></example>
    public static IHealthChecksBuilder AddHomeostatCheck(
        this IHealthChecksBuilder builder,
        string name = "homeostat",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var attentionStatus = failureStatus ?? HealthStatus.Degraded;
        // The registration's own failure status (used when the check throws or times out) stays Degraded, like a store
        // outage (decision I6): these are problems of Homeostat, not of the application.
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new HomeostatHealthCheck(
                sp.GetRequiredService<IHomeostatControl>(),
                sp.GetRequiredService<IHomeostatApprovals>(),
                attentionStatus,
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("Homeostat.HealthChecks")),
            HealthStatus.Degraded,
            (tags ?? []).Append(HomeostatHealthCheck.Tag),
            timeout));
    }
}
