# Homeostat.Redis

Redis for Homeostat. `UseRedis` stores cooldowns, the action budget, the run and condition history, learned baselines and the control state (pause, application switches, approval requests) in Redis, shared by every instance of the application, and coordinates plans marked `OncePerCluster()` with leases. `AddRedisSignals` watches a Redis client: `PING` round trip and lost or restored connections.

## Install

```bash
dotnet add package Homeostat.Redis --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;

builder.Services.AddHomeostat(h => h
    .UseRedis("redis:6379", o => o.KeyPrefix = "weather-api:homeostat:")   // one prefix per application and environment
    .AddRedisSignals(o => { o.Source = "redis:session-cache"; o.DegradedLatency = TimeSpan.FromMilliseconds(50); })   // PING round trip
    .AddCondition("forecast-provider-down", c => c
        .ForSource("http:forecast")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p
            .TurnOnSwitch("serve-cached-forecast")
            .OncePerCluster())));                                          // one instance acts, shared cooldown
```

`UseRedis("redis:6379")` gives the store and the coordinator a client of their own, even when the application registers an `IConnectionMultiplexer` (a cache, for example); `UseRedis()` uses the registered one. Keep the control state on a Redis without key eviction.

Redis client signals: [Integrations](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#redis).

## Documentation

[Persistence and clusters](https://github.com/engineering87/Homeostat/blob/main/docs/persistence-and-clusters.md)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
