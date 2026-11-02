![Homeostat](https://raw.githubusercontent.com/engineering87/Homeostat/main/assets/banner-light.png)

**Your application notices when something stays broken, fixes it, and checks that the fix worked.**

Homeostat watches signals from your application (health checks, metrics, your own code), recognizes
when a problem persists, runs a guarded remediation plan and **verifies** the outcome: healed (stop),
not healed (escalate to the next step) or inconclusive (stop and leave it to a human).

## Quick look

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<WeatherFeedHealthCheck>("weather-feed");

builder.Services
    .AddHomeostat(h => h
        .AddCondition("weather-feed-down", c => c
            .ForSource("weather-feed")
            .SustainedFor(TimeSpan.FromSeconds(30))
            .Heal(p => p
                .With<RenewFeedClientAction>()
                .Then<SwitchToBackupProviderAction>())))
    .AddHealthCheckSignals();
```

## Safe by default

- **Dry-run on**: logs what it would do until you set `DryRun = false`.
- Global action budget, cooldowns, timeouts, one run at a time, kill switch.
- **Human approval** for risky steps, **suppression** of symptoms while their cause is active,
  **hysteresis** so conditions do not flap.

## Batteries included

- **Detectors**: thread pool starvation, memory pressure, exception rate, HTTP clients, Polly circuit breakers,
  any `System.Diagnostics.Metrics` instrument (OpenTelemetry metrics) or event counter, message broker backlogs,
  dead letters and lag.
- **Conditions**: thresholds, values, trends, anomalies (also by time of day and week), predictions, intermittent
  problems, combinations, hysteresis.
- **Production**: file and Redis stores, plans that run once per cluster, conditions from `appsettings.json`.
- **Actions**: inline lambdas, restart a hosted service, reload configuration, switches for degraded mode.
- **Operations**: dashboard with one-click approvals, condition history and coverage, status and control endpoints,
  command-line tool with offline replay, Homeostat as a health check, PagerDuty, Opsgenie, Slack and Teams (with
  approvals from the channel), JSON Lines audit log, OpenTelemetry traces, metrics and a Grafana dashboard.
- **Testing**: in-memory host with simulated time and fault injection.

## Packages

- `Homeostat`: core engine, runtime detectors, built-in actions, approvals, audit log.
- `Homeostat.AspNetCore`: status, history, coverage and control endpoints, dashboard with one-click approvals, Slack and Teams approvals; alerts from Alertmanager, Grafana and Azure Monitor as signals.
- `Homeostat.HealthChecks`: health check results as signals; Homeostat's own health check.
- `Homeostat.Http`: HTTP client failures and latency as signals; webhook, PagerDuty, Opsgenie, Slack and Microsoft Teams notifiers.
- `Homeostat.Polly`: Polly v8 circuit breaker state as signals; circuit isolation; chaos enabled by switches.
- `Homeostat.Redis`: shared state and coordination across instances; Redis client health signals.
- `Homeostat.Data`: database probes for any ADO.NET provider; SqlClient pool exhaustion; connection pool reset.
- `Homeostat.ServiceBus`, `Homeostat.RabbitMQ`, `Homeostat.Kafka`: backlog, dead letters, consumers and lag as signals; consumers that plans pause and resume.
- `Homeostat.Aspire.Hosting`: Homeostat link and Pause/Resume healing commands in the Aspire dashboard.
- `Homeostat.Caching`: memory cache hit ratio; HybridCache, distributed and memory cache purge actions.
- `Homeostat.FeatureManagement` and `Homeostat.OpenFeature`: feature flags driven by Homeostat switches.
- `Homeostat.Testing`: test host with simulated time.
- `Homeostat.Kubernetes` and `Homeostat.Azure`: platform actions (pods, deployments, nodes, App Service, VMs, Front Door), with approval by default.
- `Homeostat.Cli`: the `homeostat` command-line tool. `Homeostat.Templates`: `dotnet new` templates.

The core package includes Roslyn analyzers (HOMEO001 to HOMEO005).

## Learn more

- [Documentation](https://github.com/engineering87/Homeostat/tree/main/docs)
- [Getting started in 5 minutes](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md)
- [Source code and samples](https://github.com/engineering87/Homeostat)

Preview release: the API may change before 1.0. MIT licensed.
