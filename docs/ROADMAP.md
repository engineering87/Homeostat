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

- [x] Probes: any number or round trip as a signal, concurrent, with timeouts and no pile-up (`AddProbe`, `AddLatencyProbe`)
- [x] Databases (`Homeostat.Data`): connectivity and latency probes for any ADO.NET provider, transient error tagging, pool reset actions
- [x] Redis client health signals: ping round trip and connection events
- [x] Caches (`Homeostat.Caching`): memory cache hit ratio; HybridCache, distributed and memory cache purge actions
- [x] Feature flags as degradation (`Homeostat.FeatureManagement`, `Homeostat.OpenFeature`), driven by switches with change events
- [x] Polly: circuit isolation from plans and by hand; chaos strategies enabled by switches for game days
- [x] Alert ingestion (Prometheus Alertmanager, Grafana, Azure Monitor, generic signals) as signals
- [x] Message brokers: recipes with probes for Azure Service Bus, RabbitMQ and Kafka
- [ ] Native broker packages: consumer lag and dead-letter signals, pause and resume of consumers
- [ ] Pool exhaustion signals for providers without metrics
- [ ] .NET Aspire hosting integration (resource, dashboard links)

## Wave 3: operations and developer experience

- [x] Embedded dashboard (`MapHomeostatDashboard`): status, history, one-click approvals, switches, pause
- [x] Incidents in PagerDuty and Opsgenie (Jira Service Management); Slack notifications with interactive approvals; Microsoft Teams cards
- [x] `homeostat` command-line tool (`Homeostat.Cli`) and `dotnet new` templates (`Homeostat.Templates`)
- [x] Roslyn analyzers HOMEO001 to HOMEO005 (unawaited inline actions, conditions without a source, ignored cancellation, anonymous endpoints, unsafe worker restarts)
- [x] Grafana dashboard and state gauges; worker service sample
- [x] Trend and anomaly rules from configuration
- [x] `RestartableBackgroundService`: safe restarts of stuck or crashed workers, with `AddBackgroundServiceSignals`
- [ ] Interactive approvals in Microsoft Teams (bot) and from incident tools
- [ ] Documentation site; Aspire and Kubernetes samples
- [ ] Persisted condition history

## Wave 4: cloud and orchestrators (separate packages, approval on by default)

- [x] Controlled self-restart through the host lifetime (`RestartApplication`), with a minimum uptime guard
- [x] Kubernetes (`Homeostat.Kubernetes`): restart this pod, rollout restart, scale a deployment, cordon the node; in-cluster REST, no SDK
- [x] Azure (`Homeostat.Azure`): restart App Service and VMs, Front Door origin failover, any Resource Manager operation
- [ ] Azure App Service plan scaling and Container Apps actions
- [ ] AWS: ECS tasks, Auto Scaling groups (needs SigV4 and the SDK credential chain)

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
