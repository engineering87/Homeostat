using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HomeostatWebApi;

/// <summary>Client of an external reference data provider. Replace it with your own dependency.</summary>
public sealed class ReferenceDataClient
{
    private volatile bool _healthy = true;

    public IReadOnlyList<string> CachedRegions { get; } = ["north", "central", "south", "coast"];

    public Task<IReadOnlyList<string>> GetRegionsAsync(CancellationToken cancellationToken)
        => _healthy ? Task.FromResult(CachedRegions) : throw new HttpRequestException("The reference data provider is not responding.");

    public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(_healthy);

    /// <summary>Recreates connections to the provider: often enough after a network blip.</summary>
    public void Renew() => _healthy = true;
}

public sealed class ReferenceDataHealthCheck(ReferenceDataClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => await client.PingAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The reference data provider is not responding.");
}
