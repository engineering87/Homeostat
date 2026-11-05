# Homeostat.FeatureManagement

Microsoft.FeatureManagement integration for Homeostat: a feature filter that turns a feature off while a Homeostat switch is on (or on, with `EnabledWhen: On`), so healing plans can turn features off through the feature flags your code already reads. Invalid filter parameters are logged once and never make a feature check throw.

## Install

```bash
dotnet add package Homeostat.FeatureManagement --prerelease
```

This package is a **preview**: it works and is tested, but its API can still change in a minor version (see [Stable and preview packages](https://github.com/engineering87/Homeostat/blob/main/docs/versioning.md#stable-and-preview-packages)), so `--prerelease` stays needed after 1.0.0.

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
