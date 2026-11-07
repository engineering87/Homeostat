namespace Homeostat.Caching;

/// <summary>Settings of the memory cache signals registered with <c>AddMemoryCacheSignals</c>.</summary>
public sealed class MemoryCacheSignalOptions
{
    /// <summary>Signal source. Defaults to <c>"cache:memory"</c>.</summary>
    public string Source { get; set; } = "cache:memory";

    /// <summary>Selects the cache; by default the <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> registered in the services.</summary>
    public Func<IServiceProvider, Microsoft.Extensions.Caching.Memory.IMemoryCache>? Cache { get; set; }

    /// <summary>The hit ratio (0 to 1) is published as Degraded when it is below this threshold.</summary>
    public double? DegradedBelow { get; set; }

    /// <summary>The hit ratio (0 to 1) is published as Unhealthy when it is below this threshold.</summary>
    public double? UnhealthyBelow { get; set; }

    /// <summary>
    /// Minimum number of lookups (hits plus misses) in a detection interval for the hit ratio to be published:
    /// a ratio over a handful of lookups is noise. Defaults to 20.
    /// </summary>
    public int MinimumLookups { get; set; } = 20;

    internal void Validate(string source)
    {
        foreach (var threshold in new[] { DegradedBelow, UnhealthyBelow })
        {
            if (threshold is { } t && (!double.IsFinite(t) || t < 0 || t > 1))
            {
                throw new ArgumentException($"The thresholds of memory cache signals '{source}' must be between 0 and 1.");
            }
        }

        if (DegradedBelow < UnhealthyBelow)
        {
            throw new ArgumentException($"Memory cache signals '{source}': DegradedBelow cannot be less than UnhealthyBelow.");
        }

        if (MinimumLookups < 1)
        {
            throw new ArgumentException($"Memory cache signals '{source}': MinimumLookups must be at least 1.");
        }
    }
}
