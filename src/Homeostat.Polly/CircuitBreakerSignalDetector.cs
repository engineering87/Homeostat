using Homeostat.Detectors;
using Homeostat.Signals;
using Polly.CircuitBreaker;

namespace Homeostat.Polly;

/// <summary>Publishes the state of every tracked circuit breaker (the worst one per source) at each detection interval.</summary>
internal sealed class CircuitBreakerSignalDetector : ISignalDetector
{
    private readonly CircuitBreakerRegistry _registry;
    private readonly TimeProvider _timeProvider;

    public CircuitBreakerSignalDetector(CircuitBreakerRegistry registry, TimeProvider timeProvider)
    {
        _registry = registry;
        _timeProvider = timeProvider;
    }

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (source, state) in _registry.States())
        {
            signals.TryPublish(new Signal(source, Map(state), now)
            {
                Message = $"Circuit breaker is {state}.",
                Tags = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["origin"] = "circuit-breaker",
                    ["state"] = state.ToString(),
                },
            });
        }

        return ValueTask.CompletedTask;
    }

    internal static SignalSeverity Map(CircuitState state) => state switch
    {
        CircuitState.Closed => SignalSeverity.Healthy,
        CircuitState.HalfOpen => SignalSeverity.Degraded,
        _ => SignalSeverity.Unhealthy, // Open or Isolated
    };
}
