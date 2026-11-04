using Homeostat.Polly;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly.CircuitBreaker;
using Polly.Simmy;

namespace Homeostat;

/// <summary>Connects Homeostat with Polly v8 circuit breakers.</summary>
public static class HomeostatPollyExtensions
{
    /// <summary>
    /// Enables circuit breaker signals. Then attach a tracked state provider to each circuit breaker
    /// with <see cref="HomeostatPollyServices.TrackCircuitBreaker"/>.
    /// </summary>
    public static HomeostatBuilder AddCircuitBreakerSignals(this HomeostatBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<CircuitBreakerCatalog>();
        builder.Services.TryAddSingleton<CircuitControlCatalog>();
        builder.AddDetector<CircuitBreakerSignalDetector>();
        return builder;
    }
}
