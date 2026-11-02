using Homeostat.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Registers cache signals on the Homeostat builder.</summary>
public static class HomeostatCachingExtensions
{
    /// <summary>
    /// Publishes the hit ratio (0 to 1) of a memory cache over each detection interval as the value of a signal from
    /// <paramref name="source"/>. The cache must track statistics (<c>MemoryCacheOptions.TrackStatistics = true</c>).
    /// A falling hit ratio often explains a latency spike on the dependency behind the cache.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="source">Source of the published signals. Defaults to <c>"memory-cache"</c>.</param>
    /// <param name="configure">Changes thresholds and the minimum number of lookups.</param>
    /// <param name="cache">Selects the cache; defaults to the <see cref="IMemoryCache"/> registered in the container.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddMemoryCache(o => o.TrackStatistics = true);
    /// h.AddMemoryCacheSignals("lookup-cache", o => o.DegradedBelow = 0.6);
    /// </code>
    /// </example>
    public static HomeostatBuilder AddMemoryCacheSignals(
        this HomeostatBuilder builder,
        string source = "memory-cache",
        Action<MemoryCacheSignalOptions>? configure = null,
        Func<IServiceProvider, IMemoryCache>? cache = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (builder.Services.Any(d => d.ImplementationInstance is MemoryCacheWatch existing
            && string.Equals(existing.Source, source, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Memory cache signals with source '{source}' are already registered.");
        }

        var options = new MemoryCacheSignalOptions();
        configure?.Invoke(options);
        options.Validate(source);

        builder.Services.AddSingleton(new MemoryCacheWatch(
            source, cache ?? (static sp => sp.GetRequiredService<IMemoryCache>()), options));
        builder.AddDetector<MemoryCacheSignalDetector>();
        return builder;
    }
}
