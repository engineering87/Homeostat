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
| [Detectors](detectors.md) | Ready-made signals: thread pool, memory, exceptions, HTTP clients, Polly circuit breakers, custom detectors |
| [Conditions](conditions.md) | Filters, thresholds, `SustainedFor`, hysteresis with `ClearAfter`, causes and symptoms with `SuppressedBy` |
| [Healing plans](healing-plans.md) | Escalation, human approval, `HealingContext`, verification, outcomes |
| [Actions](actions.md) | Built-in actions, switches for degraded mode, inline lambdas, action classes |
| [Guardrails](guardrails.md) | Dry-run, budget, cooldown, concurrency, timeouts, kill switch, all options |
| [Observability](observability.md) | Log messages, traces, metrics, `IHomeostatControl`, observers |
| [Notifications and audit](notifications.md) | JSON Lines audit log, webhook notifier for chats and incident tools |
| [ASP.NET Core endpoints](aspnetcore.md) | Status and control endpoints, Homeostat as a health check |
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
