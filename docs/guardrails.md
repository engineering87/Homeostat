# Guardrails

Homeostat acts on a running application, so its defaults are deliberately conservative.
**When in doubt, it does less.**

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

In dry-run Homeostat tracks conditions normally, but instead of executing a plan it logs every step it
would run:

```text
warn: [dry-run] Condition weather-feed-down: step 1/2 would execute RenewFeedClientAction.
```

Recommended rollout: run in dry-run in a real environment, check that conditions activate when and
only when you expect, then set `DryRun = false`. Dry-run runs still count toward cooldowns, so the logs
are not flooded. They do not consume the action budget. Observers still receive condition events and
`OnRunCompleted` (outcome `DryRun`, no steps), but never `OnStepCompleted`.

## Action budget

**Default: 10 actions per rolling hour, across all plans.**

Before every executed action (dry-run steps do not count), Homeostat consumes one unit of a global budget. Compensating
actions count too, but an exhausted budget never stops one: it reverts a change that did not help. When the budget is exhausted,
the run stops with an error:

```text
fail: Condition weather-feed-down: action budget exhausted (10 actions per 01:00:00). Manual intervention required.
```

This protects you from a faulty signal or a misconfigured condition triggering a storm of
interventions.

## Cooldown

**Default: 5 minutes per plan.** Override per plan with `.Cooldown(...)`.

After a run ends, whatever the outcome, the same plan cannot start again until the cooldown has
elapsed. If the condition is still active after the cooldown, the next triggering signal starts a new
run from step 1. With [`CooldownBackoff`](healing-plans.md#cooldown-backoff), the cooldown grows after every run
in a row that does not heal.

Cooldowns, backoff and the budget live in the [store](persistence-and-clusters.md): with the default in-memory
store they reset on restart; with a file or Redis store they do not. When the store is unavailable Homeostat
fails closed and does not start runs.

## Concurrency

**Default: 1 run at a time** (`MaxConcurrentHealings`).

- At most **one run per condition**, always.
- At most `MaxConcurrentHealings` runs **overall**; additional runs wait for a free slot.

## Timeouts

| Option | Default | Applies to |
|---|---|---|
| `DefaultActionTimeout` | 30 s | Each action and compensating action (`.ActionTimeout(...)` after a step) |
| `DefaultVerificationTimeout` | 1 min | Each verification (`.VerificationTimeout(...)` after a step) |

When the timeout expires, the action's `CancellationToken` is canceled and the step fails with a `TimeoutException`.
Homeostat stops waiting at that moment even if the action ignores the token: a stuck action (a blocking call, a
lost token) cannot hold the run, the concurrency slot or the shutdown. .NET cannot stop code that ignores
cancellation, so such an action keeps running in the background, and Homeostat logs an error saying so: pass the
token to every call an action makes. A verifier gets its timeout and Homeostat waits at most twice as long for it, plus the condition's
`ClearAfter` (the default verifier waits for a recovery in progress to hold); one that never returns ends the run as
`Inconclusive`.

## What Homeostat does when it is the problem

Self-healing must never be what takes the application down. These are the rules, and `ReliabilityTests` checks them:

| Failure | What happens |
|---|---|
| The store never answers | Startup waits at most 5 seconds for the control state, then Homeostat counts as paused until it can read it. Control and status endpoints answer within 15 seconds (`503` when the store is needed and does not answer), the Homeostat health check within one, and shutdown is not delayed. |
| The store fails | Fail closed: no run starts whose cooldown or budget cannot be checked; store errors are logged without flooding the log. A pause, turning a switch off or releasing a switch Homeostat turned on apply at once on the instance and are written when the store answers; resuming or turning a switch on fail. |
| The store file is locked, or Redis is down at startup | The application starts; Homeostat fails closed and tries again. |
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

What Homeostat cannot prevent: an observer (`IHealingObserver`) runs on Homeostat's threads, so a slow one delays
signal processing (use an [event sink](observability.md) for slow work); and code that ignores cancellation keeps
running after Homeostat stops waiting for it (it is not started again while it runs, but it keeps its thread, and
its services may be disposed under it when the run ends).

## Kill switch

Pause all interventions at runtime, for example during a deployment or an incident handled by people.
With `Homeostat.AspNetCore` the endpoints are ready ([details](aspnetcore.md)):

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
pause. `PauseReason` says why Homeostat is paused, for example `paused until 2026-11-02T10:30:00.0000000+00:00 by deploy`, or
the name of a maintenance window. With the endpoints: `POST /homeostat/pause?for=00:30:00`.

The pause is kept in the [store](persistence-and-clusters.md#shared-control-state): with a shared store it stops
every instance within `ControlRefreshInterval` (5 seconds by default), and with the file or Redis store it survives
restarts. `IsPaused` reads the instance's view of it and never waits.

While paused:

- conditions keep being tracked and remain visible through `GetConditions()`;
- no new run starts;
- a run in progress stops before its next step;
- a compensating action (`UndoWith`) of the step that just ran still runs: it reverts Homeostat's own change, and
  leaving it in place would keep the application degraded. The same holds during maintenance windows.

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
`IsPaused` is `true` and `PauseReason` names the window; the status endpoint and the Homeostat health check show
it too. Without `On(...)` the window opens every day; the duration must be more than zero and less than a week.
`MaintenanceWindow.IsActive(now)` tells whether the window is open at a given time, and `Name`, `Start`, `Duration`,
`Days` and `TimeZone` describe it.

For a one-off deployment, prefer a timed pause (`PauseAsync(until)`, or `POST /homeostat/pause?for=...`): it ends by itself
even if the deployment script never reaches its last step. See [Pause during deployments](recipes.md#pause-during-deployments).

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

Homeostat stops and logs it, instead of trying again immediately, when (a warning for an
inconclusive verification, an error otherwise):

- verification is **inconclusive**;
- every step of the plan has been tried without success;
- the action budget is exhausted;
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
| `ControlRefreshInterval` | 5 seconds | How often the instance reads the pause, the application switches and the answers to approval requests from the [store](persistence-and-clusters.md#shared-control-state) |

Options are validated when Homeostat starts: negative or zero values where a positive one is required,
and timeouts or intervals above the .NET timer limit (about 49 days), fail fast with a descriptive message.
Once Homeostat has started the options are frozen: setting a property throws `InvalidOperationException`, so code
that resolved the options cannot lift a guardrail such as `DryRun` or the budget at runtime.
