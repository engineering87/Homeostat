using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homeostat.Samples.WeatherFeed;

public sealed class WeatherFeedHealthCheck(WeatherFeedClient client) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (client.IsStuck)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Weather feed is not responding."));
        }

        // Working, but on the backup provider: degraded rather than healthy.
        return Task.FromResult(client.UsingBackup
            ? HealthCheckResult.Degraded("Weather feed served by the backup provider.")
            : HealthCheckResult.Healthy("Weather feed served by the primary provider."));
    }
}
