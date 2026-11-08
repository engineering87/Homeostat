# FAQ & troubleshooting

Answers to the questions that come up when Homeostat does something you did not expect, or does nothing, and to
common questions about production use.

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

The verification was probably **inconclusive**: the condition was still active, but no new signal arrived during the
verification timeout (1 minute by default, `DefaultVerificationTimeout`). The log says
`verification after ... was inconclusive; not escalating further`. Make signals more frequent or extend the timeout:

```csharp
.Heal(p => p
    .With<RenewFeedClientAction>()
        .VerificationTimeout(TimeSpan.FromMinutes(2))   // applies to the step before it
    .Then<SwitchToBackupProviderAction>())
```

### The same action runs again after a few minutes

The condition is still active. After the cooldown (5 minutes by default), the next triggering signal starts a new
run. This is intended: a problem that is not fixed is retried at a controlled pace, within the budget. Increase the
cooldown, add `CooldownBackoff`, or lower the budget if it is too frequent.

### The application fails at startup with an `InvalidOperationException`

The message says what is wrong. Common causes: two conditions with the same name, a condition with
neither `ForSource` nor `ClearWhen`, a plan with no actions, a `SuppressedBy` or composite condition that names a
condition that is not registered, or a cycle between them.

## Questions

### Is this a replacement for Polly?

No, they work together. See [Why Homeostat](why-homeostat.md) for a comparison with Polly and other tools, and an
example that combines them.

### Does Homeostat need to know my architecture?

Yes, and you provide it. Homeostat runs the self-healing loop the same way for any application; what is specific to
yours (which components to watch, when a failure is real, what depends on what, which actions are safe and in which
order) you state in conditions and healing plans. See
[Where the knowledge of your architecture goes](concepts.md#where-the-knowledge-of-your-architecture-goes).

### Is it safe to use in production?

The defaults are chosen for that: dry-run on, budget, cooldown, timeouts, one run at a time, kill
switch, and no escalation without evidence. Start in dry-run, review the decisions, then enable
actions one plan at a time, and review the [security guide](security.md). The current version is a release
candidate for 1.0: the public API changes before 1.0.0 only if a problem found in the release candidate requires
it, and from 1.0.0 the stable packages follow semantic versioning. Preview packages (gRPC, Entity Framework Core,
caching, feature flags, Azure, Aspire) can still change in a minor version (see
[Stable and preview packages](versioning.md#stable-and-preview-packages)).

### What happens with several instances of my application?

Each instance runs its own Homeostat with its own signals and conditions. By default each one also heals on
its own, which is right for problems local to the instance (its clients, caches, settings). For shared problems,
use a shared store and mark the plan `OncePerCluster()`: one instance acts, the cooldown is shared, and budget and
history cover the whole cluster. See [Persistence and clusters](persistence-and-clusters.md).

The pause, the application switches and the approval requests are kept in the store too: with a shared store, a pause,
a switch or an answer given on any instance applies to every instance within `ControlRefreshInterval` (5 seconds by
default), so behind a load balancer any instance can take the request. Switches turned on with
`scope=instance` stay on the instance that took the request. See
[Shared control state](persistence-and-clusters.md#shared-control-state).

### What if Homeostat itself, or its store, fails?

The application keeps running and serving: Homeostat stops acting, not the application. A store that does not answer
delays startup by 5 seconds at most and never blocks a request, a health check or the shutdown; Homeostat fails
closed (no new interventions) while it cannot check cooldowns and budget; a pause or a switch turned off still applies
at once; switches it turned on are turned off when their condition clears, when they reach `HoldAtMost`, or by another
instance when the one that turned them on is gone. The full list is in
[What Homeostat does when it is the problem](guardrails.md#what-homeostat-does-when-it-is-the-problem).

### How do I see what Homeostat is doing right now?

Map the status endpoint (`app.MapHomeostatStatus("policy")`, from `Homeostat.AspNetCore`), add `AddHomeostatCheck()` to your
health checks, or call `IHomeostatControl` from your own code. See [ASP.NET Core endpoints](aspnetcore.md).

### Can actions be async and use scoped services?

Yes. Actions return a `Task`, and each step runs in its own DI scope.

### Do I need OpenTelemetry?

No. Homeostat uses `System.Diagnostics` types from the BCL. You need OpenTelemetry only to export the traces and
metrics.

### Which .NET versions are supported?

.NET 10, a long-term support release. .NET 8 and .NET 9 reach end of support on November 10, 2026, so Homeostat
does not target them. See [Platforms](platforms.md) for operating systems, client library versions and Native AOT,
and [Versioning](versioning.md) for how new .NET versions are adopted.

### Where does the name come from?

From *homeostasis*: the ability of a living system to return to a stable state after a disturbance.

See also: [Guardrails](guardrails.md) · [Observability](observability.md) · [Recipes](recipes.md)
