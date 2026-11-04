# FAQ & troubleshooting

## Troubleshooting

### Nothing happens when the problem occurs

Work through this list in order:

| Check | How |
|---|---|
| Is dry-run still on? | Look for `[dry-run]` in the logs. Set `o.DryRun = false`. |
| Do signals arrive? | Is the health check registered, and did you call `AddHealthCheckSignals()`? |
| Does the source name match? | `ForSource("x")` must equal the health check name exactly (case-sensitive). |
| Does the severity trigger? | The default threshold is `Unhealthy`; a `Degraded` check does not trigger it. |
| When does it activate? | On the first triggering signal after `SustainedFor` has elapsed; a slow signal period delays activation (see [Conditions](conditions.md#how-long-sustainedfor)). |
| Does the condition have a plan? | Without `Heal(...)` a condition is only observed. |
| Is Homeostat paused? | `IHomeostatControl.IsPaused`. |
| Is the condition suppressed? | `SuppressedBy` in the status endpoint or `GetConditions()`: its cause is active. |
| Is a step waiting for approval? | `pendingApprovals` in the status endpoint, or `await IHomeostatApprovals.GetPendingAsync()`. |
| Cooldown or budget? | Cooldown skips are logged at `Debug` (enable `Debug` for the `Homeostat` category); budget exhaustion is logged as an `Error`. |

### It ran the first step but did not escalate

The verification was probably **inconclusive**: no new signal arrived during the verification
timeout. Either make signals more frequent or extend the timeout:

```csharp
.Heal(p => p
    .With<RenewFeedClientAction>()
        .VerificationTimeout(TimeSpan.FromMinutes(2))   // applies to the step before it
    .Then<SwitchToBackupProviderAction>())
```

### The same action runs again after a few minutes

The condition is still active. After the cooldown, the next triggering signal starts a new run.
This is intended: a problem that is not fixed is retried at a controlled pace, within the budget.
Increase the cooldown or lower the budget if it is too frequent.

### The application fails at startup with an `InvalidOperationException`

The message says what is wrong. Common causes: two conditions with the same name, a condition with
neither `ForSource` nor `ClearWhen`, a plan with no actions.

## Questions

### Is this a replacement for Polly?

No. Polly and `Microsoft.Extensions.Resilience` protect **individual calls** (retry, timeout, circuit
breaker). Homeostat reacts to problems that **persist** at application level and verifies recovery.
Use both.

### Is it safe to use in production?

That is the goal of the defaults: dry-run on, budget, cooldown, timeouts, one run at a time, kill
switch, and no escalation without evidence. Start in dry-run, review the decisions, then enable
actions one plan at a time, and review the [security guide](security.md). The current version is a release
candidate for 1.0: the public API changes before 1.0.0 only if a problem found in the release candidate requires
it, and from 1.0.0 it follows semantic versioning (see [Versioning](versioning.md)).

### What happens with several instances of my application?

Each instance runs its own Homeostat with its own signals and conditions. By default each one also heals on
its own, which is right for problems local to the instance (its clients, caches, settings). For shared problems,
use a shared store and mark the plan `OncePerCluster()`: one instance acts, the cooldown is shared, and budget and
history cover the whole cluster. See [Persistence and clusters](persistence-and-clusters.md).

Approval requests, switches and the kill switch are still per instance: the approval endpoints act on the
instance that receives the request. Behind a load balancer, call each instance directly (for example through its
pod address), or use the webhook notifier, whose events carry the application name.

### How do I see what Homeostat is doing right now?

Map the status endpoint (`app.MapHomeostatStatus("policy")`, from `Homeostat.AspNetCore`), add `AddHomeostatCheck()` to your
health checks, or call `IHomeostatControl` from your own code. See [ASP.NET Core endpoints](aspnetcore.md).

### Can actions be async and use scoped services?

Yes. Actions return a `Task`, and each step runs in its own DI scope.

### Do I need OpenTelemetry?

No. Homeostat uses `System.Diagnostics` types from the BCL. OpenTelemetry is only needed if you want
to export traces and metrics.

### Which .NET versions are supported?

.NET 10, a long-term support release. .NET 8 and .NET 9 reach end of support on November 10, 2026, so Homeostat
does not target them. See [Platforms](platforms.md) for operating systems, client library versions and Native AOT,
and [Versioning](versioning.md) for how new .NET versions are adopted.

### Where does the name come from?

From *homeostasis*: the ability of a living system to return to a stable state after a disturbance.
