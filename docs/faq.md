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
| Is a step waiting for approval? | `pendingApprovals` in the status endpoint, or `IHomeostatApprovals.GetPending()`. |
| Cooldown or budget? | Cooldown skips are logged at `Debug` (enable `Debug` for the `Homeostat` category); budget exhaustion is logged as an `Error`. |

### It ran the first step but did not escalate

The verification was probably **inconclusive**: no new signal arrived during the verification
timeout. Either make signals more frequent or extend the timeout:

```csharp
.Heal(p => p
    .With<RenewFeedClientAction>()
    .Then<SwitchToBackupProviderAction>()
    .VerificationTimeout(TimeSpan.FromMinutes(2)))
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
actions one plan at a time. The project is in preview, so evaluate it carefully before relying on it.

### What happens with several instances of my application?

Each instance runs its own Homeostat with its own signals, conditions and budget. Actions should
therefore fix things **local to the instance** (its clients, caches, settings). Coordinating actions
across instances is out of scope for now.

Approval requests, switches, run history and the kill switch are per instance too: the status endpoint and
the approval endpoints act on the instance that receives the request. Behind a load balancer, call each
instance directly (for example through its pod address), or use the webhook notifier, whose events carry
the application name.

### How do I see what Homeostat is doing right now?

Map the status endpoint (`app.MapHomeostatStatus("policy")`, from `Homeostat.AspNetCore`), add `AddHomeostatCheck()` to your
health checks, or call `IHomeostatControl` from your own code. See [ASP.NET Core endpoints](aspnetcore.md).

### Can actions be async and use scoped services?

Yes. Actions return a `Task`, and each step runs in its own DI scope.

### Do I need OpenTelemetry?

No. Homeostat uses `System.Diagnostics` types from the BCL. OpenTelemetry is only needed if you want
to export traces and metrics.

### Which .NET versions are supported?

.NET 10, the current long-term support release. .NET 8 and .NET 9 reach end of support on
November 10, 2026, so Homeostat does not target them; new .NET versions are added as they are released.

### Where does the name come from?

From *homeostasis*: the ability of a living system to return to a stable state after a disturbance.
