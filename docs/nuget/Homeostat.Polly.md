# Homeostat.Polly

Polly v8 integration for Homeostat: the state of the circuit breakers you choose as signals, circuit isolation as a healing action (so a plan can fail fast while a dependency is known to be down), and Polly chaos strategies enabled by Homeostat switches, to rehearse plans during game days.

## Install

```bash
dotnet add package Homeostat.Polly --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;
using Homeostat.Polly;   // TrackCircuitBreaker, CircuitControl, ChaosEnabledBySwitch
using Polly;
using Polly.CircuitBreaker;

builder.Services.AddHomeostat(h => h
    .AddCircuitBreakerSignals()
    .AddCondition("forecast-circuit-open", c => c
        .ForSource("forecast-circuit")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("serve-cached-forecast"))));

// AddResiliencePipeline comes from the Polly.Extensions package.
builder.Services.AddResiliencePipeline("forecast", (pipeline, context) =>
    pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        StateProvider = context.ServiceProvider.TrackCircuitBreaker("forecast-circuit"),
        ManualControl = context.ServiceProvider.CircuitControl("forecast"),   // lets IsolateCircuit("forecast") act on it
    }));
```

Circuit isolation and chaos: [Integrations](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#polly-circuit-isolation-and-game-days).

## Documentation

[Circuit breaker signals](https://github.com/engineering87/Homeostat/blob/main/docs/detectors.md#circuit-breakers-polly-v8)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
