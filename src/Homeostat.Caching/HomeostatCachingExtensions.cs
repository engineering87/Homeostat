using Homeostat.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Registers cache signals on the Homeostat builder.</summary>
public static class HomeostatCachingExtensions
{
    /// <summary>
    /// Publishes the hit ratio (0 to 1) of a memory cache over each detection interval as the value of a signal from
    /// <see cref="MemoryCacheSignalOptions.Source"/> (<c>cache:memory</c>). The cache must track statistics (<c>MemoryCacheOptions.TrackStatistics = true</c>).
    /// A falling hit ratio often explains a latency spike on the dependency behind the cache.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Source, cache, thresholds and the minimum number of lookups; optional.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddMemoryCache(o => o.TrackStatistics = true);
    /// h.AddMemoryCacheSignals(o => { o.Source = "lookup-cache"; o.DegradedBelow = 0.6; });
    /// </code>
    /// </example>
    public static HomeostatBuilder AddMemoryCacheSignals(this HomeostatBuilder builder, Action<MemoryCacheSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new MemoryCacheSignalOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Source, nameof(MemoryCacheSignalOptions.Source));
        var source = options.Source;
        options.Validate(source);

        if (builder.Services.Any(d => d.ImplementationInstance is MemoryCacheWatch existing
            && string.Equals(existing.Source, source, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Memory cache signals with source '{source}' are already registered.");
        }

        builder.Services.AddSingleton(new MemoryCacheWatch(
            source, options.Cache ?? (static sp => sp.GetRequiredService<IMemoryCache>()), options));
        builder.AddDetector<MemoryCacheSignalDetector>();
        return builder;
    }
}
