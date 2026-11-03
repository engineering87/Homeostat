# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

Nothing yet.

## [1.0.0-rc.1] - 2026-11-02

First release candidate of Homeostat, a self-healing library for .NET 10 applications. Homeostat turns health
information (detectors, metrics, probes, health checks, broker and cloud statistics, external alerts) into signals,
evaluates them against conditions, and runs healing plans with verification, escalation and human approval when a
condition becomes active. Every action runs behind guardrails: dry-run by default, budgets, cooldowns, a kill switch
and persistent state shared across instances. The public API is now listed and checked, and is expected to stay
stable through 1.0.0.

### Added

#### Core engine and guardrails

- Signals, conditions and healing plans with escalation and three-outcome verification.
- Guardrails: dry-run by default, action budget, cooldown with backoff, concurrency limit, timeouts, kill switch,
  maintenance windows, `PauseUntil` and `PauseReason` (`POST /homeostat/pause?for=...`).
- Human approval for risky steps (`RequireApproval`, `IHomeostatApprovals`, `NotApproved` outcome): the concurrency
  slot is released while waiting, and a rejection holds the plan until the condition clears.
- `ConditionCleared` outcome: a run stops before a step that is no longer needed.
- Persistent state: `IHomeostatStore` with in-memory, file (`UseFileStore`) and Redis stores; atomic plan-state
  updates (cooldown, backoff, holds); budget and run history survive restarts; fail-closed when the store is
  unavailable, with the status endpoint still answering. Condition events refused by a failing store are retried.
- Several instances: `IHomeostatCoordinator` and `IHomeostatLease`, `OncePerCluster()` plans with renewed leases that
  stop the run when the lease may be lost, `InstanceId` (machine name by default).
- `IHomeostatControl` with run history, budget status, condition history and coverage (`GetRecentRunsAsync`,
  `GetBudgetAsync`, `GetConditionHistoryAsync`, `GetCoverageAsync`).
- `IHomeostatSwitches` with the `Changed` event, raised when a switch is turned on or off.
- `AddPauseGate(name)` registers a keyed `PauseGate`, so workers resolve a gate whose paused state survives restarts.
- `IHealingObserver` with public run and step results and approval events.
- Tracing and metrics (`Homeostat` activity source and meter); gauges `homeostat.conditions`,
  `homeostat.approvals.pending`, `homeostat.switches.on`, `homeostat.paused`, `homeostat.dry_run`.
- Homeostat's own health check (`AddHomeostatCheck`).

#### Conditions

- Conditions on values, trends and anomalies (`WhenValueAbove`, `WhenRisingFasterThan`, `WhenAnomalouslyHigh`...),
  intermittent problems (`Occurrences`) and combinations (`WhenAllActive`, `WhenAnyActive`).
- Seasonal anomalies `WhenSeasonallyHigh` and `WhenSeasonallyLow` with `Seasonality.Daily()` and
  `Seasonality.Weekly()` (time slots, time zone); learned baselines are kept in the store and survive restarts and
  deployments.
- Predictive conditions `WhenProjectedAbove` and `WhenProjectedBelow`, which clear soon after a fix when the latest
  values stop moving toward the threshold.
- Hysteresis on recovery (`ClearAfter`) and suppression of symptoms (`SuppressedBy`, with startup validation).
- Conditions and plans from configuration (`AddConditionsFromConfiguration`, `MapAction`) with strict validation,
  reloaded at runtime without touching unchanged conditions; includes `RisingFasterThan`, `FallingFasterThan`,
  `AnomalouslyHigh`, `AnomalouslyLow`, `SeasonallyHigh`, `SeasonallyLow`, `ProjectedAbove`, `ProjectedBelow` and
  `Adaptive`.
- Persisted condition history: activations and clears with the causing signal, in the file and Redis stores
  (`ConditionHistorySize`, 200 by default; `RedisHomeostatOptions.DocumentRetention`); history reads fall back to this
  instance's events when the store fails.

#### Healing plans and actions

- Inline lambda actions and compensating actions (`UndoWith`).
- Built-in actions: restart a hosted service, reload configuration, turn on a switch, with automatic reset when the
  condition clears (`TurnOnSwitch(name, turnOffWhenCleared: true)`).
- `RestartableBackgroundService`, restarted inside the service, and `AddBackgroundServiceSignals`.
- `RestartApplication()` (approval by default): graceful stop with an exit code for the orchestrator to restart;
  `RestartGuard` and `RestartLimits` enforce a minimum uptime and a maximum number of restarts per window, counted in
  the store.

#### Detectors and signals

- Detectors: thread pool starvation, memory pressure, exception rate, custom `ISignalDetector`; `Signal.Value` is
  filled by every detector.
- Probes (`AddProbe`, `AddLatencyProbe`): any number or round trip as a signal value, with thresholds, interval and
  timeout; probes run in the background in their own scope, and a hanging call is canceled and never stacked.
- Metric signals from any `System.Diagnostics.Metrics` instrument (`AddMetricSignal`), with
  `AddServerErrorRateSignal`, `AddServerLatencySignal` and `MetricSignalBuilder.Scale(factor)`.
- Any event counter as a signal (`AddEventCounterSignal`), enabling only the counters it needs.
- Starter presets `AddWebApiDefaults()` and `AddWorkerDefaults()`: the thread pool, memory and exception detectors
  (plus the server error rate and latency signals for web APIs, and `AddBackgroundServiceSignals` for workers) with
  the observe-only conditions `threadpool-starved`, `memory-pressure`, `exception-rate-high`, `server-errors-high` and
  `server-latency-high`; the server conditions are suppressed while the thread pool is starved (latency also under
  memory pressure). Each `DefaultCondition` can be turned off (`Enabled`), given another `SustainedFor`, configured
  (`Configure`, for example `WhenValueAbove` for another threshold) or given a plan (`Heal`). One preset per
  application.
- Call signals for any client (`AddCallSignals(source)`): a keyed `CallRecorder` records calls (`Record`,
  `Record(failed)` without a duration, `TrackAsync`, which ignores cancellations requested by the caller) and publishes their failure ratio as the signal
  value, with the p95 duration in the tags; thresholds in `CallSignalOptions`. An `IServiceCollection` overload with an
  `origin` serves integration packages.
- `Homeostat.HealthChecks`: health check results as signals.

#### Integrations: data, Entity Framework Core, caching, Redis, MongoDB, HTTP and gRPC, Polly, feature flags

- `Homeostat.Data`: `AddDatabaseProbe` for any ADO.NET provider (failures tagged `transient` from
  `DbException.IsTransient`), the `ClearConnectionPools<TConnection>()` action and `AddSqlClientPoolSignal` for
  SqlClient pool exhaustion.
- `AddNpgsqlPoolSignal` (Homeostat.Data): Npgsql 10+ pool usage as a share of the maximum, from the `Npgsql` meter.
- `Homeostat.EntityFrameworkCore`: `AddDbContextSignals<TContext>()` publishes the failure ratio and p95 duration of
  the commands of a `DbContext` (`efcore:{context type name}`) through an interceptor added with `ConfigureDbContext`,
  leaving `AddDbContext`, `AddDbContextPool` and `AddDbContextFactory` unchanged (a second call updates the settings,
  never adds a second interceptor); timeouts, transient errors and failed connections count, command errors and
  cancellations do not (`DbContextSignalOptions.IsDatabaseFailure`). `AddDbContextProbe<TContext>()` checks
  `Database.CanConnectAsync` (`efcore:{context type name}:ping`), also for contexts registered only with
  `AddDbContextFactory`. Any relational
  provider; EF Core 10.
- `Homeostat.Caching`: `AddMemoryCacheSignals` (hit ratio) and the `PurgeHybridCacheTags`, `RemoveHybridCacheKeys`,
  `RemoveDistributedCacheKeys` and `CompactMemoryCache` actions.
- `Homeostat.Redis`: shared store (atomic budget script, capped history) and coordinator (`UseRedis`); `AddRedisSignals` (ping round trip and connection events).
- `Homeostat.MongoDB`: ping latency, primary availability and connection pool timeouts from the driver events
  (`MongoClientSettings.AddHomeostatSignals`), replication lag (opt-in).
- `Homeostat.Http`: failure ratio and p95 latency of `IHttpClientFactory` clients as signals; gRPC failures
  (grpc-status Unknown, DeadlineExceeded, ResourceExhausted, Internal, Unavailable, DataLoss in response headers) are
  counted by `AddHomeostatSignals` through `HttpClientSignalOptions.IsGrpcFailure`.
- `Homeostat.Grpc`: `AddHomeostatGrpcSignals()` on the builder of `AddGrpcClient` publishes the failure ratio of a
  gRPC client's calls and the p95 duration of its unary calls (`grpc:{client name}`), judged on the gRPC status
  (`GrpcSignalOptions.IsFailure`, by default `IsServiceFailure`; `Cancelled` is never recorded). Streaming calls are
  recorded without a duration when their stream ends, with the status from the trailers, or as soon as they cannot
  start. A second call on the same client updates the settings, never adds a second interceptor.
  `HomeostatGrpcInterceptor` for channels built without the client factory.
- `Homeostat.Polly`: Polly v8 circuit breaker state as signals, `CircuitControl` and the `IsolateCircuit` action
  (manual isolation driven by a switch), `ChaosEnabledBySwitch` for chaos strategies during game days.
- `Homeostat.FeatureManagement` (`Homeostat.Switch` feature filter) and `Homeostat.OpenFeature`
  (`HomeostatSwitchProvider`): feature flags driven by switches.

#### Message brokers

- Pausable consumers in the core: `IPausableConsumer`, `IHomeostatConsumers`, `AddPausableConsumer`, the plan step
  `PauseConsumer(name)` and the switch `consumer:{name}:paused` (also from the dashboard and the CLI).
- `PauseGate`: pauses any receive loop (AMQP receivers, change streams, polling workers).
- `Homeostat.ServiceBus`: active and dead-letter counts of queues and subscriptions as signals; pausable
  `ServiceBusProcessor` and `ServiceBusSessionProcessor`.
- `Homeostat.RabbitMQ`: ready messages, consumers and dead letters of a queue as signals (passive declare on a
  short-lived channel); `PausableRabbitMqConsumer`.
- `Homeostat.Kafka`: consumer lag, broker connections and producer queue use from the client statistics
  (`SetHomeostatSignals` on consumer and producer builders, `KafkaSignalReporter`); `KafkaPauseGate`, applied on the
  consumer's own thread, and `AddKafkaPauseGate(name)` for a keyed gate that survives restarts.
- `Homeostat.Artemis`: message and consumer counts of ActiveMQ Artemis queues and their dead-letter queue, through
  the broker's Jolokia management API (no client library).

#### Cloud and orchestrators

All actions require approval by default.

- `Homeostat.Kubernetes` (`UseKubernetes`): `RestartPod` (only pods with a controller), `RolloutRestart`,
  `ScaleDeployment` (only in the requested direction, conditional update), `CordonNode` (leaves cordoned nodes
  alone); in-cluster REST client trusting the cluster CA, rotating service account token, validated names.
- `Homeostat.Azure` (`UseAzure` with a token from Azure.Identity): `RestartAppService`, `RestartVirtualMachine`,
  `DisableFrontDoorOrigin` (never the last enabled origin, enabled again when it did not help),
  `AzureResourceManager`; resource ids validated against the exact type.

#### ASP.NET Core endpoints and dashboard

- `Homeostat.AspNetCore`: status and control endpoints (`MapHomeostatStatus`, `MapHomeostatControl`), which require an
  authorization policy unless explicitly mapped for anonymous use; `GET /homeostat/history` and
  `GET /homeostat/coverage`.
- `MapHomeostatDashboard`: embedded dashboard with one-click approvals, switches, pause, recent runs, condition
  history and 24-hour coverage panels; strict Content-Security-Policy, text-only rendering.
- `MapHomeostatAlerts`: receives Prometheus Alertmanager, Grafana and Azure Monitor (common alert schema)
  notifications and generic signals, protected by a policy or a token, with size limits. Alerts are kept as state per
  source (worst firing series wins, Healthy only when none fires), republished at every detection interval, expired
  after `FiringTimeToLive`, with the `alert:` source prefix.
- `MapHomeostatSlackInteractions`: Slack approval buttons, with signature, request age and allowed users checks.
- `MapHomeostatTeamsCommands`: approvals from Microsoft Teams through an outgoing webhook, with HMAC signature,
  message age and id checks and approvers by Microsoft Entra object ID; `TeamsOptions.ApprovalCommand` adds the
  instructions to approval cards.

#### Notifications and audit

- Audit log in JSON Lines (`AddAuditLog`), webhook notifier (`AddWebhookNotifications`) and a base class for custom
  sinks (`HomeostatEventSink`, `AddEventSink`); events raised during shutdown are delivered.
- Notifiers in `Homeostat.Http`: `AddPagerDutyIncidents` (Events API v2), `AddOpsgenieAlerts` (Jira Service Management
  Operations by default, or Opsgenie), `AddSlackNotifications` (Block Kit, optional approval buttons),
  `AddTeamsNotifications` (Adaptive Cards); only network errors, 408, 429 and 5xx are retried.

#### Learning and replay

- `Adaptive()` plans: steps with enough attempts of their own are ordered by measured success in the run history;
  new steps keep their position; approval steps are barriers.
- `RecordSignals`: daily JSON Lines files per instance with retention, owner-only file permissions on Unix, messages
  and tags only on request; `RecordedSignal.Read`.
- `HomeostatReplay` (Homeostat.Testing): replays recordings in enforced dry-run on simulated time, isolated from
  stores, detectors, notifiers, logging providers and telemetry, with a timeline and per-condition summary;
  `HomeostatReplay.RunFilesAsync(paths, IConfiguration, options)` merges recording files in time order.
- Coverage reports from the persisted condition history, across instances and restarts, with failed steps and a
  warning when the run history does not cover the period.

#### Tooling: CLI, templates, analyzers, Aspire

- `Homeostat.Cli`: the `homeostat` tool (`status`, `pause`, `resume`, `approve`, `reject`, `switch`, `signal`,
  `history`, `coverage --fail-below`, and `replay` with the application's `HomeostatOptions` and `--max-heals`, exit
  code 5, for pipelines).
- `Homeostat.Templates`: `homeostat-webapi` and `homeostat-worker`, built in CI from the packed packages.
- Analyzers HOMEO001 to HOMEO005, shipped in the `Homeostat` package.
- `Homeostat.Aspire.Hosting`: `WithHomeostat()` adds a Homeostat dashboard link and Pause/Resume healing commands to
  resources in the Aspire dashboard.
- Grafana dashboard in `deploy/grafana`.

#### Testing

- `Homeostat.Testing`: in-memory test host with simulated time, `HealingContextFactory`, HTTP fault injection;
  `HomeostatTestHost` runs startup option validation, like the generic host.
- Weather feed and worker service samples; unit tests.

#### Trimming and Native AOT

- Every library except `Homeostat.Aspire.Hosting` and the tools is `IsAotCompatible` and builds without trimming or AOT warnings. JSON is written
  with source-generated metadata or `JsonNode` (stores, audit log, recordings, notifiers, Kubernetes, Azure and Artemis
  requests); the endpoints use the request delegate generator and write their answers, problem details included,
  without reflection.
- `tests/Homeostat.AotSmoke` is published with Native AOT and run in CI, covering the Slack and Teams endpoints and the
  PagerDuty and Opsgenie notifiers. See `docs/native-aot.md`.

#### Performance

- A signal is offered only to the conditions that listen to its source (and those with a custom filter), so the
  engine's cost per signal does not grow with the number of conditions; trend, projection and anomaly rules keep
  running sums instead of scanning their window; the per-signal path does not allocate in steady state.
- `benchmarks/Homeostat.Benchmarks` and `docs/performance.md`: measured cost of publishing, of the engine per signal
  and of the HTTP client handler.

#### Public API and supply chain

- Public API listings in `api/`, one per package, checked by `PublicApiTests` (`HOMEOSTAT_ACCEPT_API=1` updates
  them); API rules for 1.0 in `CONTRIBUTING.md`; XML documentation for the whole public API.
- Release workflow on version tags with package validation, an SBOM (SPDX) and a build provenance attestation per
  package (also attached to the release as `.intoto.jsonl`), and package signing when a certificate is configured;
  every action pinned to a commit, checkouts without persisted credentials, NuGet restores in locked mode; CodeQL and
  OpenSSF Scorecard workflows; a security policy with coordinated disclosure timelines.
- Property-based tests (FsCheck) for the invariants of call signals (exact failure ratio, monotonic severity, every
  call counted once, p95 within the recorded durations) and of signal series (trend of a straight line, values kept
  exactly inside the window).
- Conventional Commits enforced by commitlint (local `commit-msg` hook and a CI workflow that also checks pull request
  titles); Dependabot writes compliant commit messages.
- Per-package NuGet READMEs; logo, package icon and social preview image; issue and pull request templates.
- Documentation: getting started, concepts, signals, detectors, conditions, healing plans, actions, guardrails,
  integrations, observability, notifications and audit, ASP.NET Core endpoints, testing, Native AOT, performance,
  recipes, FAQ and troubleshooting, design notes, roadmap, versioning, platforms, security hardening and a package
  index.

### Security

- Control endpoints require an `Authorization: Bearer` token or the `X-Requested-With` header, and refuse requests
  that browsers mark as cross-site or same-site (`Sec-Fetch-Site`): protection against cross-site request forgery with cookies, Windows
  authentication, Basic or client certificates.
- Notifier HTTP clients no longer log request URLs, which carry the webhook secret.

### Platform

- Targets .NET 10 (LTS). .NET 8 and .NET 9 reach end of support on November 10, 2026.
- Dependencies: Microsoft.Extensions 10.0.14, Polly.Core 8.9.0, StackExchange.Redis 3.4.29,
  Azure.Messaging.ServiceBus 7.21.0, RabbitMQ.Client 7.2.2, Confluent.Kafka 2.16.0, MongoDB.Driver 3.13.0,
  Aspire.Hosting 13.6.4, Microsoft.FeatureManagement 4.8.0, OpenFeature 2.14.1, System.CommandLine 2.0.14,
  Microsoft.Extensions.TimeProvider.Testing 10.11.0; analyzers on Microsoft.CodeAnalysis.CSharp 5.0.0 (the Roslyn of
  the .NET 10.0.1xx SDK, on purpose); tests on xUnit v3 (VSTest runner, Microsoft Testing Platform off).

### Breaking changes for users of the 0.1.0 previews

- `AddMongoSignals` and `MongoClientSettings.AddHomeostatSignals` name the deployment `default` when no name is given
  (sources `mongodb:default:ping`...; previously `mongodb:mongodb:ping`).
- The package version is now 1.0.0-rc.1.
- `HealingRunOutcome`, `VerificationOutcome` and `ApprovalStatus` are numbered from 1, so an unset value is never a
  success. Stores, the audit log and recordings write the names, so saved data is not affected by the numbering.
- `HealingRunOutcome.Cancelled` and `ApprovalStatus.Cancelled` are renamed `Canceled`. Stores written by a preview
  with `Cancelled` in run records cannot be read back: delete the run history or the store file.
- Default signal sources follow the `{technology}[:{name}][:{metric}]` convention: `memory-cache` is `cache:memory`,
  `sqlclient-pool` is `sqlclient:pool`, `http-server-errors` is `http-server:errors`, `http-server-latency` is
  `http-server:latency`. Update conditions and configuration that refer to the old names.
- `UseFileStore(path, configure)` with `FileStoreOptions.PlanStateRetention` replaces the fixed 30-day retention (the
  default is unchanged); `FileHomeostatStore.Retention` is internal.
- `ConditionDefinition`, `HealingPlan` and `HealingStep` are internal: no public member used them.
- `CoverageReport` and `ConditionCoverage` moved to the `Homeostat.Diagnostics` namespace.
- `IHomeostatControl` gains `GetCoverageAsync`, which custom implementations must add (`GetConditionHistoryAsync` has
  a default implementation).
- `HomeostatEventSink`: `ExecuteAsync` and `StopAsync` are sealed; `WriteAsync` is the extension point.
- `InMemoryHomeostatStore.OnChanged` can no longer be overridden outside Homeostat.
- `ISignalPublisher.TryPublish(source, severity, message)` is a default interface member: implementations need only
  `TryPublish(Signal)`.
- A verifier that returns a value that is not a `VerificationOutcome` (such as `default`) is logged as an error and
  treated as `Inconclusive`: the run stops instead of escalating.
- Trend and anomaly conditions no longer clear on a signal without a value (such as a failed probe) or with too
  little history: they clear only on a signal they evaluate as good.
- A switch that a person turned on (dashboard, CLI, code) is no longer taken over by a plan step that turns it on with
  `turnOffWhenCleared`, so the condition clearing no longer turns it off.
- `RestartHostedService<T>()` refuses a plain `BackgroundService` unless the host ignores background service
  failures; workers to be restarted derive from `RestartableBackgroundService`.
- Homeostat.AspNetCore: the endpoints write JSON with their own source-generated settings (web defaults, enums as
  names), no longer with the application's `HttpJsonOptions`; `/homeostat/status` writes enums as names, including
  `triggeringSignal.severity`.
- Homeostat.Artemis: `AddArtemis` is `UseArtemis`, like `UseRedis`, `UseAzure` and `UseKubernetes`.
- Homeostat.Data: `ThenClearConnectionPools` is removed; `ClearConnectionPools` works at any position in a plan.
- Homeostat.Aspire.Hosting: `HomeostatResourceOptions` is in the `Homeostat.Aspire.Hosting` namespace.
- Homeostat.Testing: `StartAsync`, `WaitForRunAsync`, `WaitForConditionAsync` and `WaitForApprovalAsync` take a
  `CancellationToken`.
- Homeostat.Azure: the `body` of `AzureResourceManager` is a `JsonNode`.
- Homeostat.Http: `WebhookOptions.CreatePayload` should return a `JsonNode` (or the event); anonymous objects still
  work, with reflection, outside trimmed and AOT applications.

### Fixed

- `RestartHostedService<T>()` on a `BackgroundService` stopped the whole application, because the generic host treats
  a background service stopped outside a shutdown as failed (`BackgroundServiceExceptionBehavior.StopHost`); see the
  breaking changes above.
- A condition that cleared while a switch step was running left the switch on: it is now turned off.
- `AddConditionsFromConfiguration` without any `MapAction` registered its action catalog only when the conditions were
  first resolved, after the host had made the service collection read-only.
- A signal that the engine cannot process is logged and skipped instead of stopping the engine.
- A signed Slack interaction or Teams command with a value of the wrong JSON type is answered 400 instead of 500.
- `UseRedis(connectionString)` no longer aborts startup when Redis is down.

[Unreleased]: https://github.com/engineering87/Homeostat/compare/v1.0.0-rc.1...HEAD
[1.0.0-rc.1]: https://github.com/engineering87/Homeostat/releases/tag/v1.0.0-rc.1
