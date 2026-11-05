![Homeostat](https://raw.githubusercontent.com/engineering87/Homeostat/main/assets/banner-light.png)

**Your application notices when something stays broken, fixes it, and checks that the fix worked.**

Homeostat watches signals from your application (health checks, metrics, your own code), recognizes
when a problem persists, runs a guarded remediation plan and **verifies** the outcome: healed (stop),
not healed (escalate to the next step) or inconclusive (stop and leave it to a human).

## Install

```bash
dotnet add package Homeostat --prerelease
dotnet add package Homeostat.HealthChecks --prerelease   # health checks as signals, used below
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<WeatherFeedClient>();                      // your client
builder.Services.AddHealthChecks()
    .AddCheck("weather-feed", () => HealthCheckResult.Healthy());       // your health check: the signal source

builder.Services
    .AddHomeostat(h => h
        .AddCondition("weather-feed-down", c => c
            .ForSource("weather-feed")
            .SustainedFor(TimeSpan.FromSeconds(30))
            .Heal(p => p
                .With("renew-client", sp => sp.GetRequiredService<WeatherFeedClient>().Renew())   // step 1
                .Then("switch-to-backup", sp => sp.GetRequiredService<WeatherFeedClient>().UseBackup())
                .RequireApproval())))                                                         // step 2, after a person's yes
    .AddHealthCheckSignals(o => o.Interval = TimeSpan.FromSeconds(10));                                 // Homeostat.HealthChecks

var app = builder.Build();
app.Run();

public sealed class WeatherFeedClient
{
    public void Renew() { /* recreate the connection */ }
    public void UseBackup() { /* switch to the secondary provider */ }
}
```

Homeostat starts in **dry-run**: it logs what it would do (`[dry-run] Condition weather-feed-down: step 1/2 would
execute renew-client.`) and changes nothing. When the decisions look right, add `.Configure(o => o.DryRun = false)`.

## Safe by default

- Dry-run is on until you set `DryRun = false`: Homeostat only logs what it would do.
- A global action budget, cooldowns, timeouts, one run at a time and a kill switch bound what it can do.
- Risky steps wait for a person's approval, symptoms are suppressed while their cause is active, and hysteresis keeps
  conditions from flapping.

## Batteries included

| Area | What is included |
|---|---|
| Starter presets | `AddWebApiDefaults()` and `AddWorkerDefaults()` register the usual detectors with an observe-only condition for each; give a condition a plan when its activations look right |
| Detectors | Thread pool starvation, memory pressure, exception rate, HTTP and gRPC clients, Entity Framework Core commands, call signals for any other client (`AddCallSignals`), database probes and connection pools, Polly circuit breakers, any `System.Diagnostics.Metrics` instrument (OpenTelemetry metrics) or event counter, message broker backlogs, dead letters and lag |
| Conditions | Thresholds, values, trends, anomalies (also by time of day and week), predictions, intermittent problems, combinations, hysteresis |
| Production | File and Redis stores, plans that run once per cluster, conditions from `appsettings.json` |
| Actions | Inline lambdas, restart a hosted service, reload configuration, switches for degraded mode |
| Operations | Dashboard with one-click approvals, condition history and coverage, status and control endpoints, command-line tool with offline replay, Homeostat as a health check, PagerDuty, Opsgenie, Slack and Teams (with approvals from the channel), JSON Lines audit log, OpenTelemetry traces and metrics, a Grafana dashboard |
| Testing | In-memory host with simulated time and fault injection |

## Packages

- `Homeostat`: core engine, runtime detectors, starter presets, call signals, database probes for any ADO.NET provider, connection pool signals for Npgsql and SqlClient, built-in actions, approvals, audit log.
- `Homeostat.AspNetCore`: status, history, coverage and control endpoints, dashboard with one-click approvals, Slack and Teams approvals; alerts from Alertmanager, Grafana and Azure Monitor as signals.
- `Homeostat.HealthChecks`: health check results as signals; Homeostat's own health check.
- `Homeostat.Http`: HTTP client failures and latency as signals (HTTP-level for gRPC clients); webhook, PagerDuty, Opsgenie, Slack and Microsoft Teams notifiers.
- `Homeostat.Grpc`: gRPC client failures and latency as signals, judged on the gRPC status, streaming calls included.
- `Homeostat.Polly`: Polly v8 circuit breaker state as signals; circuit isolation; chaos enabled by switches.
- `Homeostat.Redis`: shared state and coordination across instances; Redis client health signals.
- `Homeostat.EntityFrameworkCore`: command failures and latency of a `DbContext` as signals, for any relational provider; connectivity probe.
- `Homeostat.ServiceBus`, `Homeostat.RabbitMQ`, `Homeostat.Kafka`, `Homeostat.Artemis`: backlog, dead letters, consumers, lag and broker connections as signals; consumers that plans pause and resume.
- `Homeostat.MongoDB`: ping, primary availability, connection pool exhaustion and replication lag as signals.
- `Homeostat.Aspire.Hosting`: Homeostat link and Pause/Resume healing commands in the Aspire dashboard.
- `Homeostat.Caching`: memory cache hit ratio; HybridCache, distributed and memory cache purge actions.
- `Homeostat.FeatureManagement` and `Homeostat.OpenFeature`: feature flags driven by Homeostat switches.
- `Homeostat.Testing`: test host with simulated time.
- `Homeostat.Kubernetes` and `Homeostat.Azure`: platform actions (pods, deployments, nodes, App Service, VMs, Front Door), with approval by default.
- `Homeostat.Cli`: the `homeostat` command-line tool. `Homeostat.Templates`: `dotnet new` templates.

`Homeostat.Grpc`, `Homeostat.EntityFrameworkCore`, `Homeostat.Caching`, `Homeostat.FeatureManagement`, `Homeostat.OpenFeature`, `Homeostat.Azure` and `Homeostat.Aspire.Hosting` are [previews](https://github.com/engineering87/Homeostat/blob/main/docs/versioning.md#stable-and-preview-packages): tested, but their API can still change in a minor version.

The core package includes [build-time analyzers](https://github.com/engineering87/Homeostat/blob/main/docs/analyzers.md)
that catch mistakes that compile but misbehave at runtime. The [package index](https://github.com/engineering87/Homeostat/blob/main/docs/packages.md)
lists every package with its install command.

## Documentation

- [Documentation](https://github.com/engineering87/Homeostat/tree/main/docs)
- [Getting started in 5 minutes](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md)
- [Source code and samples](https://github.com/engineering87/Homeostat)
- [Versioning and support](https://github.com/engineering87/Homeostat/blob/main/docs/versioning.md): semantic versioning
  from 1.0.0; this release candidate changes only if a problem found in it requires it.

MIT licensed.
