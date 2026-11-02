using Homeostat.Polly;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly.CircuitBreaker;

namespace Homeostat;

/// <summary>Connects Homeostat with Polly v8 circuit breakers.</summary>
public static class HomeostatPollyExtensions
{
    /// <summary>
    /// Enables circuit breaker signals. Then attach a tracked state provider to each circuit breaker
    /// with <see cref="TrackCircuitBreaker"/>.
    /// </summary>
    public static HomeostatBuilder AddCircuitBreakerSignals(this HomeostatBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<CircuitBreakerCatalog>();
        builder.AddDetector<CircuitBreakerSignalDetector>();
        return builder;
    }

    /// <summary>
    /// Creates a <see cref="CircuitBreakerStateProvider"/> whose state Homeostat publishes as signals from
    /// <paramref name="source"/>: Closed → Healthy, HalfOpen → Degraded, Open or Isolated → Unhealthy.
    /// Assign it to <c>StateProvider</c> of the circuit breaker options.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddResiliencePipeline("forecast", (pipeline, context) =>
    ///     pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
    ///     {
    ///         StateProvider = context.ServiceProvider.TrackCircuitBreaker("forecast-circuit"),
    ///     }));
    /// </code>
    /// </example>
    public static CircuitBreakerStateProvider TrackCircuitBreaker(this IServiceProvider services, string source)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var catalog = services.GetService<CircuitBreakerCatalog>()
            ?? throw new InvalidOperationException(
                "Circuit breaker signals are not enabled. Call AddHomeostat(...).AddCircuitBreakerSignals() first.");

        return catalog.Track(source);
    }
}
