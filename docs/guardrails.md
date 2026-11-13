# Guardrails

Homeostat acts on a running application, so its defaults are deliberately conservative: when in doubt, it does
less. This page describes each guardrail (dry-run, budget, cooldown, concurrency, timeouts, pause, maintenance windows,
approval, suppression), the guardrails of [healing groups](#healing-groups), how Homeostat behaves when it fails
itself, and every global option.

```csharp
.AddHomeostat(h => h
    .Configure(o =>
    {
        o.DryRun = false;
        o.MaxActionsPerWindow = 5;
        o.ActionBudgetWindow = TimeSpan.FromHours(1);
        o.DefaultCooldown = TimeSpan.FromMinutes(10);
    }))
```

## Dry-run

**Default: on.**

In dry-run Homeostat tracks conditions normally, but instead of executing a plan it logs every step it would run:

```text
warn: [dry-run] Condition weather-feed-down: step 1/2 would execute RenewFeedClientAction.
```

Recommended rollout: run in dry-run in a real environment, check that conditions activate when and only when you
expect, then set `DryRun = false`. Dry-run runs still start the cooldown, so the logs are not flooded, and they do not
consume the action budget. Observers still receive condition events and `OnRunCompleted` (outcome `DryRun`, no steps),
but never `OnStepCompleted`.

## Action budget

**Default: 10 actions per rolling hour, across all plans.**

Before every executed action (dry-run steps do not count), Homeostat consumes one unit of a global budget.
Compensating actions count too, but an exhausted budget never stops one: it reverts a change that did not help. When
the budget is exhausted, the run stops with outcome `BudgetExhausted` and an error:

```text
fail: Condition weather-feed-down: action budget exhausted (10 actions per 01:00:00). Manual intervention required.
```

This protects you from a faulty signal or a misconfigured condition triggering a storm of interventions.
`IHomeostatControl.GetBudgetAsync()` reports how much of the budget is used. A [healing group](#healing-groups) can
have a budget of its own, spent inside this one.

## Cooldown

**Default: 5 minutes per plan.** Override per plan with `.Cooldown(...)`.

After a run ends, whatever the outcome, the same plan cannot start again until the cooldown has elapsed. The exception
is a run that stopped before its first step because the condition cleared, Homeostat was paused or the run was
suppressed between its start and step 1: it took no action, so it does not start the cooldown. If the
condition is still active after the cooldown, the next triggering signal starts a new run from step 1. With
[`CooldownBackoff`](healing-plans.md#cooldown-backoff), the cooldown grows after every run in a row that does not
heal.

Cooldowns, backoff and the budget live in the [store](persistence-and-clusters.md): with the default in-memory store
they reset on restart; with a file or Redis store they do not. When the store is unavailable, Homeostat fails closed
and does not start runs. A read of the plan state (or a cluster lease request) that has not answered in 10 seconds is
abandoned but not started again while it still runs: until it ends, the plan fails closed at once, so a store that
blocks does not get one more blocked call per triggering signal.

## Concurrency

**Default: 1 run at a time** (`MaxConcurrentHealings`).

- At most one run per condition, always.
- At most `MaxConcurrentHealings` runs overall; additional runs wait for a free slot.
- A run waiting for an approval gives its slot back until the answer arrives.
- A [healing group](#healing-groups) can have a limit of its own, inside this one.

## Timeouts

| Option | Default | Applies to |
|---|---|---|
| `DefaultActionTimeout` | 30 s | Each action and compensating action (`.ActionTimeout(...)` after a step) |
| `DefaultVerificationTimeout` | 1 min | Each verification (`.VerificationTimeout(...)` after a step) |
| `DefaultApprovalTimeout` | 15 min | Each approval request (`.RequireApproval(timeout)` after a step) |

When the action timeout expires, the action's `CancellationToken` is canceled and the step fails with a
`TimeoutException`. Homeostat stops waiting at that moment even if the action ignores the token: a stuck action (a
blocking call, a lost token) cannot hold the run, the concurrency slot or the shutdown. .NET cannot stop code that
ignores cancellation, so such an action keeps running in the background, and Homeostat logs an error saying so: pass
the token to every call an action makes.

A verifier gets its timeout, and Homeostat waits at most twice as long for it, plus the condition's `ClearAfter` (the
default verifier waits for a recovery in progress to hold). One that never returns ends the run as `Inconclusive`.

## What Homeostat does when it is the problem

Self-healing must never be what takes the application down. These are the rules, and the `ReliabilityTests` test
class checks them:

| Failure | What happens |
|---|---|
| The store never answers | Startup waits at most 5 seconds for the control state, then Homeostat counts as paused until it can read it. Control and status endpoints answer within 15 seconds (`503` when the store is needed and does not answer), the Homeostat health check within one, and shutdown is not delayed. |
| The store fails | Fail closed: no run starts whose cooldown or budget cannot be checked; store errors are logged without flooding the log. A pause, turning a switch off or releasing a switch Homeostat turned on apply at once on the instance and are written when the store answers; resuming or turning a switch on fail. |
| The store file is locked, or Redis is down at startup | The application starts; Homeostat fails closed and tries again (every 10 seconds). |
| The store file, or the Redis pause record, cannot be read | Homeostat counts as paused (the record may have held a person's pause) and logs a critical error; a person resumes it on purpose. The unreadable file is set aside as `*.corrupt`. |
| A condition in configuration is invalid | Logged as critical and skipped (`failOnInvalid: true` to stop instead). |
| The instance that turned on a switch for a cluster plan stops or crashes | Another instance turns it off as soon as the condition is not active there. |
| An action keeps its own condition active (for example, a paused consumer whose backlog grows) | `HoldAtMost` turns the switch off after a maximum time. |
| An abandoned action is still running when the plan runs again | It is not started a second time: the step fails until the previous call ends. |
| An action, compensating action or verifier never returns | Homeostat stops waiting at its timeout (twice the timeout plus `ClearAfter` for a verifier) and logs it. |
| A classifier (`IsFailure`) or a metric filter throws | The application's call or measurement is not affected: Homeostat counts it and moves on. |
| A detector, probe, observer or sink throws | Logged and isolated: the others go on. |
| Something escapes Homeostat's engine or detector loop | Logged as critical; self-healing stops, the application keeps running (an exception out of a background service would otherwise stop the host). |
| Signals arrive faster than they are processed | `TryPublish` never waits: the buffer (`SignalBufferCapacity`) drops the oldest. |
| Plans that would act too often | Dry-run by default, action budget (10 per hour), one run at a time, cooldown with backoff, restart limits. |

Every store call is bounded and starts off the caller's thread; while a read abandoned at its timeout still runs,
the periodic refresh and the health check do not start another, so a store blocked on I/O cannot pile up threads.

What Homeostat cannot prevent:

- An observer (`IHealingObserver`) runs on Homeostat's threads, so a slow one delays signal processing. Use an
  [event sink](observability.md) for slow work.
- Code that ignores cancellation keeps running after Homeostat stops waiting for it. It is not started again while it
  runs, but it keeps its thread, and the services of its step stay alive until it ends.

## Kill switch

Pause all interventions at runtime, for example during a deployment or an incident handled by people. With
`Homeostat.AspNetCore` the endpoints are ready ([details](aspnetcore.md)):

```csharp
app.MapHomeostatControl("operations");   // POST /homeostat/pause and /resume, ... (authorization policy)
```

Or call `IHomeostatControl` from your own code:

```csharp
await control.PauseAsync();                                         // until ResumeAsync
await control.PauseAsync(until: time.GetUtcNow().AddMinutes(30), by: "deploy");   // resumes by itself
await control.ResumeAsync();
```

A timed pause is handy for a deployment script that might not reach its last step. A new call replaces the previous
pause. `PauseReason` says why Homeostat is paused, for example
`paused until 2026-11-02T10:30:00.0000000+00:00 by deploy`, or the name of a maintenance window. With the endpoints:
`POST /homeostat/pause?for=00:30:00`.

The pause is kept in the [store](persistence-and-clusters.md#shared-control-state): with a shared store it stops
every instance within `ControlRefreshInterval` (5 seconds by default), and with the file or Redis store it survives
restarts. Pauses and resumes are written in the order they were decided, by the clock of the instance where each was
made (keep the clocks synchronized), so a late write never undoes a newer decision. A pause that the store refuses
because a resume was decided after it is logged as a warning naming that resume: the pause then ends on that instance
too, at its next read of the store.
`IsPaused` reads the instance's view of the pause and never waits.

While paused:

- conditions keep being tracked and remain visible through `GetConditions()`;
- no new run starts;
- a run in progress stops before its next step;
- a compensating action (`UndoWith`) of the step that just ran still runs: it reverts Homeostat's own change, and
  leaving it in place would keep the application degraded. The same holds during maintenance windows.

> [!IMPORTANT]
> The control endpoints require an authorization policy: they control what your application does to itself.

## Maintenance windows

Recurring periods during which Homeostat behaves as paused, without anybody having to remember to pause it:

```csharp
.AddMaintenanceWindow("nightly-deploy", w => w
    .Daily(new TimeOnly(2, 0), TimeSpan.FromHours(1))
    .On(DayOfWeek.Tuesday, DayOfWeek.Thursday)
    .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome")))
```

Windows may cross midnight and follow daylight saving time in their time zone (UTC by default). During a window,
`IsPaused` is `true` and `PauseReason` names the window; the status endpoint and the Homeostat health check show it
too. Without `On(...)` the window opens every day; the duration must be more than zero and less than a week.
`MaintenanceWindow.IsActive(now)` tells whether the window is open at a given time, and `Name`, `Start`, `Duration`,
`Days` and `TimeZone` describe it. A window cannot change once registered.

For a one-off deployment, prefer a timed pause (`PauseAsync(until)`, or `POST /homeostat/pause?for=...`): it ends by
itself even if the deployment script never reaches its last step. See
[Pause during deployments](recipes.md#pause-during-deployments).

A window restricted with `.ForGroups("reports")` pauses only the plans of those [healing groups](#windows-per-group).

## Healing groups

A healing group is a named set of conditions, typically one module of a modular monolith (reports, ingestion,
exports). A condition joins one with [`InGroup`](conditions.md#modules-ingroup) (or the `Group` key in
[configuration](configuration.md#condition-keys)). The group labels everything about its conditions (snapshots, runs,
approvals, events, metrics, logs) and has guardrails of its own, applied **on top of** the global ones, like
bulkheads: one module that misbehaves spends its own budget and its own slots, and can be paused alone. An
application that does not use groups behaves exactly as before.

```csharp
.AddHomeostat(h => h
    .Configure(o => { o.MaxActionsPerWindow = 10; o.MaxConcurrentHealings = 2; })
    .ConfigureGroup("reports", g =>
    {
        g.MaxActionsPerWindow = 3;          // at most 3 of the 10 actions per window
        g.MaxConcurrentHealings = 1;        // at most 1 of the 2 slots
    })
    .AddMaintenanceWindow("reports-reindex", w => w.Daily(new TimeOnly(3, 0), TimeSpan.FromHours(1)).ForGroups("reports"))
    .AddCondition("reports-db-slow", c => c.ForSource("reports-db").InGroup("reports").Heal(/* ... */)))
```

| Option of `HealingGroupOptions` | Default | Meaning |
|---|---|---|
| `MaxActionsPerWindow` | `null` (no group budget) | Actions the group may execute per window, inside the global budget |
| `ActionBudgetWindow` | `null` (the global `ActionBudgetWindow`) | Rolling window of the group budget |
| `MaxConcurrentHealings` | `null` (no group limit) | Runs of the group executing at the same time, inside the global limit |

`ConfigureGroup` can be called several times for the same group: the configurations apply in order, like
`Configure`. The options are validated when Homeostat starts (positive values, a window of at most about 49 days) and
read once then. A group that no condition uses is allowed (its conditions can come from configuration later) and
logged once at startup at Information level. A group needs no configuration to be paused or to have a maintenance
window. Group names are not empty, have at most 200 characters and no control characters.

`IHomeostatControl.GetGroups()` lists every group known on the instance (the groups of the conditions, the configured
ones and the paused ones) with its conditions, its own pause and its limits; `GetGroupBudgetAsync(group)` reports the
use of its budget from the store, or `null` for a group without a budget.

### Concurrency per group

A run takes **the slot of its group first, then a global slot**, and gives them back in the reverse order. A run that
waits for its busy group therefore holds no global slot that another group could use. While a step waits for an
approval, the run gives both slots back and takes them again, in the same order, when the answer arrives. A run
canceled while it waits for either slot (the application stops, a cluster lease is lost) leaves no slot taken.

### Budget per group

Before each executed action, Homeostat consumes one unit of the **group budget first** (when the group has one), then
one of the global budget. When the group budget is exhausted, the run stops with `BudgetExhausted` and the error names
it:

```text
fail: Condition reports-db-slow: action budget of group 'reports' exhausted (3 actions per 01:00:00). Manual intervention required.
```

The other groups go on. When the global budget is exhausted after the group unit was consumed, that unit is not given
back: it costs the group itself, never another group, and giving it back would need one more store write that could
fail in turn. Compensating actions count in both budgets, best effort, and neither budget ever blocks them. A group
budget that the store cannot check stops the run (`Failed`), like the global one: Homeostat fails closed. Group
budgets live in the [store](persistence-and-clusters.md#shared-control-state) next to the global budget, so with a
shared store they span every instance.

### Pause per group

```csharp
await control.PauseGroupAsync("reports", until: time.GetUtcNow().AddMinutes(30), by: "deploy");
await control.ResumeGroupAsync("reports", by: "on-call");
```

A paused group behaves for its runs exactly as Homeostat does under the [global pause](#kill-switch): no run of the
group starts, a run in progress stops before its next step with outcome `Paused`, its compensating action still
runs, and its conditions keep being tracked. The other groups go on. The global pause still pauses every group.

The group pause follows the global pause's rules, on the same code path: it is kept in the store and reaches every
instance within `ControlRefreshInterval`; pauses and resumes of a group are written in decision order (a late write
never undoes a newer decision about the same group; on a tie the pause wins); a pause made while the store is down
applies on the instance at once and is written when the store answers, while a resume fails; a resume that times out leaves the group paused (the pause is decided again after it); a timed pause ends
by itself; a group pause record Redis cannot read counts as a pause of that group; and until the control state is
first read, every group counts as paused. At most 256 groups can be paused at once (`ControlState.MaxGroupPauses`):
the pause of one more fails with `InvalidOperationException`, and resuming a group (a timed pause included, once it
ended) makes room. The instance checks the limit on what it knows: its view of the store or, before the state is first
read (a store down at startup), the group pauses still waiting to be written, so pauses deferred during an outage stay
bounded too. The store checks it again: when another instance took the last room within the refresh interval, the
store refuses the pause and `PauseGroupAsync` fails with the same `InvalidOperationException`; the pause is then
neither applied nor deferred. (Some stores and clients throw `InvalidOperationException` in an outage too, so the
instance reads the store to confirm the limit; when the read fails or shows room, the pause is deferred as in any
outage.) A pause deferred during an outage that the store later refuses for the limit is dropped and logged as a
warning, like a pause refused for a later resume. The pause that a timed-out resume decides again is never refused
here: it stays deferred until the store takes it.

`IsGroupPaused(group)` tells whether the plans of a group are suspended, by the global pause, the group's pause or one
of its windows, and `GetGroupPauseReason(group)` says why: the global `PauseReason` when Homeostat is paused (resuming
the group would not be enough), otherwise who paused the group and until when, or which window is open. Both read the
instance's view and never wait.

### Windows per group

```csharp
.AddMaintenanceWindow("reports-reindex", w => w.Daily(new TimeOnly(3, 0), TimeSpan.FromHours(1)).ForGroups("reports", "exports"))
```

A window with groups pauses only the runs of those groups, as a pause of each group would; `IsPaused` stays `false`
and `IsGroupPaused("reports")` is `true` during the window. A window without groups behaves as before: it pauses every
plan. `MaintenanceWindow.Groups` lists the groups of a window (empty for a global one).

## Approval

Risky steps can wait for a person: `.Then<FailoverAction>().RequireApproval()`. A rejected or unanswered
request stops the run with `NotApproved`, without escalating. A rejection holds the plan until the condition clears
or until [`ReleaseHoldAsync`](healing-plans.md#releasing-a-hold). See
[Human in the loop](healing-plans.md#human-in-the-loop-requireapproval).

## Suppression

A condition declared as a symptom with `SuppressedBy("cause")` is not healed while its cause is pending or active, so
one broken dependency does not spend the budget on every component that depends on it. See
[Causes and symptoms](conditions.md#causes-and-symptoms-suppressedby).

## Stopping instead of looping

Homeostat stops and logs it, instead of trying again immediately, in these cases (a warning for an inconclusive
verification, an error otherwise):

- verification is **inconclusive**;
- every step of the plan has been tried without success;
- the action budget, or the budget of the condition's group, is exhausted;
- an approval request expires.

These logs are good candidates for alerts (see [Observability](observability.md)).

For a checklist of the settings to review before production (endpoint policies, signatures, platform permissions),
see [Security](security.md).

## All options

| Option | Default | Description |
|---|---|---|
| `DryRun` | `true` | Log decisions without executing actions |
| `MaxActionsPerWindow` | `10` | Global action budget |
| `ActionBudgetWindow` | 1 hour | Rolling window of the budget |
| `MaxConcurrentHealings` | `1` | Runs executing at the same time |
| `DefaultCooldown` | 5 minutes | Pause between runs of the same plan |
| `DefaultActionTimeout` | 30 seconds | Maximum duration of an action |
| `DefaultVerificationTimeout` | 1 minute | Time allowed for verification |
| `VerificationPollInterval` | 1 second | Polling interval of the default verifier |
| `SignalBufferCapacity` | `1024` | In-memory signal buffer; oldest dropped when full |
| `DetectionInterval` | 10 seconds | How often [detectors](detectors.md) take a sample |
| `DefaultApprovalTimeout` | 15 minutes | Time to wait for a person to approve a step |
| `RunHistorySize` | `50` | Completed runs kept by the store, for `GetRecentRunsAsync()` and the status endpoint |
| `ConditionHistorySize` | `200` | Condition activations and clears kept by the store, for `GetConditionHistoryAsync()`, the history endpoint and coverage; `0` disables it |
| `ApplicationName` | entry assembly name | Included in notifications and audit records |
| `InstanceId` | machine name | Identifies the instance in runs, leases, traces and instance plan keys |
| `ClusterLeaseDuration` | 1 minute | Lease of `OncePerCluster()` plans, renewed while a run lasts |
| `ControlRefreshInterval` | 5 seconds | How often the instance reads the pause, the pauses of groups, the application switches and the answers to approval requests from the [store](persistence-and-clusters.md#shared-control-state) |

The options of each healing group are in [Healing groups](#healing-groups).

Options are validated when Homeostat starts and fail fast with a descriptive message: negative or zero values where a
positive one is required, timeouts or intervals above the .NET timer limit (about 49 days), a `DetectionInterval` below
1 ms, a `ClusterLeaseDuration` below one second and an empty `InstanceId`. Once Homeostat has started, the options are
frozen: setting a property throws `InvalidOperationException`, so code that resolved the options cannot lift a
guardrail such as `DryRun` or the budget at runtime.

See also: [Healing plans](healing-plans.md), [Persistence and clusters](persistence-and-clusters.md),
[ASP.NET Core](aspnetcore.md), [Security](security.md).
