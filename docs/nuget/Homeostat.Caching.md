# Homeostat.Caching

Cache integration for Homeostat: the hit ratio of an `IMemoryCache` as a signal, and healing actions that purge `HybridCache` tags and keys, remove `IDistributedCache` keys and compact the memory cache.

## Install

```bash
dotnet add package Homeostat.Caching --prerelease
```

This package is a **preview**: it works and is tested, but its API can still change in a minor version (see [Stable and preview packages](https://github.com/engineering87/Homeostat/blob/main/docs/versioning.md#stable-and-preview-packages)), so `--prerelease` stays needed after 1.0.0.

## Example

```csharp
using Homeostat;

builder.Services.AddMemoryCache(o => o.TrackStatistics = true);   // required for the hit ratio

builder.Services.AddHomeostat(h => h
    .AddMemoryCacheSignals(o => o.DegradedBelow = 0.6)   // source "cache:memory"
    .AddMemoryPressureDetector()
    .AddCondition("memory-high", c => c
        .ForSource("memory")
        .SustainedFor(TimeSpan.FromMinutes(2))
        .Heal(p => p.CompactMemoryCache(0.25))));
```

## Documentation

[Caches](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#caches)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
