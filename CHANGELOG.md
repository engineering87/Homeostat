# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Platform

- Targets .NET 10 (LTS). .NET 8 and .NET 9 reach end of support on November 10, 2026.
- Dependencies on the current releases: Microsoft.Extensions 10.0.14, Polly.Core 8.9.0, StackExchange.Redis 3.4.29,
  Microsoft.FeatureManagement 4.8.0, OpenFeature 2.14.1, System.CommandLine 2.0.14,
  Microsoft.Extensions.TimeProvider.Testing 10.11.0; analyzers on Microsoft.CodeAnalysis.CSharp 5.0.0 (the Roslyn of
  the .NET 10.0.1xx SDK, on purpose); tests on xUnit v3 (VSTest runner, Microsoft Testing Platform off).

### Fixed

- `RestartHostedService<T>()` on a `BackgroundService` stopped the whole application: the generic host treats a
  background service stopped outside a shutdown as failed (`BackgroundServiceExceptionBehavior.StopHost`). Workers
  now derive from `RestartableBackgroundService`, restarted inside the service; a plain `BackgroundService` is
  refused unless the host ignores background service failures.

### Security

- Control endpoints require an `Authorization: Bearer` token or the `X-Requested-With` header, and refuse requests
  that browsers mark as cross-site: protection against cross-site request forgery with cookies, Windows
  authentication, Basic or client certificates.
- Notifier HTTP clients no longer log request URLs, which carry the webhook secret.

### Added

- Operations and developer experience (wave 3):
  - `MapHomeostatDashboard`: embedded dashboard with one-click approvals, switches, pause and recent runs; strict
    Content-Security-Policy, text-only rendering.
  - Notifiers in `Homeostat.Http`: `AddPagerDutyIncidents` (Events API v2), `AddOpsgenieAlerts` (Jira Service
    Management Operations by default, or Opsgenie), `AddSlackNotifications` (Block Kit, optional approval buttons),
    `AddTeamsNotifications` (Adaptive Cards); only network errors, 408, 429 and 5xx are retried.
  - `MapHomeostatSlackInteractions`: Slack approval buttons, with signature, request age and allowed users checks.
  - `Homeostat.Cli`: the `homeostat` tool (status, pause, resume, approve, reject, switch, signal).
  - `Homeostat.Templates`: `homeostat-webapi` and `homeostat-worker`.
  - Analyzers HOMEO001 to HOMEO005, shipped in the `Homeostat` package.
  - `RestartableBackgroundService` and `AddBackgroundServiceSignals`.
  - Gauges `homeostat.conditions`, `homeostat.approvals.pending`, `homeostat.switches.on`, `homeostat.paused`,
    `homeostat.dry_run`; Grafana dashboard in `deploy/grafana`.
  - Trend and anomaly rules from configuration (`RisingFasterThan`, `FallingFasterThan`, `AnomalouslyHigh`,
    `AnomalouslyLow`).
  - Worker service sample.
- Integrations (wave 2), see `docs/integrations.md`:
  - Probes (`AddProbe`, `AddLatencyProbe`): any number or round trip as a signal value, with thresholds, interval
    and timeout; probes run in the background in their own scope without delaying other detectors, and a hanging
    call is cancelled and never stacked with new ones.
  - `Homeostat.Data`: `AddDatabaseProbe` for any ADO.NET provider, failures tagged `transient` from
    `DbException.IsTransient`, and the `ClearConnectionPools<TConnection>()` action.
  - `Homeostat.Redis`: `AddRedisSignals` (ping round trip and connection events of the Redis client);
    `UseRedis(connectionString)` no longer aborts startup when Redis is down.
  - `Homeostat.Caching`: `AddMemoryCacheSignals` (hit ratio) and the `PurgeHybridCacheTags`, `RemoveHybridCacheKeys`,
    `RemoveDistributedCacheKeys` and `CompactMemoryCache` actions.
  - `Homeostat.AspNetCore`: `MapHomeostatAlerts`, receiving Prometheus Alertmanager, Grafana and Azure Monitor
    (common alert schema) notifications and generic signals, protected by a policy or a token, with size limits.
    Alerts are kept as state per source (worst firing series wins, Healthy only when none fires), republished at
    every detection interval and expired after `FiringTimeToLive`; sources carry the `alert:` prefix.
  - `Homeostat.FeatureManagement` (`Homeostat.Switch` feature filter) and `Homeostat.OpenFeature`
    (`HomeostatSwitchProvider`): feature flags driven by switches.
  - `Homeostat.Polly`: `CircuitControl` and the `IsolateCircuit` action (manual circuit isolation driven by a switch),
    `ChaosEnabledBySwitch` for chaos strategies during game days.
  - `IHomeostatSwitches.Changed` event, raised when a switch is turned on or off.
- Persistent state: `IHomeostatStore` with in-memory, file (`UseFileStore`) and Redis stores; atomic plan-state
  updates (cooldown, backoff, holds), budget and run history survive restarts; fail-closed when the store is
  unavailable, with the status endpoint still answering.
- Several instances: `IHomeostatCoordinator` and `IHomeostatLease`, `OncePerCluster()` plans with renewed leases
  that stop the run when the lease may be lost, `InstanceId` (machine name by default).
- `Homeostat.Redis`: shared store (atomic budget script, capped history) and coordinator (`UseRedis`).
- Metric signals from any `System.Diagnostics.Metrics` instrument (`AddMetricSignal`), with
  `AddServerErrorRateSignal` and `AddServerLatencySignal`; `Signal.Value` filled by every detector.
- Conditions on values, trends and anomalies (`WhenValueAbove`, `WhenRisingFasterThan`, `WhenAnomalouslyHigh`...),
  intermittent problems (`Occurrences`) and combinations (`WhenAllActive`, `WhenAnyActive`).
- Conditions and plans from configuration (`AddConditionsFromConfiguration`, `MapAction`) with strict validation,
  reloaded at runtime without touching unchanged conditions.
- Cooldown backoff, compensating actions (`UndoWith`), maintenance windows, `PauseUntil` and `PauseReason`
  (`POST /homeostat/pause?for=...`).

- Signals, conditions, healing plans with escalation and three-outcome verification.
- Guardrails: dry-run by default, action budget, cooldown, concurrency limit, timeouts, kill switch.
- `Homeostat.HealthChecks`: health check results as signals.
- Detectors: thread pool starvation, memory pressure, exception rate, custom `ISignalDetector`.
- `Homeostat.Http`: failure ratio and p95 latency of `IHttpClientFactory` clients as signals.
- `Homeostat.Polly`: Polly v8 circuit breaker state as signals.
- Inline lambda actions and built-in actions: restart a hosted service, reload configuration,
  turn on a switch (`IHomeostatSwitches`).
- `IHealingObserver` with public run and step results.
- `Homeostat.Testing`: in-memory test host with simulated time, `HealingContextFactory`,
  HTTP fault injection.
- Tracing and metrics (`Homeostat` activity source and meter).
- Hysteresis on recovery (`ClearAfter`) and suppression of symptoms (`SuppressedBy`, with startup validation).
- Human approval for risky steps (`RequireApproval`, `IHomeostatApprovals`, `NotApproved` outcome); the
  concurrency slot is released while waiting, a rejection holds the plan until the condition clears.
- `ConditionCleared` outcome: a run stops before a step that is no longer needed.
- Automatic switch reset when a condition clears (`TurnOnSwitch(name, turnOffWhenCleared: true)`).
- Run history and budget status in `IHomeostatControl` (`GetRecentRunsAsync`, `GetBudgetAsync`); approval events
  for observers.
- Audit log in JSON Lines (`AddAuditLog`), webhook notifier (`AddWebhookNotifications`) and a base class for
  custom sinks (`HomeostatEventSink`, `AddEventSink`); events raised during shutdown are delivered.
- `Homeostat.AspNetCore`: status and control endpoints (`MapHomeostatStatus`, `MapHomeostatControl`), which
  require an authorization policy unless explicitly mapped for anonymous use.
- Homeostat's own health check (`AddHomeostatCheck`).
- Release workflow on version tags, package validation, XML documentation for the whole public API.
- Conventional Commits enforced by commitlint: local `commit-msg` hook and a CI workflow that also checks
  pull request titles; Dependabot writes compliant commit messages.
- Weather feed sample and unit tests.
- Logo, package icon and social preview image.
- Documentation: getting started, concepts, signals, detectors, conditions, healing plans, actions,
  guardrails, observability, notifications and audit, ASP.NET Core endpoints, testing, recipes,
  FAQ and troubleshooting, design notes, roadmap.
- Dedicated NuGet package README.
- Issue and pull request templates.
