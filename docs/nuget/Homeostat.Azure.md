# Homeostat.Azure

Azure actions for Homeostat through Azure Resource Manager: restart App Service apps and slots and virtual machines, take an Azure Front Door origin out of rotation, or call any Resource Manager operation. No SDK dependency: bring a token from the credential you already use, for example Azure.Identity. Every action requires a person's approval by default.

## Install

```bash
dotnet add package Homeostat.Azure --prerelease
```

This package is a **preview**: it works and is tested, but its API can still change in a minor version (see [Stable and preview packages](https://github.com/engineering87/Homeostat/blob/main/docs/versioning.md#stable-and-preview-packages)), so `--prerelease` stays needed after 1.0.0.

## Example

```csharp
using Azure.Core;
using Azure.Identity;
using Homeostat;

var credential = new DefaultAzureCredential();   // Azure.Identity, referenced by your application

builder.Services.AddHomeostat(h => h
    .UseAzure(o => o.AccessToken = async (services, ct) =>
        (await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct)).Token)
    .AddCondition("api-down", c => c
        .ForSource("alert:WeatherApiDown")
        .Heal(p => p
            .RestartAppService("/subscriptions/{subscription}/resourceGroups/weather-rg/providers/Microsoft.Web/sites/weather-api")
            .OncePerCluster())));   // one instance restarts the app, not every instance
```

Give the identity the narrowest role, scoped to the resource the action targets.

## Documentation

[Azure](https://github.com/engineering87/Homeostat/blob/main/docs/cloud.md#azure)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
