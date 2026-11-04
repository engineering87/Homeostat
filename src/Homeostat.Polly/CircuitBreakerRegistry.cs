using System.Collections.Concurrent;
using Polly.CircuitBreaker;

namespace Homeostat.Polly;

/// <summary>Circuit breaker state providers tracked by Homeostat, keyed by signal source.</summary>
internal sealed class CircuitBreakerRegistry
{
    private readonly ConcurrentDictionary<string, CircuitBreakerStateProvider> _providers = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns a new state provider for <paramref name="source"/>. A provider can be attached to a single
    /// strategy, so pipelines that are rebuilt (for example on configuration reload) get a fresh one,
    /// which replaces the previous provider for the same source.
    /// </summary>
    public CircuitBreakerStateProvider Track(string source)
    {
        var provider = new CircuitBreakerStateProvider();
        _providers[source] = provider;
        return provider;
    }

    public IEnumerable<KeyValuePair<string, CircuitBreakerStateProvider>> Tracked => _providers;
}
