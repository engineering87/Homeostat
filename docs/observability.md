# Observability

A system that fixes itself must also explain itself. Homeostat logs every decision, traces every run
and exposes metrics and runtime state.

## Logs

Categories: `Homeostat.Engine.HomeostatEngine` (conditions), `Homeostat.Engine.HealingRunner` (runs),
`Homeostat.Detectors.DetectorHost` (detectors), `Homeostat.Engine.ObserverNotifier` (observers) and the
built-in sinks `Homeostat.Notifications.AuditLogSink` and `Homeostat.Http.WebhookSink`.

| Level | Message | Meaning |
|---|---|---|
| Warning | `Condition {Condition} is active (source {Source}, severity {Severity}).` | A condition activated |
| Information | `Condition {Condition} cleared.` | A condition returned to inactive |
| Information | `Condition {Condition} is suppressed by {Cause}: not healing while {Cause} is active.` | Activated while its cause is active |
| Information | `Condition {Condition} cleared: switch {Switch} turned off.` | Switch reset on clear |
| Warning | `[dry-run] Condition {Condition}: step {Step}/{Total} would execute {Action}.` | Dry-run decision |
| Warning | `[dry-run] Condition {Condition}: step {Step}/{Total} would request approval, then execute {Action}.` | Dry-run decision for an approval step |
| Warning | `Condition {Condition}: step {Step}/{Total} {Action} is waiting for approval {ApprovalId} until {ExpiresAt}.` | Approval requested |
| Warning | `Condition {Condition}: approval {ApprovalId} for {Action} granted by {By}.` | Approved |
| Warning | `Condition {Condition}: approval {ApprovalId} for {Action} rejected by {By}.` | Rejected; the run stops |
| Warning | `Condition {Condition}: plan held after the rejection; it will run again only after the condition clears.` | Plan held |
| Information | `Condition {Condition}: approval {ApprovalId} for {Action} withdrawn.` | Condition cleared or application stopping |
| Error | `Condition {Condition}: approval {ApprovalId} for {Action} expired. Manual intervention required.` | Nobody answered |
| Warning | `Condition {Condition}: executing step {Step}/{Total} {Action}.` | An action is starting |
| Information | `Condition {Condition}: healed by {Action}.` | Verified recovery |
| Warning | `Condition {Condition}: {Action} did not heal the condition.` | Escalating |
| Warning | `Condition {Condition}: verification after {Action} was inconclusive; not escalating further.` | Stopped for lack of evidence |
| Error | `Condition {Condition}: {Action} timed out.` | Action exceeded its timeout |
| Error | `Condition {Condition}: {Action} threw an exception.` | Action failed |
| Error | `Condition {Condition}: all {Count} healing steps were tried without success. Manual intervention required.` | Plan exhausted |
| Error | `Condition {Condition}: action budget exhausted ({Max} actions per {Window}). Manual intervention required.` | Budget exhausted |
| Warning | `Condition {Condition}: Homeostat was paused, stopping before step {Step}.` | Paused during a run |
| Warning | `Condition {Condition}: suppressed by {Cause}, stopping before step {Step}.` | Cause activated during a run |
| Error | `Condition {Condition}: verification after {Action} threw an exception.` | A verifier failed; the step is inconclusive |
| Error | `Condition {Condition}: the verifier after {Action} returned {Value}, which is not a VerificationOutcome; treated as Inconclusive.` | A verifier returned an undefined value |
| Error | `Healing run for condition {Condition} failed unexpectedly.` | Unexpected error |
| Information | `Condition {Condition}: cleared before step {Step}; nothing more to do.` | Run ended with `ConditionCleared` |
| Error | `Condition {Condition} failed to evaluate a signal from {Source}.` | A trigger or clear predicate threw |
| Warning | `Homeostat started without any registered condition.` | Configuration issue |
| Error | `Detector {Detector} failed.` | A detector threw |
| Error | `Healing observer {Observer} threw an exception.` | An observer threw |
| Debug | `Condition {Condition} is active, but Homeostat is paused.` | Kill switch on |
| Debug | `Condition {Condition} is active, but suppressed by {Cause}.` | Retry skipped while the cause is active |
| Debug | `Condition {Condition}: not starting, {Reason}.` | Cooldown, backoff or hold |
| Debug | `Condition {Condition}: the plan is running on another instance.` | `OncePerCluster()` lease taken |
| Warning | `Condition {Condition}: {Action} did not heal; compensating with {Undo}.` | Compensation |
| Error | `Condition {Condition}: compensating action {Undo} failed.` | Compensation failed |
| Error | `Condition {Condition}: the store is unavailable; not starting the plan.` | Fail closed (Error at most once a minute per condition, Debug otherwise) |
| Error | `Condition {Condition}: the store is unavailable; the action budget cannot be checked.` | Fail closed |
| Error | `Condition {Condition}: could not save the plan state.` | Store write failed |
| Warning | `Condition {Condition}: could not record the run in the history.` | Store write failed |
| Information | `Homeostat conditions reloaded from configuration ({Count} conditions).` | [Configuration](configuration.md) change applied |
| Error | `The Homeostat configuration is not valid; keeping the previous conditions.` | Configuration change rejected |
| Warning | `The Homeostat lease {Lease} was lost; another instance may take over.` | Redis lease taken over during a run |
| Warning | `The Homeostat lease {Lease} could not be renewed in time; another instance may take over.` | Redis unreachable for a whole lease duration |
| Warning | `Condition {Condition}: the cluster lease was lost during the run, which was stopped.` | Run ended `Canceled` |
| Information | `Condition {Condition} was removed from configuration: switch {Switch} turned off.` | Configuration change |
| Warning | `{Sink} could not deliver a {Kind} event for condition {Condition}.` | Audit log or webhook failed |
| Warning | `{Sink} stopped before delivering all queued events.` | Shutdown timeout reached |
| Warning | `{Sink} dropped {Count} event(s) raised after the application stopped.` | Late events |

Skipped runs because of cooldown or a run already in progress are logged at `Debug` level.

**Suggested alerts:** every `Error` from `Homeostat.Engine.HealingRunner`, `Inconclusive` warnings and
approval requests (or use the [webhook notifier](notifications.md), which sends them for you).

## Traces and metrics

Homeostat uses `System.Diagnostics` directly, with no extra dependencies. The activity source and the
meter are both named `Homeostat` (`HomeostatDiagnostics.Name`).

```csharp
using Homeostat.Diagnostics;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(HomeostatDiagnostics.Name))
    .WithMetrics(m => m.AddMeter(HomeostatDiagnostics.Name));
```

### Activity

Each run produces an activity named `homeostat.heal` with these tags:

| Tag | Example |
|---|---|
| `homeostat.condition` | `weather-feed-down` |
| `homeostat.signal.source` | `weather-feed` |
| `homeostat.instance` | `weather-api-7d9f8` (`HomeostatOptions.InstanceId`) |
| `homeostat.run.id` | `3f2b9c0d...` (`HealingRunResult.RunId`, also on the step events) |
| `homeostat.outcome` | `Healed` |

### Metrics

| Instrument | Type | Tags |
|---|---|---|
| `homeostat.healing.runs` | Counter | `condition` |
| `homeostat.healing.actions` | Counter | `condition`, `action`, `dry_run` |
| `homeostat.healing.outcomes` | Counter | `condition`, `outcome` |
| `homeostat.healing.duration` | Histogram (seconds) | `condition`, `outcome` |

Run outcomes: `Healed`, `NotHealed`, `Inconclusive`, `DryRun`, `BudgetExhausted`, `Paused`, `NotApproved`,
`Suppressed`, `ConditionCleared`, `Canceled`, `Failed` (see [Run outcomes](healing-plans.md#run-outcomes)).

Gauges describe the current state of each instance:

| Instrument | Type | Tags |
|---|---|---|
| `homeostat.conditions` | Gauge | `status` (`Inactive`, `Pending`, `Active`) |
| `homeostat.approvals.pending` | Gauge | |
| `homeostat.switches.on` | Gauge | |
| `homeostat.paused` | Gauge (0 or 1) | |
| `homeostat.dry_run` | Gauge (0 or 1) | |

### Grafana dashboard

[`deploy/grafana/homeostat-dashboard.json`](../deploy/grafana/homeostat-dashboard.json) is a ready-made dashboard
for Prometheus: active conditions, pending approvals, switches and paused instances; runs per condition, outcomes,
actions executed (and simulated in dry-run), p95 run duration, and outcomes by condition. Import it in Grafana and
choose the Prometheus data source; the `Application` variable filters by the `job` label. It expects the metric names
produced by the OpenTelemetry Prometheus exporter (`homeostat_conditions`, `homeostat_healing_runs_total`,
`homeostat_healing_duration_seconds_bucket`...).

Useful views:

- **Success rate per condition**: `outcomes{outcome="Healed"}` / `runs`.
- **Time to recover**: `duration` filtered by `outcome="Healed"`.
- **Noisy conditions**: `runs` by `condition` over time.

## Runtime state

`IHomeostatControl` exposes the current state:

| Member | Returns |
|---|---|
| `IsPaused`, `PauseReason`, `IsDryRun` | Pause state (kill switch, timed pause, maintenance window) and dry-run; the pause is this instance's view of the [shared control state](persistence-and-clusters.md#shared-control-state) |
| `GetConditions()` | A `ConditionSnapshot` per condition: status, timestamps, last signal, `ClearingSince`, `SuppressedBy` |
| `GetRecentRunsAsync()` | The latest completed runs, newest first, from the store (`RunHistorySize`, default 50); with a shared store, of every instance |
| `GetBudgetAsync()` | Actions used and remaining in the current budget window, from the store |
| `GetConditionHistoryAsync()` | The latest activations and clears, newest first, with the signal that caused them (`ConditionHistorySize`, default 200); from the store, so with a shared store of every instance |
| `GetCoverageAsync(since)` | What was exercised in a period: see [Learning and foresight](intelligence.md#coverage) |

`await IHomeostatApprovals.GetPendingAsync()` lists the steps waiting for approval (from the store, so with a shared
store of every instance), and `IHomeostatSwitches.GetActive()` the switches that are on, for the application or for
this instance. `PauseAsync`, `ResumeAsync` and `ReleaseHoldAsync` change the state: see [Guardrails](guardrails.md#kill-switch)
and [Releasing a hold](healing-plans.md#releasing-a-hold).

The quickest way to see all of it is the status endpoint of [Homeostat.AspNetCore](aspnetcore.md):
`app.MapHomeostatStatus("policy")` maps `GET /homeostat/status`. For `/health`, add Homeostat's own health check with
`AddHealthChecks().AddHomeostatCheck()`.

## Observers: react to events

For the common cases there are built-in observers: an [audit log and a webhook notifier](notifications.md).
For anything else, implement `IHealingObserver`, for example to open an incident in your tracker. Implement
only the methods you need:

```csharp
public sealed class IncidentNotifier(IncidentQueue queue) : IHealingObserver
{
    public void OnRunCompleted(HealingRunResult run)
    {
        if (run.Outcome is HealingRunOutcome.NotHealed or HealingRunOutcome.Inconclusive or HealingRunOutcome.BudgetExhausted)
        {
            queue.Enqueue($"Homeostat could not heal {run.ConditionName}: {run.Outcome}.");   // your code
        }
    }
}
```

```csharp
.AddHomeostat(h => h.AddObserver<IncidentNotifier>())
```

| Method | Called when |
|---|---|
| `OnConditionActivated(ConditionSnapshot)` | A condition becomes active |
| `OnConditionCleared(ConditionSnapshot)` | An active condition clears |
| `OnStepCompleted(HealingStepResult)` | A step finishes: name, success, verification outcome, error |
| `OnRunCompleted(HealingRunResult)` | A run ends: outcome, all steps, start and end time, run id (also on each step) and instance |
| `OnApprovalRequested(PendingApproval)` | A step waits for approval: id, step, expiry |
| `OnApprovalResolved(ApprovalResolution)` | The request was approved, rejected, expired or withdrawn, and by whom |

Condition events are raised on the engine loop and step/run events on the run's thread pool thread, so
an observer can be called concurrently and must be thread-safe. Observers are called in registration
order. `OnStepCompleted` fires only for executed steps (not in dry-run, not for steps skipped by budget or
pause).

`AddObserver<T>()` resolves `T` from the container (registered as a singleton unless you registered it
yourself, in which case keep it a singleton), so the same instance can be injected elsewhere. Observers are singletons called **synchronously** on Homeostat's threads: keep them fast and hand off
slow work (HTTP calls, database writes) to a queue or a background service. Exceptions thrown by an
observer are logged and do not affect healing.
