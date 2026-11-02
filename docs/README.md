# Homeostat documentation

New to Homeostat? Read the pages in this order: each one builds on the previous.

## Learn

| Step | Page | You will learn |
|---|---|---|
| 1 | [Getting started](getting-started.md) | Install, write your first action, watch the dry-run, then let it act |
| 2 | [Concepts](concepts.md) | The mental model and the vocabulary used everywhere else |

## Reference

| Page | Covers |
|---|---|
| [Signals](signals.md) | `Signal`, severities, `ISignalPublisher`, health checks as signals |
| [Detectors](detectors.md) | Ready-made signals: thread pool, memory, exceptions, HTTP clients, Polly circuit breakers, any metric, custom detectors |
| [Conditions](conditions.md) | Filters, thresholds, `SustainedFor`, `ClearAfter`, `SuppressedBy`, values, trends, anomalies, occurrences, composites |
| [Healing plans](healing-plans.md) | Escalation, human approval, compensation, backoff, once per cluster, verification, outcomes |
| [Actions](actions.md) | Built-in actions, switches for degraded mode, inline lambdas, action classes |
| [Guardrails](guardrails.md) | Dry-run, budget, cooldown, concurrency, timeouts, kill switch, maintenance windows, all options |
| [Observability](observability.md) | Log messages, traces, metrics, `IHomeostatControl`, observers |
| [Notifications and audit](notifications.md) | Audit log, webhook, PagerDuty and Opsgenie incidents, Slack and Microsoft Teams |
| [Persistence and clusters](persistence-and-clusters.md) | File and Redis stores, several instances, plans that run once per cluster |
| [Integrations](integrations.md) | Probes, databases and pool exhaustion, MongoDB, Redis, caches, alerts from monitoring, feature flags, Polly circuit isolation and chaos, .NET Aspire |
| [Message brokers](messaging.md) | Azure Service Bus, RabbitMQ, Kafka and ActiveMQ Artemis: backlog, dead letters, lag; pausing consumers |
| [Learning and foresight](intelligence.md) | Predictive conditions, adaptive plans, signal recording and replay, coverage reports |
| [Cloud and orchestrators](cloud.md) | Restart the application, Kubernetes and Azure actions, permissions |
| [Configuration](configuration.md) | Conditions and plans in `appsettings.json`, applied at runtime |
| [ASP.NET Core endpoints](aspnetcore.md) | Status and control endpoints, dashboard, Slack approvals, Homeostat as a health check |
| [Tools](tools.md) | `homeostat` command-line tool, `dotnet new` templates |
| [Analyzers](analyzers.md) | Build-time checks HOMEO001 to HOMEO005 |
| [Performance](performance.md) | What publishing a signal, the engine and the HTTP handler cost, measured |
| [Trimming and Native AOT](native-aot.md) | What works in trimmed and AOT-compiled applications, and how it is checked |
| [Testing](testing.md) | Scenario tests with simulated time, approvals, fault injection, unit testing actions |

## Practice

| Page | Covers |
|---|---|
| [Recipes](recipes.md) | Fail over (with approval), cached mode, shared failures, chat notifications, backlogs, configuration, deployments, testing |
| [FAQ & troubleshooting](faq.md) | Why it did or did not act, scaling out, comparison with Polly |

## Project

| Page | Covers |
|---|---|
| [Design notes](design.md) | Internal pipeline and threading model |
| [Roadmap](ROADMAP.md) | What is planned and what is out of scope |
