# Roadmap

Homeostat aims to be the reference self-healing library for .NET. The roadmap is organized in waves; each wave
ships as previews. It is indicative and changes with feedback: open an issue to discuss priorities.

## Shipped in the first preview

**Core loop and safety**
- [x] Signals, conditions (`SustainedFor`, `ClearAfter`, `SuppressedBy`), healing plans with escalation and three-outcome verification
- [x] Guardrails: dry-run by default, action budget, cooldown with backoff, concurrency limit, timeouts, kill switch, timed pause, maintenance windows
- [x] Human approval for risky steps, compensating actions (`UndoWith`), automatic switch reset

**Production foundations (wave 1)**
- [x] Persistent state: `IHomeostatStore` with in-memory, file and Redis stores; fail-closed behavior
- [x] Several instances: `IHomeostatCoordinator`, plans that run once per cluster with renewed leases, shared budget and history (`Homeostat.Redis`)
- [x] Any `System.Diagnostics.Metrics` instrument as a signal, with presets for server errors and latency
- [x] Advanced conditions: values, trends, anomalies, occurrences, composites
- [x] Conditions and plans from configuration, applied at runtime

**Signals, actions, operations**
- [x] Health checks, thread pool, memory, exceptions, HTTP clients, Polly circuit breakers
- [x] Inline and class actions, restart a hosted service, reload configuration, switches
- [x] Status and control endpoints, Homeostat health check, webhook notifier, audit log, traces and metrics
- [x] `Homeostat.Testing`: in-memory host with simulated time, approvals, fault injection

## Wave 2: integrations where .NET applications break

- [ ] Databases (EF Core, SqlClient, Npgsql): pool exhaustion and transient error signals; pool reset actions
- [ ] Redis client health signals and reconnect action
- [ ] Messaging (Azure Service Bus, RabbitMQ, Kafka): consumer lag and dead-letter signals; consumer restart and pause actions
- [ ] Caches (`HybridCache`, `IDistributedCache`): hit ratio signals; targeted purge actions
- [ ] Feature flags (Microsoft.FeatureManagement, OpenFeature) as degradation actions
- [ ] Polly: manual circuit control actions; chaos strategies to validate plans
- [ ] Alert ingestion (Prometheus Alertmanager, Azure Monitor, Grafana) as signals
- [ ] .NET Aspire integration

## Wave 3: operations and developer experience

- [ ] Embedded dashboard: status, history, one-click approvals
- [ ] Interactive approvals in Slack and Microsoft Teams; PagerDuty and Opsgenie incidents
- [ ] `dotnet homeostat` CLI and `dotnet new` template
- [ ] Roslyn analyzers (actions that ignore cancellation, conditions without a source)
- [ ] Grafana dashboard; documentation site; samples gallery (worker service, Aspire, Kubernetes)
- [ ] Trend and anomaly rules from configuration; persisted condition history

## Wave 4: cloud and orchestrators (separate packages, approval on by default)

- [ ] Kubernetes: restart pod, scale, cordon
- [ ] Azure: App Service restart and scale, Front Door failover, Key Vault secret refresh
- [ ] AWS: ECS tasks, Auto Scaling groups
- [ ] Controlled self-restart through the host lifetime

## Wave 5: intelligence

- [ ] Adaptive plans: order steps by measured success
- [ ] Predictive healing from trends
- [ ] Signal recording and replay: "what would the plans have done yesterday?"
- [ ] Plan coverage reports from chaos experiments

## Toward 1.0

- [ ] Stable public API, enforced by package validation baselines and API analyzers
- [ ] Native AOT and trimming compatibility, published benchmarks of the overhead
- [ ] Signed packages, SBOM, OpenSSF Scorecard
- [ ] .NET Foundation application

## Out of scope

- Per-call resilience (retries, timeouts, circuit breakers): use Polly.
- Infrastructure orchestration as such (operators, autoscalers): Homeostat drives them through actions, it does not replace them.
