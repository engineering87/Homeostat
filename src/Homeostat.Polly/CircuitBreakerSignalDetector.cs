using Homeostat.Detectors;
using Homeostat.Signals;
using Polly.CircuitBreaker;

namespace Homeostat.Polly;

/// <summary>Publishes the state of every tracked circuit breaker at each detection interval.</summary>
internal sealed class CircuitBreakerSignalDetector : ISignalDetector
{
    private readonly CircuitBreakerCatalog _catalog;
    private readonly TimeProvider _timeProvider;

    public CircuitBreakerSignalDetector(CircuitBreakerCatalog catalog, TimeProvider timeProvider)
    {
        _catalog = catalog;
        _timeProvider = timeProvider;
    }

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (source, provider) in _catalog.Tracked)
        {
            CircuitState state;
            try
            {
                state = provider.CircuitState;
            }
            catch (InvalidOperationException)
            {
                // Defensive: skip a provider that cannot report its state. Depending on the Polly version,
                // a provider not yet attached to a strategy may instead report Closed (published as Healthy).
                continue;
            }

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
