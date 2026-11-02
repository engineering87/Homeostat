# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Platform

- Targets .NET 10 (LTS). .NET 8 and .NET 9 reach end of support on November 10, 2026.
- Dependencies on the current releases: Microsoft.Extensions 10.0.14, Polly.Core 8.9.0,
  Microsoft.Extensions.TimeProvider.Testing 10.11.0; tests on xUnit v3 (VSTest runner, Microsoft Testing Platform off).

### Added

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
- Run history and budget status in `IHomeostatControl`; approval events for observers.
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
