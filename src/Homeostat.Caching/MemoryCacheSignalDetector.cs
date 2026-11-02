using System.Globalization;
using Homeostat.Detectors;
using Homeostat.Signals;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Homeostat.Caching;

internal sealed class MemoryCacheWatch(string source, Func<IServiceProvider, IMemoryCache> resolve, MemoryCacheSignalOptions options)
{
    public string Source { get; } = source;

    public Func<IServiceProvider, IMemoryCache> Resolve { get; } = resolve;

    public MemoryCacheSignalOptions Options { get; } = options;
}

/// <summary>
/// Publishes the hit ratio of each watched memory cache over the last detection interval. The first interval is only
/// a baseline, and a counter that goes back (a recreated cache) starts a new baseline.
/// </summary>
internal sealed class MemoryCacheSignalDetector : ISignalDetector
{
    private readonly State[] _watches;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MemoryCacheSignalDetector> _logger;

    public MemoryCacheSignalDetector(
        IEnumerable<MemoryCacheWatch> watches,
        IServiceProvider services,
        TimeProvider timeProvider,
        ILogger<MemoryCacheSignalDetector> logger)
    {
        _watches = watches.Select(w => new State(w)).ToArray();
        _services = services;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        foreach (var state in _watches)
        {
            try
            {
                Detect(state, signals);
            }
            catch (Exception ex)
            {
                // One misconfigured cache must not silence the others.
                _logger.LogError(ex, "Memory cache signals {Source} failed.", state.Watch.Source);
            }
        }

        return ValueTask.CompletedTask;
    }

    private void Detect(State state, ISignalPublisher signals)
    {
        var watch = state.Watch;
        var statistics = watch.Resolve(_services).GetCurrentStatistics();
        if (statistics is null)
        {
            if (!state.WarnedNoStatistics)
            {
                state.WarnedNoStatistics = true;
                _logger.LogWarning(
                    "Memory cache signals {Source}: the cache does not track statistics. Set MemoryCacheOptions.TrackStatistics to true.",
                    watch.Source);
            }

            return;
        }

        var previous = state.Previous;
        state.Previous = statistics;
        if (previous is null || statistics.TotalHits < previous.TotalHits || statistics.TotalMisses < previous.TotalMisses)
        {
            return; // Baseline.
        }

        var hits = statistics.TotalHits - previous.TotalHits;
        var misses = statistics.TotalMisses - previous.TotalMisses;
        var lookups = hits + misses;
        if (lookups < watch.Options.MinimumLookups)
        {
            return;
        }

        var ratio = (double)hits / lookups;
        var severity = ratio < watch.Options.UnhealthyBelow ? SignalSeverity.Unhealthy
            : ratio < watch.Options.DegradedBelow ? SignalSeverity.Degraded
            : SignalSeverity.Healthy;

        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["origin"] = "cache:memory",
            ["hits"] = hits.ToString(CultureInfo.InvariantCulture),
            ["misses"] = misses.ToString(CultureInfo.InvariantCulture),
            ["entries"] = statistics.CurrentEntryCount.ToString(CultureInfo.InvariantCulture),
        };
        if (statistics.CurrentEstimatedSize is { } size)
        {
            tags["estimatedSize"] = size.ToString(CultureInfo.InvariantCulture);
        }

        signals.TryPublish(new Signal(watch.Source, severity, _timeProvider.GetUtcNow())
        {
            Value = ratio,
            Message = $"Memory cache hit ratio {ratio:P1} over {lookups} lookups.",
            Tags = tags,
        });
    }

    private sealed class State(MemoryCacheWatch watch)
    {
        public MemoryCacheWatch Watch { get; } = watch;

        public MemoryCacheStatistics? Previous { get; set; }

        public bool WarnedNoStatistics { get; set; }
    }
}
