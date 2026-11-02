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
| Error | `Healing run for condition {Condition} failed unexpectedly.` | Unexpected error |
| Information | `Condition {Condition}: cleared before step {Step}; nothing more to do.` | Run ended with `ConditionCleared` |
| Error | `Condition {Condition} failed to evaluate a signal from {Source}.` | A trigger or clear predicate threw |
| Warning | `Homeostat started without any registered condition.` | Configuration issue |
| Error | `Detector {Detector} failed.` | A detector threw |
| Error | `Healing observer {Observer} threw an exception.` | An observer threw |
| Debug | `Condition {Condition} is active, but Homeostat is paused.` | Kill switch on |
| Debug | `Condition {Condition} is active, but suppressed by {Cause}.` | Retry skipped while the cause is active |
| Debug | `Condition {Condition} is active, but its plan is held after a rejected approval.` | Retry skipped after a rejection |
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
| `homeostat.outcome` | `Healed` |

### Metrics

| Instrument | Type | Tags |
|---|---|---|
| `homeostat.healing.runs` | Counter | `condition` |
| `homeostat.healing.actions` | Counter | `condition`, `action`, `dry_run` |
| `homeostat.healing.outcomes` | Counter | `condition`, `outcome` |
| `homeostat.healing.duration` | Histogram (seconds) | `condition`, `outcome` |

Run outcomes: `Healed`, `NotHealed`, `Inconclusive`, `DryRun`, `BudgetExhausted`, `Paused`,
`Cancelled`, `Failed`.

Useful views:

- **Success rate per condition**: `outcomes{outcome="Healed"}` / `runs`.
- **Time to recover**: `duration` filtered by `outcome="Healed"`.
- **Noisy conditions**: `runs` by `condition` over time.

## Runtime state

`IHomeostatControl` exposes the current state:

| Member | Returns |
|---|---|
| `IsPaused`, `IsDryRun` | Kill switch and dry-run state |
| `GetConditions()` | A `ConditionSnapshot` per condition: status, timestamps, last signal, `ClearingSince`, `SuppressedBy` |
| `GetRecentRuns()` | The latest completed runs, newest first (`RunHistorySize`, default 50) |
| `GetBudget()` | Actions used and remaining in the current budget window |

`IHomeostatApprovals.GetPending()` lists the steps waiting for approval, and `IHomeostatSwitches.GetActive()`
the switches that are on.

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
| `OnRunCompleted(HealingRunResult)` | A run ends: outcome, all steps, start and end time |
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
