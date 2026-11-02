# Homeostat.AspNetCore

ASP.NET Core endpoints for Homeostat: a status endpoint with conditions, approvals, budget and recent runs; condition history and coverage reports; control endpoints to pause, resume, approve or reject steps and flip switches; an embedded dashboard with one-click approvals; approvals from Slack and Microsoft Teams; and alert ingestion from Prometheus Alertmanager, Grafana and Azure Monitor as signals. The status, control and dashboard endpoints require an authorization policy, the alert endpoints a policy or a token, and the Slack and Teams endpoints a valid signature; only local development can opt out explicitly.

## Install

```bash
dotnet add package Homeostat.AspNetCore --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHomeostat(h => h
    .AddCondition("weather-feed-down", c => c
        .ForSource("weather-feed")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.TurnOnSwitch("serve-cached-forecast", turnOffWhenCleared: true))));
builder.Services.AddAuthorization(o => o.AddPolicy("operations", p => p.RequireRole("operations")));

var app = builder.Build();
app.UseAuthorization();
app.MapHomeostatStatus("operations");      // GET  /homeostat/status, /history, /coverage
app.MapHomeostatControl("operations");     // POST /homeostat/pause, /resume, /approvals/{id}/approve, ...
app.MapHomeostatDashboard("operations");   // GET  /homeostat/dashboard
app.Run();
```

## Documentation

[ASP.NET Core endpoints](https://github.com/engineering87/Homeostat/blob/main/docs/aspnetcore.md)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
