# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

Nothing yet.

## [1.0.0-rc.1] - 2026-11-05

First release candidate of Homeostat, a self-healing library for .NET 10 applications. Homeostat turns health
information (detectors, metrics, probes, health checks, broker and cloud statistics, external alerts) into signals,
evaluates them against conditions, and runs healing plans with verification, escalation and human approval when a
condition becomes active. Every action runs behind guardrails: dry-run by default, budgets, cooldowns, a kill switch
and persistent state shared across instances. Homeostat failing, or its store, never blocks the application or leaves it
degraded, and what it keeps in memory does not grow with the load. The public API is now listed and checked, and is
expected to stay stable through 1.0.0.

### Added

#### Core engine and guardrails

- Signals, conditions and healing plans with escalation and three-outcome verification.
- Guardrails: dry-run by default, action budget, cooldown with backoff, concurrency limit, timeouts, kill switch,
  maintenance windows, timed pauses (`PauseAsync(until, by)`, `POST /homeostat/pause?for=...`) and `PauseReason`.
- Human approval for risky steps (`RequireApproval`, `IHomeostatApprovals` with `GetPendingAsync`, `ApproveAsync` and
  `RejectAsync`, `NotApproved` outcome): the concurrency slot is released while waiting, and a rejection holds the
  plan until the condition clears or `IHomeostatControl.ReleaseHoldAsync(condition)` lifts the hold
  (`POST /homeostat/conditions/{name}/release`, `homeostat release <condition>`).
- Shared control state: the pause, the application switches and the approval requests are kept in the store, so with
  a shared store a pause, a switch or an answer given on one instance applies to every instance within
  `HomeostatOptions.ControlRefreshInterval` (5 seconds by default). `IHomeostatStore` gains `GetControlStateAsync`,
  `SetPauseAsync`, `SetSwitchAsync`, `TryClaimSwitchAsync`, `TryReleaseSwitchAsync` (atomic ownership of the
  switches a cluster plan turns on), `AddApprovalAsync`, `TryResolveApprovalAsync` and `RemoveApprovalAsync`, with
  `ControlState`, `PauseState`, `SwitchState` and `ApprovalRecord`. The file store keeps the pause and the switches
  turned on by a person or by code across restarts; Redis keeps everything, shared, under keys with the `{control}`
  hash tag (one slot on Redis Cluster). Until the state has been read once, Homeostat counts as paused.
- `ConditionCleared` outcome: a run stops before a step that is no longer needed.
- Persistent state: `IHomeostatStore` with in-memory, file (`UseFileStore`) and Redis stores; atomic plan-state
  updates (cooldown, backoff, holds); budget and run history survive restarts; fail-closed when the store is
  unavailable, with the status endpoint still answering. Condition events refused by a failing store are retried.
- Several instances: `IHomeostatCoordinator` and `IHomeostatLease`, `OncePerCluster()` plans with renewed leases that
  stop the run when the lease may be lost, `InstanceId` (machine name by default).
- `IHomeostatControl` with run history, budget status, condition history and coverage (`GetRecentRunsAsync`,
  `GetBudgetAsync`, `GetConditionHistoryAsync`, `GetCoverageAsync`).
- `IHomeostatSwitches` with `TurnOnAsync(name, scope, by)`, `TurnOffAsync`, `SwitchScope` (`Application`, the
  default, or `Instance`) and the `Changed` event, raised when a switch is turned on or off, also on another instance.
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
- Conditions and plans from configuration (`AddConditionsFromConfiguration`, `AddAction`) with strict validation,
  reloaded at runtime without touching unchanged conditions; includes `RisingFasterThan`, `FallingFasterThan`,
  `AnomalouslyHigh`, `AnomalouslyLow`, `SeasonallyHigh`, `SeasonallyLow`, `ProjectedAbove`, `ProjectedBelow` and
  `Adaptive`.
- Persisted condition history: activations and clears with the causing signal, in the file and Redis stores
  (`ConditionHistorySize`, 200 by default; `RedisStoreOptions.DocumentRetention`); history reads fall back to this
  instance's events when the store fails.

#### Healing plans and actions

- Inline lambda actions and compensating actions (`UndoWith`).
- Built-in actions: restart a hosted service, reload configuration, turn on a switch, turned off when the condition
  clears unless `TurnOnSwitch(name, turnOffWhenCleared: false)`. In a `OncePerCluster()` plan the switch is an
  application switch owned by the condition, turned off when it clears on any instance; in other plans it is an
  instance switch.
- Step settings: `ActionTimeout`, `VerificationTimeout`, `RequireApproval` and `UndoWith` apply to the step added just
  before them (in configuration, `ActionTimeout` and `VerificationTimeout` are keys of each step); `Cooldown`,
  `CooldownBackoff`, `UseVerifier`, `OncePerCluster` and `Adaptive` apply to the plan.
- `RestartableBackgroundService`, restarted inside the service, and `AddBackgroundServiceSignals` (sources
  `hosted-service:{type name}`, `SourcePrefix` option).
- `RestartApplication()` (approval by default): graceful stop with an exit code for the orchestrator to restart;
  `RestartGuard` and `RestartLimits` enforce a minimum uptime and a maximum number of restarts per window, counted in
  the store.

#### Detectors and signals

- Detectors: thread pool starvation, memory pressure, exception rate, custom `ISignalDetector`; `Signal.Value` is
  filled by every detector.
- Probes (`AddProbe`, `AddLatencyProbe`): any number or round trip as a signal value, with thresholds, interval and
  timeout; probes run in the background in their own scope, and a hanging call is canceled and never stacked.
- Metric signals from any `System.Diagnostics.Metrics` instrument (`AddMetricSignal`), with
  `AddServerErrorRateSignal(o => ...)` (`ServerErrorRateSignalOptions`), `AddServerLatencySignal(o => ...)`
  (`ServerLatencySignalOptions`, thresholds as `TimeSpan`) and `MetricSignalBuilder.Scale(factor)`.
- Any event counter as a signal (`AddEventCounterSignal`, source `counter:{eventSource}:{counter}`, `Interval` 10 s by
  default), enabling only the counters it needs.
- Starter presets `AddWebApiDefaults()` and `AddWorkerDefaults()`: the thread pool, memory and exception detectors
  (plus the server error rate and latency signals for web APIs, and `AddBackgroundServiceSignals` for workers) with
  the observe-only conditions `threadpool-starved`, `memory-pressure`, `exception-rate-high`, `server-errors-high` and
  `server-latency-high`; the server conditions are suppressed while the thread pool is starved (latency also under
  memory pressure). Each `DefaultCondition` can be turned off (`Enabled`), given another `SustainedFor`, configured
  (`Configure`, for example `WhenValueAbove` for another threshold) or given a plan (`Heal`). One preset per
  application.
- Call signals for any client (`AddCallSignals(source)`): a keyed `CallRecorder` records calls (`Record`,
  `Record(failed)` without a duration, `TrackAsync(call, isFailure, cancellationToken)`, which ignores cancellations
  requested by the caller) and publishes their failure ratio as the signal
  value, with the p95 duration in the tags; thresholds in `CallSignalOptions`. An `IServiceCollection` overload with an
  `origin` serves integration packages.
- `Homeostat.HealthChecks`: health check results as signals.

#### Integrations: data, Entity Framework Core, caching, Redis, MongoDB, HTTP and gRPC, Polly, feature flags

- Databases, in the core package (option types in `Homeostat.Data`): `AddDatabaseProbe` for any ADO.NET provider (failures tagged `transient` from
  `DbException.IsTransient`), the `ClearConnectionPools<TConnection>()` action and `AddSqlClientPoolSignal(o => ...)`
  (`SqlClientPoolSignalOptions`) for SqlClient pool exhaustion.
- `AddNpgsqlPoolSignal(o => ...)` (`NpgsqlPoolSignalOptions`): Npgsql 10+ pool usage as a share of
  the maximum, from the `Npgsql` meter.
- `Homeostat.EntityFrameworkCore`: `AddDbContextSignals<TContext>()` publishes the failure ratio and p95 duration of
  the commands of a `DbContext` (`efcore:{context type name}`) through an interceptor added with `ConfigureDbContext`,
  leaving `AddDbContext`, `AddDbContextPool` and `AddDbContextFactory` unchanged (a second call updates the settings,
  never adds a second interceptor); timeouts, transient errors and failed connections count, command errors and
  cancellations do not (`DbContextSignalOptions.IsDatabaseFailure`). `AddDbContextProbe<TContext>(o => ...)` (`DbContextProbeOptions`) checks
  `Database.CanConnectAsync` (`efcore:{context type name}:ping`), also for contexts registered only with
  `AddDbContextFactory`. Any relational
  provider; EF Core 10.
- `Homeostat.Caching`: `AddMemoryCacheSignals(o => ...)` (hit ratio, `MemoryCacheSignalOptions`) and the `PurgeHybridCacheTags`, `RemoveHybridCacheKeys`,
  `RemoveDistributedCacheKeys` and `CompactMemoryCache` actions.
- `Homeostat.Redis`: shared store (atomic budget script, capped history) and coordinator (`UseRedis`); `AddRedisSignals(o => ...)` (ping round trip and connection events, `RedisSignalOptions.Source`).
- `Homeostat.MongoDB`: ping latency, primary availability and connection pool timeouts from the driver events
  (`MongoClientSettings.AddHomeostatSignals`), replication lag (opt-in); `MongoSignalOptions.Source` sets the prefix.
- `Homeostat.Http`: failure ratio and p95 latency of `IHttpClientFactory` clients as signals (`MinimumCalls`,
  `IsFailure` with the default `HttpClientSignalOptions.IsServiceFailure`); gRPC failures
  (grpc-status Unknown, DeadlineExceeded, ResourceExhausted, Internal, Unavailable, DataLoss in response headers) are
  counted by `AddHomeostatSignals` through `HttpClientSignalOptions.IsGrpcFailure`.
- `Homeostat.Grpc`: `AddHomeostatGrpcSignals()` on the builder of `AddGrpcClient` publishes the failure ratio of a
  gRPC client's calls and the p95 duration of its unary calls (`grpc:{client name}`), judged on the gRPC status
  (`GrpcSignalOptions.IsFailure`, by default `IsServiceFailure`; `Cancelled` is never recorded). Streaming calls are
  recorded without a duration when their stream ends, with the status from the trailers, or as soon as they cannot
  start. A second call on the same client updates the settings, never adds a second interceptor.
  `HomeostatGrpcInterceptor` for channels built without the client factory.
- `Homeostat.Polly`: Polly v8 circuit breaker state as signals, `CircuitControl` and the `IsolateCircuit` action
  (manual isolation driven by a switch), `ChaosEnabledBySwitch` for chaos strategies during game days;
  `TrackCircuitBreaker`, `CircuitControl` and `ChaosEnabledBySwitch` are in `Homeostat.Polly.HomeostatPollyServices`
  (`using Homeostat.Polly;`).
- `Homeostat.FeatureManagement` (`Homeostat.Switch` feature filter) and `Homeostat.OpenFeature`
  (`HomeostatSwitchProvider`): feature flags driven by switches.

#### Message brokers

- Pausable consumers in the core: `IPausableConsumer`, `IHomeostatConsumers`, `AddPausableConsumer`, the plan step
  `PauseConsumer(name)` and the switch `consumer:{name}:paused` (also from the dashboard and the CLI).
- `PauseGate`: pauses any receive loop (AMQP receivers, change streams, polling workers).
- `Homeostat.ServiceBus`: active and dead-letter counts of queues and subscriptions as signals
  (`servicebus:{queue}:messages`, `servicebus:{topic}:{subscription}:messages`, `:dead-letter`); pausable
  `ServiceBusProcessor` and `ServiceBusSessionProcessor`.
- `Homeostat.RabbitMQ`: ready messages, consumers and dead letters of a queue as signals (passive declare on a
  short-lived channel); `PausableRabbitMqConsumer`.
- `Homeostat.Kafka`: consumer lag, broker connections and producer queue use from the client statistics
  (`SetHomeostatSignals` on consumer and producer builders, thresholds in `Lag` and `Queue`, `KafkaSignalReporter`); `KafkaPauseGate`, applied on the
  consumer's own thread, and `AddKafkaPauseGate(name)` for a keyed gate that survives restarts.
- `Homeostat.Artemis`: message and consumer counts of ActiveMQ Artemis queues and their dead-letter queue, through
  the broker's Jolokia management API (no client library).

#### Cloud and orchestrators

All actions require approval by default.

- `Homeostat.Kubernetes` (`UseKubernetes`): `RestartPod(gracePeriod)` (only pods with a controller), `RolloutRestart`,
  `ScaleDeployment` (only in the requested direction, conditional update), `CordonNode` (leaves cordoned nodes
  alone); in-cluster REST client trusting the cluster CA, rotating service account token, validated names.
- `Homeostat.Azure` (`UseAzure` with a token from Azure.Identity): `RestartAppService`, `RestartVirtualMachine`,
  `DisableFrontDoorOrigin` (never the last enabled origin, enabled again when it did not help),
  `AzureResourceManager`; resource ids validated against the exact type.

#### ASP.NET Core endpoints and dashboard

- `Homeostat.AspNetCore`: status and control endpoints (`MapHomeostatStatus`, `MapHomeostatControl`), which require an
  authorization policy unless explicitly mapped for anonymous use; `GET /homeostat/history` and
  `GET /homeostat/coverage`. Switch names in the URL are refused when empty, longer than 200 characters or with
  control characters, by both the `on` and the `off` endpoint. `POST /homeostat/switches/{name}/on?scope=instance`
  turns a switch on for one instance; the control endpoints answer 503 when the store is down.
- `MapHomeostatStatus`, `MapHomeostatControl` and `MapHomeostatDashboard` overloads that take an inline policy
  (`p => p.RequireRole("operations")`). The JSON of the endpoints is the contract; the types behind it are internal.
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

- Audit log in JSON Lines (`AddAuditLog`), webhook notifier (`AddWebhookNotifications`) and custom sinks
  (`IHomeostatEventSink`, `AddEventSink`): Homeostat queues the events and fills `ApplicationName`; events raised
  during shutdown are delivered.
- Notifiers in `Homeostat.Http`: `AddPagerDutyIncidents` (Events API v2), `AddOpsgenieIncidents` (Jira Service Management
  Operations by default, or Opsgenie), `AddSlackNotifications` (Block Kit, optional approval buttons),
  `AddTeamsNotifications` (Adaptive Cards); only network errors, 408, 429 and 5xx are retried. Named HTTP
  clients `PagerDutyOptions.HttpClientName`, `OpsgenieOptions.HttpClientName`, `SlackOptions.HttpClientName` and
  `TeamsOptions.HttpClientName`.

#### Learning and replay

- `Adaptive()` plans: steps with enough attempts of their own are ordered by measured success in the run history;
  new steps keep their position; approval steps are barriers.
- `AddSignalRecording`: daily JSON Lines files per instance with retention (`Retention`, 14 days), owner-only file permissions on Unix, messages
  and tags only on request; `RecordedSignal.Read`.
- `HomeostatReplay` (Homeostat.Testing): replays recordings in enforced dry-run on simulated time, isolated from
  stores, detectors, notifiers, logging providers and telemetry, with a timeline and per-condition summary;
  `HomeostatReplay.RunFilesAsync(paths, IConfiguration, options)` merges recording files in time order.
- Coverage reports from the persisted condition history, across instances and restarts, with failed steps and a
  warning when the run history does not cover the period.

#### Tooling: CLI, templates, analyzers, Aspire

- `Homeostat.Cli`: the `homeostat` tool (`status`, `pause`, `resume`, `approve`, `reject`, `release`,
  `switch on|off` with `--instance`, `signal`,
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
  publishing to nuget.org with trusted publishing (no stored API key); every action pinned to a commit, checkouts
  without persisted credentials, every NuGet package fixed to an exact version; CodeQL and OpenSSF Scorecard
  workflows; a security policy with coordinated disclosure timelines.
- CodeQL configuration (`.github/codeql/codeql-config.yml`): the security-and-quality suite on the shipped code,
  without tests and benchmarks, and without three style rules and the catch-all rule of the engine's fault isolation,
  each with its reason.
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

#### Failure isolation and resource limits

- `HoldAtMost(maximum)` after `TurnOnSwitch` (and the steps built on it, such as `PauseConsumer`), and the
  `HoldAtMost` step key in configuration: the switch is turned off after that time even if the condition never
  clears, for actions whose effect can keep their own condition active.
- `SwitchState.RenewedAt`: the instance that turned on a switch for a cluster plan renews it; when that instance
  stops or crashes, another instance turns the switch off as soon as the condition is not active there.
- The pause and the switches are written in decision order: `IHomeostatStore.TrySetPauseAsync` and `TrySetSwitchAsync`
  replace `SetPauseAsync` and `SetSwitchAsync`, and `TryReleaseSwitchAsync` takes the time the release was decided.
  Every write carries the time of its decision, and the store refuses one older than the latest decision about the same
  pause or switch, so a write that timed out on the instance but reached the store later cannot undo a newer decision
  (a switch turned back on after it was turned off, a pause ended by an older resume). On a tie, a decision that makes
  Homeostat do less wins. `TryClaimSwitchAsync` follows the same order, and a renewal only updates the claim it renews.
  The built-in stores implement it; the Redis store adds the `{control}:decided` hash (microseconds).
- `ChaosEnabledBySwitch(switchName, maximumDuration)`: chaos stops by itself one hour (by default) after its switch
  was first seen on, so a game day nobody ended does not keep degrading the application.
- `AddConditionsFromConfiguration(section, failOnInvalid)`: by default an invalid condition in configuration is
  logged as critical and skipped instead of stopping the application.

Also, compared with earlier builds:

- With the store unavailable, changes that make Homeostat do less (pause, turning a switch off, releasing a switch
  it turned on) apply at once on the instance and are written when the store answers again; changes that would let
  it act (resume, turning a switch on) still fail, with `503` from the endpoints.
- Every wait on the store is bounded and runs off the caller's thread, so a store with synchronous or never-ending
  calls cannot block startup, shutdown, a request, the health check (one second at most) or a Slack or Teams command.
- The file store opens lazily: a file locked by another process no longer stops the application; Homeostat fails
  closed and tries again every 10 seconds.
- Redis: the connection is resolved on first use and again after a failure, so a Redis server that is down at
  startup no longer stops the application.
- A compensating action (`UndoWith`) is never blocked by the pause, maintenance windows or the action budget, and runs
  after a step that timed out (it may have half-applied); it does not run after an action that refused.
- An action or compensating action abandoned at its timeout and still running is not started again until it ends,
  so a blocked action cannot exhaust the thread pool.
- Paused consumers: a pause that timed out leaves the state unknown, so the consumer is resumed when the condition
  clears; calls to the same consumer never overlap.
- `RestartHostedService` starts the service again even if stopping it failed or timed out.
- Restart limits are counted atomically in the store; the in-memory store logs a warning that they reset with the
  process.
- Kubernetes `RestartPod` evicts the pod (respecting PodDisruptionBudgets; `429` is reported as such) instead of
  deleting it. The RBAC rule needs `create` on `pods/eviction`.
- Service Bus: resume restarts only a processor Homeostat stopped, not one the application stopped or closed. Kafka:
  `KafkaPauseGate.Apply` takes the partitions the application paused, which it never resumes.
- The `Homeostat.Switch` feature filter never throws: invalid parameters are logged once and the feature is enabled.
- Detectors: a detector that hangs is skipped until its call returns, instead of piling up calls; failures are logged
  once and then at debug level while they continue.
- Call and HTTP counters saturate instead of overflowing.
- Bounded memory whatever the load: each alert source tracks at most 1,000 firing series (10,000 in all), an
  observable metric at most 10,000 tag combinations, and a trend, projection or anomaly window about 8,192 points (a
  faster source keeps the latest value of each interval). See [Performance](docs/performance.md#memory).
- MongoDB: a closed client no longer counts toward the deployment's state.
- A pause or a switch turned off applies to the instance before the store is even tried, never waits behind a slow
  refresh, and is not lost when the caller gives up (an aborted request); deferred changes are written in parallel.
  A resume that fails no longer drops a pause still waiting to be written. The control endpoints wait up to 15
  seconds, longer than a store write, so a deferred change answers `204`.
- A switch claimed by a cluster plan after its step timed out is released instead of staying on unowned; a renewal
  that races with a release no longer turns the switch back on.
- If the engine stops unexpectedly, the switches its conditions turned on are turned off.
- Approval requests are withdrawn off the engine loop, and every store call of a healing run and of the approvals is
  bounded. The periodic refresh and the health check never start a second read while one abandoned at its timeout
  still runs.
- A verifier abandoned at its timeout and still running is not started again.
- `RestartHostedService` waits a bounded time for a stop that ignores its token, and does not start the service
  again while the application is stopping.
- Restart limits are counted in fixed windows, the current one and the previous one together, with no counter ever
  reset: when every replica restarted at the same moment, a reset racing with a count could let one restart too many.
- An orphaned cluster switch is released only as of the renewal the sweeping instance saw: one renewed since by its
  owner stays on. Renewals only update a claim still held by the same instance.
- The services of a step are disposed only once an action or a verifier abandoned at its timeout has ended, also when
  the application stops.
- Shared control state, after a third review: a release deferred during an outage is no longer dropped by a failed
  refresh; a pause made while a resume is being written stands (the same for turning a switch off while it is being
  turned on); a resume or a turn-on that times out is decided again in the safe direction, so a write the store runs
  late cannot undo what the caller was told; read-backs are coalesced, so a burst of changes on a slow store does not
  hold the periodic refresh; one renewal at a time per switch; the Redis store renews only the claim it was made for.
- Cluster leases: acquiring is bounded, and a lease that arrives after Homeostat stopped waiting is released at once;
  releasing is bounded. `IHomeostatControl.ReleaseHoldAsync`, `GetRecentRunsAsync` and `GetBudgetAsync` are bounded too.
- The HTTP handler of `AddHomeostatSignals` allocates nothing when the response is already there.
- CI: the `Integration` workflow tests Kafka (statistics signals, pause gate), MongoDB (ping and primary signals),
  RabbitMQ and Artemis (queue signals) and the Service Bus emulator (pausable processor) against real servers in
  containers (`tests/Homeostat.IntegrationTests`), and the `kubernetes` job of the CI workflow the Kubernetes actions on
  a kind cluster with the documented Role.
- Kubernetes `ScaleDeployment` reads the scale again and retries (up to 4 attempts) when the conditional update
  answers `409`, as it often does while the deployment is rolling out; found by the new kind job.
- Shared control state, after a second review: a claim refused by the store no longer drops a pending switch-off; a
  change that lets Homeostat act shows at once on the instance and never flaps back while older reads complete, and its
  caller no longer waits behind a slow refresh; a person's turn-on is always written; a hold limit (`HoldAtMost`) only
  ends the claim it was set for; pending changes are written even while the state cannot be read, one write at a time
  per change; a file store that cannot save no longer keeps the decision time of the change it undid; the Redis store
  skips approval records without a request instead of failing the whole read.
- A pausable consumer that blocks before returning its task no longer holds its gate beyond the timeout.
- Seasonal baselines and the signal recorder no longer delay or fail shutdown when the store does not answer.
- After a code-quality review: a switch renewal is no longer a decision, so a person's turn-off decided between the
  claim and its renewal still applies (memory, file and Redis stores); a value rule (`WhenValueAbove`...) is no longer
  cleared by a signal without a value; a condition that cleared and started counting again is `Healed` for the default
  verifier; the detectors' timer can no longer stop the application (`DetectionInterval` must be at least 1 ms); the
  thread pool probe clamps its wait to the timer limit; gRPC streams no longer turn a normal end into an exception when
  the final status cannot be read; the Slack endpoint answers Slack at once and updates the message in the background;
  the health check starts one store read for concurrent probes; `HomeostatTestHost` stops every service even when one
  throws, and reports the errors after disposing.
- Validation: `AddHomeostatSignals` refuses ratios outside (0, 1] or in the wrong order, non-positive latencies,
  `MinimumCalls` below 1 and a null `IsFailure`; ratios equal to NaN are refused for call signals too; a metric signal
  that calls `RatioOf` and then another `Statistic` is refused instead of ignoring the match.
- `ChaosEnabledBySwitch` starts a new round when the switch is turned off and on again, also with no call in between.
- Hot paths: `KafkaPauseGate.Apply` returns before reading the assignment when there is nothing to do; Prometheus and
  Alertmanager sources pick their worst series in one pass; the analyzer compares symbols instead of formatting names.
- After an independent review, checked against the [design decisions](docs/decisions.md) so that no earlier fix is
  undone:
  - Bounded memory and threads: `Occurrences` keeps only the latest `count` matches; plan state reads, cluster lease
    requests, condition history reads and writes, and seasonal baseline loads and saves are not started again while one
    abandoned at its timeout still runs; seasonal baselines load in the background (startup no longer waits for them)
    and their saves are bounded, also at shutdown; restart counters carry an end, so the file store's retention removes
    them; detectors run concurrently, so a slow one no longer delays the others; a failed Redis connection is resolved
    again at most every 10 seconds, by the store and the coordinator; a sink that drops events from its full queue logs
    how many.
  - Healing: after an action abandoned at its timeout, its compensation runs once the action has ended (waiting at most
    the action timeout; if it is still running, the compensation is not run and an error asks for a person); a verifier
    whose constructor throws makes the step `Inconclusive`; a service that fails to dispose no longer turns a healed run
    into a failed one; a signal stamped more than a minute ahead of the instance's clock is processed as of now (on the
    system clock).
  - Shared control: a pause or a switch-off refused because the store holds a later decision is logged as a warning
    naming it (decisions follow the instances' clocks: keep them synchronized); a pause record that cannot be read
    counts as a pause (Redis), and an unreadable store file is set aside and the file store starts paused; a pause
    written while an older resume returns stays shown; a cluster plan's turn-on step fails when its claim is refused
    and the switch is off; claims record their owner's renewal period (`SwitchState.RenewInterval`), so an owner that
    renews less often is not taken for gone; the owner's release applies at once and is never older than its own
    renewal; Redis keeps a held plan until its hold ends (it expired after `PlanStateRetention`).
  - Approvals: listing pending requests is bounded; an answer whose write times out is read back (succeeds if
    recorded, returns `false` if another answer was, otherwise a `TimeoutException` says the outcome is unknown); a
    request written after its registration timed out is removed; every store drops requests a day after they expired;
    Redis removes a request and its answer atomically; a lease whose acquisition failed is released with its token.
  - Endpoints: a store timeout and a store failure get different answers (an approval or a hold release may still be
    applied; a resume or a turn-on was not made); store calls no longer take the request's token, so a disconnect
    neither cuts a change short nor logs an unhandled error; `GET /status` reports the type of a store error, not its
    message; `%2F` in switch and condition names is decoded; release names are validated; Teams keeps a timed-out
    command marked as handled. The Homeostat health check reports a store outage as `Degraded`, whatever
    `failureStatus`.
  - Integrations: a Service Bus processor paused before it starts is stopped as soon as the application starts it;
    `DisableFrontDoorOrigin` reads the origin group again and enables its origin again if none is left; a circuit
    breaker source reports the worst state of every pipeline tracked for it (at most 32); the `Homeostat.Switch` feature
    filter warns once per feature; metric thresholds must be finite and ordered; `RolloutRestart`, `ScaleDeployment`
    and the Azure steps are documented as needing `OncePerCluster()`.
  - Public API, reviewed for consistency before the API freezes: `VerifyWith<T>()` is `UseVerifier<T>()` (it applies
    to the whole plan); `ThreadPoolDetectorOptions` is `ThreadPoolStarvationDetectorOptions`; server signals name their
    thresholds by quantity (`DegradedLatency`/`UnhealthyLatency`, `DegradedErrorRatio`/`UnhealthyErrorRatio`);
    `AddHomeostatCheck(name, failureStatus, tags, timeout)` follows the health check conventions (`null` means
    `Degraded`); `ActionBudgetStatus` is a nominal record; the `ConditionClearedVerifier` constructor is internal (resolve
    it from the services); `HealingRunRecord.RunId` links stored runs to events and the audit log;
    `HomeostatTestHost.Publish(..., value)` and `StartAsync(configureServices: ...)`; `ReplayReport.SignalCount`,
    `Since`, `Until`; a `MapHomeostat*` policy that looks like a route is refused. The naming rules are recorded in the
    [design decisions](docs/decisions.md) (P9).
  - Documentation: a [Why Homeostat](docs/why-homeostat.md) page compares it with Polly, health checks and probes,
    platform auto-heal and incident automation; the README shows the weather feed sample healing with an approval; a
    documentation site with the API reference is built with docfx and published to GitHub Pages (`Docs` workflow).
  - Packaging: preview packages are numbered `X-preview.1` on a release and `X-preview.0.rc.N` on a release
    candidate, so the release sorts above the candidate; `Homeostat.Testing` depends on exactly its own Homeostat
    version.

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

### Changed since earlier builds from source

No version was published before this one. These changes matter only to code built against earlier commits of the
repository (version `0.1.0` in the project files):

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
- Custom event sinks implement `IHomeostatEventSink` (`Events`, `WriteAsync`) instead of deriving from the
  `HomeostatEventSink` base class, which is removed; Homeostat fills `ApplicationName`.
- `IHomeostatControl.Pause()`, `PauseUntil()` and `Resume()` are `PauseAsync(until, by)` and `ResumeAsync()`;
  `IHomeostatSwitches.TurnOn`/`TurnOff` are `TurnOnAsync`/`TurnOffAsync`; `IHomeostatApprovals.GetPending`, `Approve`
  and `Reject` are `GetPendingAsync`, `ApproveAsync` and `RejectAsync`. Custom stores implement the control-state
  members of `IHomeostatStore`.
- Results and events are records with `required` and `init` properties instead of positional records, so properties
  can be added after 1.0 without breaking: `HealingStepResult`, `HealingRunResult`, `PendingApproval`,
  `ApprovalResolution`, `ConditionSnapshot`, `HomeostatEvent`, `CoverageReport`, `ConditionCoverage`, `RecordedSignal`,
  `ReplayReport`, `ReplayEvent`, `ReplayConditionSummary`. Create them with object initializers
  (`new RecordedSignal { Timestamp = t, Source = "feed", Severity = SignalSeverity.Unhealthy }`); deconstruction is
  gone. `HealingRunResult` gains `RunId` and `Instance`, `HealingStepResult` gains `RunId`, `HomeostatEvent` gains
  `RunId` and `Instance`, and traces carry `homeostat.run.id`.
- Latency probes (`AddLatencyProbe`, `AddDatabaseProbe`, `AddDbContextProbe`, `AddRedisSignals`,
  `MongoSignalOptions.Ping`) take `LatencyProbeOptions`, with `DegradedLatency` and `UnhealthyLatency` as durations;
  the millisecond thresholds (`DegradedAbove`, `UnhealthyAbove`) still work and are the same values.
- `HealthCheckSignalOptions.Period` is `Interval`, like every other periodic setting.
- `MapHomeostatAlerts(policy, prefix, allowAnonymous, configure)` takes the options callback last, and gains an
  overload with an inline policy (`MapHomeostatAlerts(p => p.RequireRole("monitoring"))`).
- `AzureOptions.AccessToken` and `KubernetesOptions.AccessToken` receive the services
  (`(services, ct) => ...`), and `HomeostatResourceOptions.BearerToken` returns a `ValueTask`, so the three token
  callbacks have the same shape.
- `ISignalPublisher.TryPublish(source, severity, message, value)` takes an optional value.
- `CompositeMode` is internal; a maintenance window cannot change once `AddMaintenanceWindow` registered it.
- `IHomeostatSwitches.TurnOffAsync(name, by)` and `IHomeostatControl.ResumeAsync(by)` take who acted, like
  `TurnOnAsync` and `PauseAsync`: pass the cancellation token by name. The answer parameters of `ApproveAsync` and
  `RejectAsync` are named `by`. `IHomeostatStore` gains `SupportsDocuments` (default `false`).
- `TurnOnSwitch(name)` turns the switch off when the condition clears by default (`turnOffWhenCleared: true`), and so
  does a `switch:` step in configuration without `TurnOffWhenCleared`.
- `ActionTimeout` and `VerificationTimeout` apply to the previous step, not to the whole plan; in configuration they
  move from the plan to each step.
- Names aligned before 1.0: `AddAction` (was `MapAction`), `AddSignalRecording` (`RecordSignals`),
  `services.AddHomeostatDetector` (`AddSignalDetector`), `RedisStoreOptions` (`RedisHomeostatOptions`),
  `AddOpsgenieIncidents` (`AddOpsgenieAlerts`), `ChatOptions.Url` (`WebhookUrl`), `HttpClientSignalOptions.MinimumCalls`
  (`MinimumRequests`), `EventCounterSignalOptions.Interval` (`IntervalSeconds`), `SignalRecordingOptions.Retention`
  (`RetentionDays`), `RestartPod(gracePeriod)` (`gracePeriodSeconds`), Kafka `Lag.*` and `Queue.*` (`LagDegradedAbove`...).
- Registrations take an options callback: `AddSqlClientPoolSignal`, `AddNpgsqlPoolSignal`, `AddServerErrorRateSignal`,
  `AddServerLatencySignal`, `AddMemoryCacheSignals`, `AddRedisSignals`, `AddDbContextProbe<T>` and
  `AddHealthCheckSignals` (for example `AddSqlClientPoolSignal(o => o.MaxPoolSize = 200)`).
- Sources: `service:{type}` is `hosted-service:{type}`; Service Bus `:active` is `:messages`
  (`ServiceBusSignalOptions.Messages`) and subscriptions are `servicebus:{topic}:{subscription}`.
- `TrackCircuitBreaker`, `CircuitControl` and `ChaosEnabledBySwitch` need `using Homeostat.Polly;`. `CallRecorder.TrackAsync`
  takes the cancellation token last: pass it by name.
- `InMemoryHomeostatStore` and `FileHomeostatStore` are sealed, and `FileHomeostatStore` no longer derives from
  `InMemoryHomeostatStore`; `PlanScope`, `CallRecorder.Options` and the endpoint DTOs (`HomeostatStatus`, `RunInfo`...)
  are internal; `ChatOptions` and the incident options cannot be derived; `HomeostatOptions` cannot change once
  Homeostat has started; the `SourcePrefix` of alert ingestion cannot be empty.
- `ISignalPublisher.TryPublish(source, severity, message)` is a default interface member: implementations need only
  `TryPublish(Signal)`.
- A verifier that returns a value that is not a `VerificationOutcome` (such as `default`) is logged as an error and
  treated as `Inconclusive`: the run stops instead of escalating.
- Trend and anomaly conditions no longer clear on a signal without a value (such as a failed probe) or with too
  little history: they clear only on a signal they evaluate as good.
- A switch that a person turned on (dashboard, CLI, code) is no longer taken over by a plan step that turns it on, so
  the condition clearing no longer turns it off.
- `RestartHostedService<T>()` refuses a plain `BackgroundService` unless the host ignores background service
  failures; workers to be restarted derive from `RestartableBackgroundService`.
- Homeostat.AspNetCore: the endpoints write JSON with their own source-generated settings (web defaults, enums as
  names), no longer with the application's `HttpJsonOptions`; `/homeostat/status` writes enums as names, including
  `triggeringSignal.severity`.
- Homeostat.Artemis: `AddArtemis` is `UseArtemis`, like `UseRedis`, `UseAzure` and `UseKubernetes`.
- Databases: `ThenClearConnectionPools` is removed; `ClearConnectionPools` works at any position in a plan.
- The `Homeostat.Data` package is merged into `Homeostat`: it had no dependency of its own (ADO.NET is part of .NET).
  Remove the package reference; the namespaces and the API are unchanged.
- Packages have a stability level: the integrations not yet proven in production (`Homeostat.Azure`,
  `Homeostat.EntityFrameworkCore`, `Homeostat.Grpc`, `Homeostat.Caching`, `Homeostat.FeatureManagement`,
  `Homeostat.OpenFeature`, `Homeostat.Aspire.Hosting`) ship as previews (`1.0.0-preview.1` when the others are `1.0.0`, `1.0.0-preview.0.rc.1` in this release candidate),
  so their API can still change in a minor version. See [Versioning](docs/versioning.md#stable-and-preview-packages).
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
- `AddConditionsFromConfiguration` without any `AddAction` registered its action mappings only when the conditions were
  first resolved, after the host had made the service collection read-only.
- A signal that the engine cannot process is logged and skipped instead of stopping the engine.
- A signed Slack interaction or Teams command with a value of the wrong JSON type is answered 400 instead of 500.
- `UseRedis(connectionString)` no longer aborts startup when Redis is down.
- The Redis coordinator declares a lease lost before its key can expire (a margin of a sixth of the lease duration),
  so a cluster run stops before another instance can take the lease.
- When the plan state cannot be saved at the end of a cluster run, the lease is kept for the cooldown instead of
  released, so another instance does not run the plan again at once.
- Compensating actions (`UndoWith`) count in the action budget (best effort); the pause, maintenance windows and the
  budget never block them (see Failure isolation).
- A cached hold is read again from the store after a minute, so a hold lifted on another instance applies;
  `ReleaseHoldAsync` lifts it at once.
- A pending cause that receives no signal for `max(SustainedFor, 1 minute)` no longer suppresses its symptoms.
- A condition removed by a configuration reload stops its running plan before the next step.
- `RestartLimits.Window` is counted in fixed windows aligned on UTC time (see Failure isolation).
- Homeostat can no longer hold up the application it heals: startup waits at most 5 seconds for the control state; the
  control, status, history and coverage endpoints, the Slack and Teams endpoints and the health check stop waiting for
  a store that does not answer; an action, compensating action or verifier that ignores its cancellation no longer
  holds the run, the concurrency slot or the shutdown; an `IsFailure` classifier that throws no longer fails the
  application's HTTP, gRPC, Entity Framework Core or tracked call; exceptions from the engine and detector loops no
  longer stop the host; MongoDB, Redis, Kafka and alert-timer callbacks never throw into the client libraries.
- File store: a change that cannot be written is undone in memory too (a retried condition event was stored twice, a
  refused budget unit stayed spent); a second process on the same file fails at startup (`{path}.lock`); null records
  in the file no longer prevent startup. `FileHomeostatStore` is `IDisposable`, to release the lock.
- `SustainedFor`: two triggering signals more than twice the duration apart (at least five minutes) restart the
  window, so isolated blips an hour apart no longer activate a condition.
- `CooldownBackoff(multiplier, max)` with `max` below the cooldown shortened the cooldown after a failure: the backoff
  only lengthens it. Cooldowns are limited to about 49 days, like the other timers, and the cluster lease kept for the
  cooldown is always released.
- A rejection or a failure count saved just after the condition cleared outlived the episode: it is reset again.
- A compensating action that is not run reports `UndoSucceeded = null`, not `false`; its XML documentation said it does
  not consume the budget, it does.
- Configuration refuses values that were silently ignored: numeric or combined enum values (`"Severity": "7"`),
  `AllActive` with `AnyActive`, `ApprovalTimeout` without `RequireApproval`, `TurnOffWhenCleared` on a step that is
  not a switch. A change made to the configuration while the application started is applied.
- The `Last` statistic of an observable instrument counted series that stopped reporting (a disposed pool); series
  missing for ten collections are forgotten.
- Event sinks drain together when the application stops: one slow sink no longer uses up the others' shutdown time.
- The audit log's `{date}` uses the invariant culture.
- Notifier clients, the CLI and the dashboard no longer follow redirects: a sign-in redirect was reported as success,
  and a redirected notification as delivered.
- Alert endpoints accept only JSON and refuse cross-site requests (`415`, `403`), against forged form posts.
- The Entity Framework Core command signal recognizes SQL Server timeouts and transient errors, which
  `Microsoft.Data.SqlClient` does not mark as `IsTransient`.
- The Kafka broker signal no longer publishes Healthy for the first document that shows brokers down.
- Teams cards escape Markdown in texts that come from signals; a Teams command that fails because the store is down
  can be sent again.
- Aspire's Pause and Resume commands are enabled while the resource runs, also when it is unhealthy.
- The CLI keeps carriage returns out of the terminal; HOMEO002 is no longer silenced by a LINQ `Where` inside the
  condition.
- `HomeostatOptions`, `RedisStoreOptions` and the built-in detector options are validated at startup.

[Unreleased]: https://github.com/engineering87/Homeostat/compare/v1.0.0-rc.1...HEAD
[1.0.0-rc.1]: https://github.com/engineering87/Homeostat/releases/tag/v1.0.0-rc.1
