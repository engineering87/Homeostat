using Homeostat.Healing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat;

/// <summary>Cache healing actions.</summary>
public static class HealingPlanBuilderCachingExtensions
{
    /// <summary>
    /// Appends a step that invalidates every <see cref="HybridCache"/> entry carrying any of <paramref name="tags"/>,
    /// for example after a poisoned or stale value made a downstream check fail.
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="tags">Tags to invalidate. <c>"*"</c> invalidates every entry.</param>
    /// <example><code>.Heal(p => p.PurgeHybridCacheTags("reference-data"))</code></example>
    public static HealingPlanBuilder PurgeHybridCacheTags(this HealingPlanBuilder plan, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var copy = Validate(tags, nameof(tags));
        return plan.With($"Purge HybridCache tags {string.Join(", ", copy)}", async (context, ct) =>
        {
            Log(context, "Purging HybridCache tags {Tags} for condition {Condition}.", copy);
            await context.Services.GetRequiredService<HybridCache>().RemoveByTagAsync(copy, ct).ConfigureAwait(false);
        });
    }

    /// <summary>Appends a step that removes <paramref name="keys"/> from <see cref="HybridCache"/>, in memory and in the distributed tier.</summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="keys">Keys to remove.</param>
    public static HealingPlanBuilder RemoveHybridCacheKeys(this HealingPlanBuilder plan, params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var copy = Validate(keys, nameof(keys));
        return plan.With($"Remove HybridCache keys {string.Join(", ", copy)}", async (context, ct) =>
        {
            Log(context, "Removing HybridCache keys {Keys} for condition {Condition}.", copy);
            await context.Services.GetRequiredService<HybridCache>().RemoveAsync(copy, ct).ConfigureAwait(false);
        });
    }

    /// <summary>Appends a step that removes <paramref name="keys"/> from the registered <see cref="IDistributedCache"/>.</summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="keys">Keys to remove.</param>
    public static HealingPlanBuilder RemoveDistributedCacheKeys(this HealingPlanBuilder plan, params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var copy = Validate(keys, nameof(keys));
        return plan.With($"Remove distributed cache keys {string.Join(", ", copy)}", async (context, ct) =>
        {
            Log(context, "Removing distributed cache keys {Keys} for condition {Condition}.", copy);
            var cache = context.Services.GetRequiredService<IDistributedCache>();
            foreach (var key in copy)
            {
                await cache.RemoveAsync(key, ct).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Appends a step that evicts a share of the entries of the registered memory cache (lowest priority and least
    /// recently used first), to free memory under pressure. Requires the default <see cref="MemoryCache"/> implementation.
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="percentage">Share of entries to evict, greater than 0 and at most 1. Defaults to 0.25.</param>
    /// <example>
    /// <code>
    /// h.AddMemoryPressureDetector()
    ///  .AddCondition("memory-high", c => c.ForSource("memory").SustainedFor(TimeSpan.FromMinutes(1))
    ///      .Heal(p => p.CompactMemoryCache(0.5)));
    /// </code>
    /// </example>
    public static HealingPlanBuilder CompactMemoryCache(this HealingPlanBuilder plan, double percentage = 0.25)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!double.IsFinite(percentage) || percentage <= 0 || percentage > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentage), percentage, "The percentage must be greater than 0 and at most 1.");
        }

        return plan.With($"Compact memory cache by {percentage:P0}", (context, _) =>
        {
            if (context.Services.GetRequiredService<IMemoryCache>() is not MemoryCache cache)
            {
                throw new InvalidOperationException("CompactMemoryCache needs the default MemoryCache implementation of IMemoryCache.");
            }

            Log(context, "Compacting the memory cache ({Count} entries) for condition {Condition}.", cache.Count);
            cache.Compact(percentage);
            return Task.CompletedTask;
        });
    }

    private static string[] Validate(string[] values, string name)
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Provide at least one value, and no empty values.", name);
        }

        return [.. values];
    }

    private static void Log(HealingContext context, string message, object value)
        => context.Services.GetService<ILoggerFactory>()?.CreateLogger("Homeostat.Caching")
            .LogWarning(message, value is string[] array ? string.Join(", ", array) : value, context.ConditionName);
}
