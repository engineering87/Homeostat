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
    /// the signal source, and Healthy / Degraded / Unhealthy map to the same severities.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="period">
    /// How often health checks run (at least one second). When <c>null</c>, the framework default applies (30 seconds).
    /// Keep it shorter than the <c>SustainedFor</c> of your conditions.
    /// </param>
    public static HomeostatBuilder AddHealthCheckSignals(this HomeostatBuilder builder, TimeSpan? period = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (period is { } requested && requested < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(period), "The health check period must be at least one second.");
        }

        // Registers the hosted service that runs health checks periodically.
        builder.Services.AddHealthChecks();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheckPublisher, HealthCheckSignalPublisher>());

        if (period is { } value)
        {
            builder.Services.Configure<HealthCheckPublisherOptions>(o => o.Period = value);
        }

        return builder;
    }

    /// <summary>
    /// Adds a health check that reports Homeostat itself: <paramref name="attentionStatus"/> (Degraded by default)
    /// while it is paused, its action budget is exhausted, a step waits for approval or a condition is active;
    /// Healthy otherwise. The details are in the result data. This check is never turned into a signal.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">Name of the check.</param>
    /// <param name="attentionStatus">Status reported when Homeostat needs attention.</param>
    /// <param name="tags">Additional tags, for example to select the check in a health endpoint.</param>
    /// <example><code>builder.Services.AddHealthChecks().AddHomeostatCheck();</code></example>
    public static IHealthChecksBuilder AddHomeostatCheck(
        this IHealthChecksBuilder builder,
        string name = "homeostat",
        HealthStatus attentionStatus = HealthStatus.Degraded,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new HomeostatHealthCheck(
                sp.GetRequiredService<IHomeostatControl>(),
                sp.GetRequiredService<IHomeostatApprovals>(),
                attentionStatus),
            failureStatus: null,
            tags: (tags ?? []).Append(HomeostatHealthCheck.Tag)));
    }
}
