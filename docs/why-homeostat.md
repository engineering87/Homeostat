# Why Homeostat

This page is for .NET developers and SREs deciding whether to adopt Homeostat. It describes the gap Homeostat fills,
how it compares with the tools you probably use already, how to combine them, and when Homeostat is the wrong choice.

## The problem: the runbook that is always the same

Many incidents end with the same few steps. The forecast provider stops answering. Someone is paged at 3 a.m., opens
the dashboard, waits a minute to see whether the problem goes away, renews the client, checks again, switches to cached
forecasts when renewing did not help, checks again, and writes down what happened. Next month the same alert fires and
another person runs the same steps.

Those steps have three things in common. They depend on knowledge of the application (which client to renew, which
degraded mode is acceptable). They need judgment over time (is this a blip or a real outage, did the last step work).
And they are safe only with limits (do not restart every replica at once, stop and call a person when nothing helps).

Homeostat lets you write that runbook as code inside the application, and run it under those limits.

## What Homeostat adds

- **Conditions over time.** A [condition](conditions.md) turns signals (health checks, metrics, probes, alerts, your
  own code) into a decision: the problem has lasted `SustainedFor`, recovery has held for `ClearAfter`, a value is
  rising, a queue will be full in two hours. `SuppressedBy` stops Homeostat from healing the symptoms of a failure
  while its cause is active.
- **Guarded healing plans.** A [healing plan](healing-plans.md) is an ordered list of steps, from the least invasive to
  the strongest. Every step runs inside [guardrails](guardrails.md): a global action budget, a cooldown per plan, one
  run at a time, a timeout per action, a kill switch and maintenance windows.
- **Verification and escalation.** After each step Homeostat checks the outcome. It moves to the next step only with
  evidence that the previous one did not work, and stops when it cannot tell. A step can have a compensating action
  (`UndoWith`) that reverts it when it did not help.
- **Approvals.** A risky step waits for a person (`RequireApproval`), who answers from the
  [dashboard](aspnetcore.md#dashboard), Slack, Microsoft Teams, the [CLI](tools.md) or the API. Restarts and the
  Kubernetes and Azure actions require approval by default.
- **Shared state across instances.** With a shared store such as Redis, the pause, switches, approvals, budget and
  history apply to every instance, and a plan marked `OncePerCluster()` runs on one instance at a time. See
  [Persistence and clusters](persistence-and-clusters.md).
- **Dry-run by default.** Homeostat starts by logging what it would do, without acting. You can also
  [record signals and replay them](intelligence.md#recording-and-replay) against new rules, and test plans on
  simulated time with [`Homeostat.Testing`](testing.md).
- **Observability.** Every decision is logged; runs, steps and outcomes are traced and measured with
  `System.Diagnostics` (export them with OpenTelemetry); a JSON Lines audit log, a status endpoint and a dashboard show
  what happened. When healing gives up, Homeostat can open an
  [incident in PagerDuty or Opsgenie](notifications.md#incidents-pagerduty-and-opsgenie).

Homeostat is designed so that its own failures never take the application down. If its store does not answer, it
stops acting (fails closed) and the application keeps serving. Publishing a signal never blocks. An exception that
escapes its engine stops self-healing, not the host. Startup waits at most 5 seconds for the store. The full list, and
the two cases it cannot prevent, are in
[What Homeostat does when it is the problem](guardrails.md#what-homeostat-does-when-it-is-the-problem).

## How it compares

Each column describes what the tool does on its own, with its usual configuration. Many of these tools can be extended
with custom code; the table does not count that.

| Capability | Homeostat | Polly / `Microsoft.Extensions.Resilience` | Health checks and Kubernetes probes | Platform auto-heal (operators, App Service auto-heal) | External auto-remediation (runbook automation, AIOps) |
|---|---|---|---|---|---|
| Where it runs | In the application process | In the application process | Checks in the process; probes in the kubelet | In the platform or its control plane | In a separate service, with agents near the systems |
| Scope | The application and its dependencies, over time | One call through a pipeline | One container | One instance, workload or managed system | Any system the automation can reach |
| Reacts to | A condition that persists, from any signal | The result, exception or duration of a call | A probe that fails `failureThreshold` times in a row | Platform metrics or the state of a resource | An alert or an incident |
| Typical action | Renew a client, clear a pool, turn on a degraded mode, pause consumers, fail over; restart as a last step | Retry, timeout, circuit breaker, fallback, hedging, rate limiting | Restart the container (liveness); stop routing traffic (readiness) | Recycle the process, reschedule a pod, reconcile toward the desired state | A script or job against infrastructure |
| Knows the application's internals | Yes: actions are your own classes, resolved from DI | Yes, per call | Only what the health check reports | No | No, unless it calls an API the application exposes |
| Ordered escalation | Yes, step by step | Strategies are layered per call | No: the same action, with backoff | Depends on the operator | Yes, as authored in the workflow |
| Verifies the outcome | Yes, after every step; stops when inconclusive | Partly: a half-open circuit tests the dependency with trial calls | The probe keeps running; readiness returns when it passes | Operators reconcile continuously | As authored in the workflow |
| Human approval before a step | Yes, per step | No | No | Not usually | Yes, a responder can run the job |
| Coordination across instances | Yes, with a shared store | No, per process | No, per pod | Yes, in the control plane | Yes, centrally |
| Dry-run | On by default | No (chaos strategies help test) | No | No | Depends on the product |
| Record of what happened | Logs, traces, metrics, audit log | Telemetry events and metrics | Kubernetes events | Platform logs and events | Job logs and audit |

Notes on each tool:

- **Polly and `Microsoft.Extensions.Resilience`** handle transient faults of individual calls. Microsoft's packages
  are built on Polly and add DI and HTTP integration. Homeostat does not retry calls; it reacts when a dependency stays
  broken after retries, and `Homeostat.Polly` turns a circuit breaker that stays open into a signal and lets a plan
  isolate a circuit. See [Polly: circuit isolation and game days](integrations.md#polly-circuit-isolation-and-game-days).
- **ASP.NET Core health checks** report status; they do not fix anything. An orchestrator or a load balancer reacts to
  them. Kubernetes restarts a container whose liveness probe fails and stops sending traffic to one whose readiness
  probe fails. Homeostat reads the same health checks as signals (`AddHealthCheckSignals`) and adds the steps between
  "report" and "restart".
- **Kubernetes operators and platform auto-heal** act on the infrastructure. An operator encodes the knowledge of how to
  run a specific system; Azure App Service auto-heal mitigates by rules on request count, slow requests, memory and
  status codes, and proactive auto-heal restarts an app it judges unrecoverable (Windows only). They see the process
  from outside. Homeostat can call them as the last step of a plan (`RestartPod()`, `RolloutRestart()`,
  `RestartAppService()`; see [Cloud and orchestrators](cloud.md)), after cheaper steps inside the application failed.
- **Incident and runbook automation** (PagerDuty Runbook Automation, PagerDuty AIOps, Jira Service Management, which
  replaces Opsgenie: Atlassian stopped selling Opsgenie on 4 June 2025 and ends its support on 5 April 2027) runs
  centrally, across many systems, triggered by alerts or by a responder. It suits work that spans services and
  infrastructure. Homeostat suits the steps that need the application's own objects, and it pages these tools when
  healing gives up or a step waits for approval.

### Other self-healing projects for .NET

- **[FabricHealer](https://github.com/microsoft/service-fabric-healer)** (Microsoft, MIT) schedules and runs repairs in
  Service Fabric clusters: applications, containers, virtual machines and disk space, coordinated through the Service
  Fabric RepairManager. Repair workflows are written as logic rules and triggered by health events from FabricObserver.
  It targets .NET 8 and Service Fabric only (version 1.3.2, August 2025). On Service Fabric it is the specialized
  choice; Homeostat does not integrate with RepairManager.
- **[HealixNET](https://github.com/VividZap/HealixNET)** (MIT) wraps operations in healing policies such as retry and
  fallback (`SelfHealingManager.ExecuteWithHealing`) and includes a WPF dashboard. Version 1.0.0 (August 2025) targets
  `net9.0-windows`. Its model is closer to per-call resilience; Homeostat works on conditions over time and targets
  .NET 10 on any operating system.

## Use them together

Each tool keeps its job. In a weather API:

1. Polly retries a failed call to the forecast provider and opens a circuit when failures continue.
2. Homeostat sees the circuit stay open for two minutes, renews the client, and if that does not help, asks a person
   before switching to the backup provider.
3. Homeostat opens a PagerDuty incident when a step waits for approval or when no step heals, and resolves it when
   the condition clears.
4. The Kubernetes liveness probe still restarts the container if the process stops answering, which Homeostat cannot
   do from inside a process that no longer runs.

```csharp
using Homeostat;
using Homeostat.Polly;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

// 1. Per call: Polly.
builder.Services.AddResiliencePipeline("forecast", (pipeline, context) => pipeline
    .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 2 })
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        StateProvider = context.ServiceProvider.TrackCircuitBreaker("forecast-circuit"),
    }));

// 2 and 3. Over time: Homeostat.
builder.Services.AddHomeostat(h => h
    .AddCircuitBreakerSignals()
    .AddPagerDutyIncidents(o => o.RoutingKey = builder.Configuration["PagerDuty:RoutingKey"])
    .AddCondition("forecast-circuit-open", c => c
        .ForSource("forecast-circuit")
        .SustainedFor(TimeSpan.FromMinutes(2))
        .Heal(p => p
            .With<RenewForecastClientAction>()
            .Then<SwitchToBackupProviderAction>()
                .RequireApproval(TimeSpan.FromMinutes(10)))));

// 4. Liveness: the process answers. No dependency checks here.
app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
```

`RenewForecastClientAction` and `SwitchToBackupProviderAction` are your own `IHealingAction` classes (see
[Writing actions](healing-plans.md#writing-actions)). `AddPagerDutyIncidents` comes from `Homeostat.Http`, and
`AddResiliencePipeline` from `Polly.Extensions`.

> [!IMPORTANT]
> Keep dependency checks and the Homeostat health check out of the liveness probe. A broken forecast provider is a
> reason to heal or to degrade, not to restart every replica.

## When not to use Homeostat

- **To avoid fixing the cause.** Homeostat shortens the time to recover from known failures. A plan that runs every
  day is a defect to fix; the run history and the audit log help to find it.
- **For per-call resilience.** Retries, timeouts and circuit breakers belong in Polly or
  `Microsoft.Extensions.Resilience`.
- **When the process cannot run it.** Homeostat runs inside the application. A crashed process, a deadlock or complete
  thread pool starvation stops it too: liveness probes and platform restarts remain necessary.
- **When actions have side effects you cannot bound.** An action that fails over, purges or restarts changes the
  system. Make actions idempotent, give them a compensating action where possible, require approval, and rehearse
  them in dry-run and in a game day before letting them act.
- **For problems that span many services.** Homeostat acts within one application. Coordinating several services or
  teams is the job of incident and runbook automation.
- **To learn the architecture for you.** Homeostat runs the loop; the knowledge of your architecture (components,
  failure modes, dependencies, safe actions) is yours to state in conditions and plans. See
  [Where the knowledge of your architecture goes](concepts.md#where-the-knowledge-of-your-architecture-goes).
- **When nothing recurs.** Writing conditions and actions takes effort. If incidents are rare and always different,
  the starter presets (`AddWebApiDefaults`, `AddWorkerDefaults`) in observe-only mode may be all you need.
- **Outside .NET 10.** Every package targets .NET 10. Applications on .NET Framework, .NET 8 or .NET 9 cannot use it.
- **On Service Fabric repairs.** For cluster-level repairs on Service Fabric, FabricHealer is built for that platform.

Homeostat is a release candidate (`1.0.0-rc.1`); see [Versioning](versioning.md) for what is stable and what is a
preview package.

See also: [Concepts](concepts.md) · [Guardrails](guardrails.md) · [Integrations](integrations.md) ·
[Cloud and orchestrators](cloud.md) · [FAQ](faq.md)

## References

Facts about the other tools were checked in November 2026.

- Polly, a .NET resilience and transient fault handling library: <https://github.com/App-vNext/Polly>
- Microsoft Learn, Introduction to resilient app development (`Microsoft.Extensions.Resilience`, built on Polly):
  <https://learn.microsoft.com/dotnet/core/resilience>
- Microsoft Learn, Health checks in ASP.NET Core: <https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks>
- Kubernetes, Liveness, Readiness, and Startup Probes:
  <https://kubernetes.io/docs/concepts/configuration/liveness-readiness-startup-probes/>
- Kubernetes, Configure Liveness, Readiness and Startup Probes:
  <https://kubernetes.io/docs/tasks/configure-pod-container/configure-liveness-readiness-startup-probes/>
- Kubernetes, Operator pattern: <https://kubernetes.io/docs/concepts/extend-kubernetes/operator/>
- Microsoft Learn, Azure App Service diagnostics overview (auto-heal and proactive auto-heal):
  <https://learn.microsoft.com/azure/app-service/overview-diagnostics>
- Azure App Service team blog, Introducing Proactive Auto Heal:
  <https://azure.github.io/AppService/2017/08/17/Introducing-Proactive-Auto-Heal.html>
- PagerDuty Runbook Automation: <https://www.pagerduty.com/platform/automation/runbook/>
- Atlassian, The evolution of IT operations (Opsgenie end of sale and end of support):
  <https://www.atlassian.com/blog/company-news/evolution-of-it-operations>
- FabricHealer on NuGet: <https://www.nuget.org/packages/Microsoft.ServiceFabricApps.FabricHealer.Linux.SelfContained>,
  source: <https://github.com/microsoft/service-fabric-healer>
- HealixNET on NuGet: <https://www.nuget.org/packages/HealixNET>, source: <https://github.com/VividZap/HealixNET>
