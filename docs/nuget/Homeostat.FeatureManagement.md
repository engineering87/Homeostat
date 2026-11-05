# Homeostat.FeatureManagement

Microsoft.FeatureManagement integration for Homeostat: a feature filter that turns a feature off while a Homeostat switch is on (or on, with `EnabledWhen: On`), so healing plans degrade gracefully through the feature flags your code already reads.

## Install

```bash
dotnet add package Homeostat.FeatureManagement --prerelease
```

The current version is a release candidate (`1.0.0-rc.2`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;
using Microsoft.FeatureManagement;

builder.Services.AddFeatureManagement().AddHomeostatSwitchFilter();

builder.Services.AddHomeostat(h => h
    .AddCondition("forecast-maps-slow", c => c
        .ForSource("http:maps")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("shed-forecast-maps"))));

// appsettings.json:
// "FeatureManagement": {
//   "ForecastMaps": {
//     "EnabledFor": [ { "Name": "Homeostat.Switch", "Parameters": { "Switch": "shed-forecast-maps" } } ]
//   }
// }
```

## Documentation

[Feature flags](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#microsoftfeaturemanagement)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
