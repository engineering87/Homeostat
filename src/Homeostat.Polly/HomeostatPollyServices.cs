using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.Simmy;

namespace Homeostat.Polly;

/// <summary>
/// The Homeostat pieces to plug into Polly resilience pipelines, resolved from the services. In the
/// <c>Homeostat.Polly</c> namespace, so they appear on <see cref="IServiceProvider"/> only where it is imported.
/// </summary>
public static class HomeostatPollyServices
{
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

        var registry = services.GetService<CircuitBreakerRegistry>()
            ?? throw new InvalidOperationException(
                "Circuit breaker signals are not enabled. Call AddHomeostat(...).AddCircuitBreakerSignals() first.");

        return registry.Track(source);
    }

    /// <summary>
    /// Returns the <see cref="CircuitBreakerManualControl"/> named <paramref name="name"/>, driven by the Homeostat switch
    /// <c>circuit:{name}:isolated</c>: while the switch is on, every circuit breaker attached to the control is isolated
    /// (calls fail fast with <see cref="IsolatedCircuitException"/>); when it is turned off, they close. Assign it to
    /// <c>ManualControl</c> of the circuit breaker options, then isolate from a healing plan with
    /// <see cref="HealingPlanBuilderPollyExtensions.IsolateCircuit"/>, or by hand through the control endpoints.
    /// The same name always returns the same control, which can be attached to several circuit breakers.
    /// </summary>
    /// <param name="services">The application services.</param>
    /// <param name="name">Name of the control.</param>
    /// <example>
    /// <code>
    /// services.AddResiliencePipeline("forecast", (pipeline, context) =>
    ///     pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
    ///     {
    ///         StateProvider = context.ServiceProvider.TrackCircuitBreaker("forecast-circuit"),
    ///         ManualControl = context.ServiceProvider.CircuitControl("forecast"),
    ///     }));
    /// </code>
    /// </example>
    public static CircuitBreakerManualControl CircuitControl(this IServiceProvider services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var registry = services.GetService<CircuitControlRegistry>()
            ?? throw new InvalidOperationException(
                "Circuit breaker integration is not enabled. Call AddHomeostat(...).AddCircuitBreakerSignals() first.");

        return registry.GetOrAdd(name);
    }

    /// <summary>
    /// Returns an <c>EnabledGenerator</c> for Polly chaos strategies (<c>AddChaosFault</c>, <c>AddChaosLatency</c>,
    /// <c>AddChaosOutcome</c>, <c>AddChaosBehavior</c>) that injects chaos only while the Homeostat switch
    /// <paramref name="switchName"/> is on. Use it for game days: turn the switch on through the control endpoints,
    /// watch the conditions activate and the healing plans run, then turn it off. Chaos stays off by default.
    /// </summary>
    /// <param name="services">The application services.</param>
    /// <param name="switchName">Switch that enables chaos, for example <c>"chaos:forecast"</c>.</param>
    /// <example>
    /// <code>
    /// pipeline.AddChaosLatency(new ChaosLatencyStrategyOptions
    /// {
    ///     Latency = TimeSpan.FromSeconds(3),
    ///     InjectionRate = 0.5,
    ///     EnabledGenerator = context.ServiceProvider.ChaosEnabledBySwitch("chaos:forecast"),
    /// });
    /// </code>
    /// </example>
    public static Func<EnabledGeneratorArguments, ValueTask<bool>> ChaosEnabledBySwitch(this IServiceProvider services, string switchName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(switchName);

        var switches = services.GetService<IHomeostatSwitches>()
            ?? throw new InvalidOperationException("Homeostat is not registered. Call AddHomeostat(...) first.");

        return _ => ValueTask.FromResult(switches.IsOn(switchName));
    }
}
