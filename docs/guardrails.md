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

Before every executed action (dry-run steps do not count) Homeostat consumes one unit of a global budget. When the budget is exhausted,
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
| `DefaultActionTimeout` | 30 s | Each action (`.ActionTimeout(...)` per plan) |
| `DefaultVerificationTimeout` | 1 min | Each verification (`.VerificationTimeout(...)` per plan) |

Cancellation is cooperative: when the timeout expires, the action's `CancellationToken` is cancelled,
and the step fails if the action then throws. An action that ignores the token keeps the run waiting,
and if it eventually completes normally it is treated as successful.

## Kill switch

Pause all interventions at runtime, for example during a deployment or an incident handled by people.
With `Homeostat.AspNetCore` the endpoints are ready ([details](aspnetcore.md)):

```csharp
app.MapHomeostatControl("operations");   // POST /homeostat/pause and /resume, ... (authorization policy)
```

Or call `IHomeostatControl.Pause()` and `Resume()` from your own code. `PauseUntil(time)` pauses until a given
time and then resumes by itself, which is handy for a deployment script that might not reach its last step.
`PauseReason` says why Homeostat is paused. With the endpoints: `POST /homeostat/pause?for=00:30:00`.

While paused:

- conditions keep being tracked and remain visible through `GetConditions()`;
- no new run starts;
- a run in progress stops before its next step.

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
it too.

## Approval

Risky steps can wait for a person: `.Then<FailoverAction>().RequireApproval()`. A rejected or unanswered
request stops the run with `NotApproved`, without escalating. See
[Human in the loop](healing-plans.md#human-in-the-loop-requireapproval).

## Suppression

A condition declared as a symptom with `SuppressedBy("cause")` is not healed while its cause is active, so
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

Options are validated when Homeostat starts: negative or zero values where a positive one is required,
and timeouts or intervals above the .NET timer limit (about 49 days), fail fast with a descriptive message.
