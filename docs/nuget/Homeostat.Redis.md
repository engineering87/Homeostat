# Homeostat.Redis

Redis for Homeostat. `UseRedis` stores cooldowns, the action budget, the run and condition history and learned baselines in Redis, shared by every instance of the application, and coordinates plans marked `OncePerCluster()` with leases. `AddRedisSignals` watches a Redis client: `PING` round trip and lost or restored connections.

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
    .AddRedisSignals("redis:session-cache", o => o.DegradedAbove = 50)           // milliseconds of PING
    .AddCondition("forecast-provider-down", c => c
        .ForSource("http:forecast")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p
            .TurnOnSwitch("serve-cached-forecast", turnOffWhenCleared: true)
            .OncePerCluster())));                                          // one instance acts, shared cooldown
```

Redis client signals: [Integrations](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#redis).

## Documentation

[Persistence and clusters](https://github.com/engineering87/Homeostat/blob/main/docs/persistence-and-clusters.md)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
