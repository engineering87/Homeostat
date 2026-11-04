# Homeostat.HealthChecks

Turns the results of ASP.NET Core and `Microsoft.Extensions` health checks into Homeostat signals: each health check name becomes a signal source, and its status the severity. It also adds a health check that reports Homeostat itself (paused, budget exhausted, a step waiting for approval, an active condition), which is never turned back into a signal.

## Install

```bash
dotnet add package Homeostat.HealthChecks --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;

builder.Services.AddHealthChecks()
    .AddCheck<WeatherFeedHealthCheck>("weather-feed")   // your health check: "weather-feed" becomes the signal source
    .AddHomeostatCheck();                               // Homeostat's own state, for /health

builder.Services.AddHomeostat(h => h
        .AddCondition("weather-feed-down", c => c
            .ForSource("weather-feed")
            .SustainedFor(TimeSpan.FromSeconds(30))
            .Heal(p => p.TurnOnSwitch("serve-cached-forecast"))))
    .AddHealthCheckSignals(o => o.Interval = TimeSpan.FromSeconds(10));
```

`Interval` sets `HealthCheckPublisherOptions.Period`, which applies to every health check publisher of the application.

## Documentation

[Health checks as signals](https://github.com/engineering87/Homeostat/blob/main/docs/signals.md#health-checks-as-signals)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
