# Homeostat.OpenFeature

OpenFeature integration for Homeostat: a provider that exposes Homeostat switches as boolean flags, alone or next to your flag provider in a multi-provider, so healing plans can turn features off through OpenFeature. An optional key prefix keeps switch names apart from your other flags.

## Install

```bash
dotnet add package Homeostat.OpenFeature --prerelease
```

This package is a **preview**: it works and is tested, but its API can still change in a minor version (see [Stable and preview packages](https://github.com/engineering87/Homeostat/blob/main/docs/versioning.md#stable-and-preview-packages)), so `--prerelease` stays needed after 1.0.0.

## Example

```csharp
using Homeostat;
using Homeostat.OpenFeature;
using OpenFeature;

builder.Services.AddHomeostat(h => h
    .AddCondition("forecast-api-failing", c => c
        .ForSource("http:forecast")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.TurnOnSwitch("serve-cached-forecast"))));

var app = builder.Build();
await Api.Instance.SetProviderAsync(new HomeostatSwitchProvider(app.Services.GetRequiredService<IHomeostatSwitches>()));

var flags = Api.Instance.GetClient();
var cached = await flags.GetBooleanValueAsync("serve-cached-forecast", false);   // true while the switch is on
```

## Documentation

[Feature flags](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#openfeature)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
