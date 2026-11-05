using System.Collections.Concurrent;
using Polly.CircuitBreaker;

namespace Homeostat.Polly;

/// <summary>Circuit breaker state providers tracked by Homeostat, keyed by signal source.</summary>
/// <remarks>
/// Several pipelines can share a source (keyed or dynamic pipelines, an HTTP resilience handler per authority), so
/// every provider of a source is kept and the source reports the worst state among them. Providers are held strongly:
/// a provider references its circuit breaker, but nothing in the pipeline references the provider, so a weak reference
/// would let a live pipeline's provider be collected. A pipeline that is disposed (Polly disposes a pipeline rebuilt on
/// reload) makes its provider throw <see cref="ObjectDisposedException"/>, and the provider is dropped then. At most
/// <see cref="MaxProvidersPerSource"/> providers are kept per source, the most recent ones, so pipelines rebuilt without
/// being disposed cannot grow the list without bound.
/// </remarks>
internal sealed class CircuitBreakerRegistry
{
    /// <summary>Most providers kept per source; the oldest is dropped beyond it.</summary>
    internal const int MaxProvidersPerSource = 32;

    private readonly ConcurrentDictionary<string, List<CircuitBreakerStateProvider>> _providers = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns a new state provider for <paramref name="source"/>. A provider can be attached to a single strategy, so
    /// each pipeline (and each pipeline rebuilt on a configuration reload) gets a fresh one.
    /// </summary>
    public CircuitBreakerStateProvider Track(string source)
    {
        var provider = new CircuitBreakerStateProvider();
        var list = _providers.GetOrAdd(source, static _ => []);
        lock (list)
        {
            list.Add(provider);
            if (list.Count > MaxProvidersPerSource)
            {
                list.RemoveAt(0);
            }
        }

        return provider;
    }

    /// <summary>
    /// The worst state of every source with at least one provider able to report it (Open or Isolated, then HalfOpen,
    /// then Closed). Providers of disposed pipelines are dropped.
    /// </summary>
    public IEnumerable<(string Source, CircuitState State)> States()
    {
        foreach (var (source, list) in _providers)
        {
            CircuitBreakerStateProvider[] providers;
            lock (list)
            {
                providers = [.. list];
            }

            CircuitState? worst = null;
            foreach (var provider in providers)
            {
                CircuitState state;
                try
                {
                    state = provider.CircuitState;
                }
                catch (ObjectDisposedException)
                {
                    // Its pipeline was disposed (rebuilt on reload): it will never report again.
                    lock (list)
                    {
                        list.Remove(provider);
                    }

                    continue;
                }
                catch (InvalidOperationException)
                {
                    // Defensive: skip a provider that cannot report its state. Depending on the Polly version, a
                    // provider not yet attached to a strategy may instead report Closed (published as Healthy).
                    continue;
                }

                if (worst is null || Rank(state) > Rank(worst.Value))
                {
                    worst = state;
                }
            }

            if (worst is { } reported)
            {
                yield return (source, reported);
            }
        }
    }

    /// <summary>Number of providers kept for a source, for tests.</summary>
    internal int Count(string source)
    {
        if (!_providers.TryGetValue(source, out var list))
        {
            return 0;
        }

        lock (list)
        {
            return list.Count;
        }
    }

    private static int Rank(CircuitState state) => state switch
    {
        CircuitState.Closed => 0,
        CircuitState.HalfOpen => 1,
        _ => 2,   // Open or Isolated
    };
}
