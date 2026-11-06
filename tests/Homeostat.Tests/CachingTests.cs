using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Homeostat.Tests;

public class CachingTests
{
    [Fact]
    public async Task Memory_cache_signals_publish_the_hit_ratio_of_each_interval()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMemoryCache(o => o.TrackStatistics = true);
        services.AddHomeostat(h => h.AddMemoryCacheSignals(o =>
        {
            o.Source = "lookup-cache";
            o.DegradedBelow = 0.6;
            o.UnhealthyBelow = 0.2;
            o.MinimumLookups = 10;
        }));
        await using var provider = services.BuildServiceProvider();
        var detector = provider.GetServices<Detectors.ISignalDetector>().Single(d => d.GetType().Name == "MemoryCacheSignalDetector");
        var cache = provider.GetRequiredService<IMemoryCache>();
        var signals = new CollectingPublisher();

        void Lookups(int hits, int misses)
        {
            cache.Set("present", 1);
            for (var i = 0; i < hits; i++)
            {
                cache.TryGetValue("present", out _);
            }

            for (var i = 0; i < misses; i++)
            {
                cache.TryGetValue($"absent-{Guid.NewGuid()}", out _);
            }
        }

        Lookups(100, 0);
        await detector.DetectAsync(signals, CancellationToken.None); // Baseline only.
        Assert.Empty(signals.Signals);

        Lookups(9, 1);
        await detector.DetectAsync(signals, CancellationToken.None);
        Lookups(4, 6);
        await detector.DetectAsync(signals, CancellationToken.None);
        Lookups(1, 9);
        await detector.DetectAsync(signals, CancellationToken.None);
        Lookups(1, 2); // Too few lookups.
        await detector.DetectAsync(signals, CancellationToken.None);

        Assert.Equal([0.9, 0.4, 0.1], signals.Signals.Select(s => Math.Round(s.Value!.Value, 3)));
        Assert.Equal(
            [SignalSeverity.Healthy, SignalSeverity.Degraded, SignalSeverity.Unhealthy],
            signals.Signals.Select(s => s.Severity));
        Assert.Equal("cache:memory", signals.Signals[0].Tags!["origin"]);
        Assert.Equal("9", signals.Signals[0].Tags!["hits"]);
    }

    [Fact]
    public async Task Memory_cache_without_statistics_publishes_nothing()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMemoryCache();
        services.AddHomeostat(h => h.AddMemoryCacheSignals());
        await using var provider = services.BuildServiceProvider();
        var detector = provider.GetServices<Detectors.ISignalDetector>().Single(d => d.GetType().Name == "MemoryCacheSignalDetector");
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);
        await detector.DetectAsync(signals, CancellationToken.None);

        Assert.Empty(signals.Signals);
    }

    [Fact]
    public void Memory_cache_signal_options_are_validated()
    {
        new ServiceCollection().AddHomeostat(h =>
        {
            h.AddMemoryCacheSignals(o => o.Source = "a");
            Assert.Throws<InvalidOperationException>(() => h.AddMemoryCacheSignals(o => o.Source = "a"));
            Assert.Throws<ArgumentException>(() => h.AddMemoryCacheSignals(o => { o.Source = "b"; o.DegradedBelow = 1.5; }));
            Assert.Throws<ArgumentException>(() => h.AddMemoryCacheSignals(o => { o.Source = "c"; o.DegradedBelow = 0.2; o.UnhealthyBelow = 0.5; }));
        });
    }

    [Fact]
    public async Task Cache_actions_purge_tags_remove_keys_and_compact()
    {
        var hybrid = new RecordingHybridCache();
        var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await distributed.SetStringAsync("reference:regions", "stale");
        await distributed.SetStringAsync("reference:units", "stale");
        await distributed.SetStringAsync("keep", "fresh");
        var memory = new MemoryCache(new MemoryCacheOptions());
        for (var i = 0; i < 100; i++)
        {
            memory.Set(i, i);
        }

        await using var host = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("stale-reference-data", c => c.ForSource("reference").Heal(p => p
                .PurgeHybridCacheTags("reference-data")
                .Then("noop", _ => { })
                .RemoveHybridCacheKeys("regions")
                .RemoveDistributedCacheKeys("reference:regions", "reference:units")
                .CompactMemoryCache(0.5)
                .UseVerifier<NeverHealed>())),
            s => s.AddSingleton<HybridCache>(hybrid).AddSingleton<IDistributedCache>(distributed).AddSingleton<IMemoryCache>(memory));

        host.Publish("reference", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("stale-reference-data");

        Assert.All(run.Steps, s => Assert.True(s.Succeeded, s.Error?.ToString()));
        Assert.Equal(["tags:reference-data", "keys:regions"], hybrid.Calls);
        Assert.Null(await distributed.GetStringAsync("reference:regions"));
        Assert.Null(await distributed.GetStringAsync("reference:units"));
        Assert.Equal("fresh", await distributed.GetStringAsync("keep"));
        Assert.Equal(50, memory.Count);
    }

    [Fact]
    public void Cache_actions_validate_their_arguments()
    {
        new ServiceCollection().AddHomeostat(h => h.AddCondition("x", c => c.ForSource("x").Heal(p =>
        {
            Assert.Throws<ArgumentException>(() => p.PurgeHybridCacheTags());
            Assert.Throws<ArgumentException>(() => p.RemoveDistributedCacheKeys("a", " "));
            Assert.Throws<ArgumentOutOfRangeException>(() => p.CompactMemoryCache(0));
            p.With("ok", _ => { });
        })));
    }

    internal sealed class NeverHealed : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(VerificationOutcome.NotHealed);
    }

    private sealed class RecordingHybridCache : HybridCache
    {
        public List<string> Calls { get; } = [];

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
            => factory(state, cancellationToken);

        public override ValueTask SetAsync<T>(string key, T value, HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Calls.Add($"keys:{key}");
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
        {
            Calls.Add($"tags:{tag}");
            return ValueTask.CompletedTask;
        }
    }
}
