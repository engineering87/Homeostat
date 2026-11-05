# Homeostat.Aspire.Hosting

.NET Aspire hosting integration for Homeostat. In the app host, it adds a **Homeostat** link to the application's Homeostat dashboard and **Pause healing** and **Resume healing** commands to the resource in the Aspire dashboard, useful while you debug a failure that Homeostat would otherwise heal. **Pause healing** pauses for 30 minutes by default (`PauseFor`).

## Install

```bash
dotnet add package Homeostat.Aspire.Hosting --prerelease   # in the app host project
```

This package is a **preview**: it works and is tested, but its API can still change in a minor version (see [Stable and preview packages](https://github.com/engineering87/Homeostat/blob/main/docs/versioning.md#stable-and-preview-packages)), so `--prerelease` stays needed after 1.0.0.

## Example

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.WeatherApi>("weather-api")   // an application that maps the Homeostat endpoints
       .WithHomeostat(o => o.PauseFor = TimeSpan.FromHours(1));

builder.Build().Run();
```

The application itself references `Homeostat` and `Homeostat.AspNetCore` and maps `MapHomeostatStatus`, `MapHomeostatControl` and `MapHomeostatDashboard`. When those endpoints require a policy, give the commands a token with `o.BearerToken`.

## Documentation

[.NET Aspire](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#net-aspire)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
