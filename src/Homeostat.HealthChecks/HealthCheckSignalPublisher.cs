using Homeostat.Signals;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homeostat.HealthChecks;

/// <summary>
/// Publishes one signal per health check entry each time the health check publisher runs.
/// The entry name becomes the signal source.
/// </summary>
internal sealed class HealthCheckSignalPublisher : IHealthCheckPublisher
{
    private readonly ISignalPublisher _signals;
    private readonly TimeProvider _timeProvider;

    public HealthCheckSignalPublisher(ISignalPublisher signals, TimeProvider timeProvider)
    {
        _signals = signals;
        _timeProvider = timeProvider;
    }

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var (name, entry) in report.Entries)
        {
            // Homeostat's own check describes Homeostat, not a component to heal.
            if (entry.Tags.Contains(HomeostatHealthCheck.Tag))
            {
                continue;
            }

            _signals.TryPublish(new Signal(name, Map(entry.Status), now)
            {
                Message = entry.Description ?? entry.Exception?.Message,
                Tags = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["origin"] = "health-check",
                    ["duration_ms"] = entry.Duration.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                },
            });
        }

        return Task.CompletedTask;
    }

    internal static SignalSeverity Map(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => SignalSeverity.Healthy,
        HealthStatus.Degraded => SignalSeverity.Degraded,
        _ => SignalSeverity.Unhealthy,
    };
}
