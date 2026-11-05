# Concepts

The model behind Homeostat and the vocabulary the rest of the documentation uses. Read it after
[Getting started](getting-started.md) and before the reference pages.

Homeostat is built on four ideas: signals, conditions, healing plans and verification.

```mermaid
flowchart LR
    A["Signal<br/><sub>an observation</sub>"] --> B["Condition<br/><sub>a problem that persists</sub>"]
    B --> C["Healing plan<br/><sub>what to try, in order</sub>"]
    C --> D["Verification<br/><sub>did it work?</sub>"]
```

The name comes from *homeostasis*: the ability of a living system to bring itself back to a stable
state after a disturbance.

## 1. Signal: "here is what I see"

A **signal** is a single observation about one part of your application:

| Field | Example |
|---|---|
| `Source` | `"weather-feed"` |
| `Severity` | `Healthy`, `Degraded` or `Unhealthy` |
| `Timestamp` | when it was observed |
| `Value` | optional measurement, such as a latency in milliseconds or a queue length |
| `Message`, `Tags` | optional details |

Signals say nothing about what to do. They come from health checks, from built-in detectors (thread
pool, memory, exceptions, HTTP clients, circuit breakers), from alerts of monitoring tools and from your own code.
→ [Signals](signals.md) · [Detectors](detectors.md)

## 2. Condition: "this is a real problem"

A single bad signal is often noise: a timeout, a slow response, a restart. A **condition** turns a
stream of signals into a decision, and it has a lifecycle:

```mermaid
stateDiagram-v2
    direction LR
    [*] --> Inactive
    Inactive --> Pending: triggering signal
    Pending --> Active: still triggering after SustainedFor
    Pending --> Inactive: clearing signal
    Active --> Inactive: clearing signal
```

With `SustainedFor` at zero (the default), the first triggering signal makes the condition active at once. Only an
**active** condition starts its healing plan. → [Conditions](conditions.md)

## 3. Healing plan: "try this, then that"

A **healing plan** is an ordered list of steps, each running one action. Homeostat runs the first step; it moves to
the next only if it has evidence that the previous one did not work, or the action itself failed (threw or timed
out). Put the cheaper, less invasive steps first and the stronger ones later. → [Healing plans](healing-plans.md)

## 4. Verification: "did it actually work?"

After each action, a verifier checks the outcome instead of assuming success. The default verifier,
`ConditionClearedVerifier`, watches the condition until the verification timeout (1 minute by default):

| Outcome | Meaning (default verifier) | What happens next |
|---|---|---|
| `Healed` | The condition is no longer active: it cleared (a new episode may already be pending) | The run stops successfully |
| `NotHealed` | The condition is still active, and a signal arrived after the action | Escalate to the next step |
| `Inconclusive` | The condition is still active, and no signal arrived after the action | Stop, log, let a person decide |

A stopped run is not final: if the condition is still active when the cooldown ends, the next
triggering signal starts a new run from step 1. The exception is a step a person rejected: the plan then waits until
the condition clears (or until someone releases the hold).

The third outcome is deliberate: **Homeostat never escalates blindly.** If it cannot tell whether a
step worked, running a stronger one could make things worse.

## Around the loop: guardrails

Everything above runs inside safety limits: dry-run by default, a global action budget, cooldowns,
timeouts, a concurrency limit and a kill switch. → [Guardrails](guardrails.md)

Three more tools keep a self-healing system stable and accountable:

- **Hysteresis**: `ClearAfter` requires recovery to last before a condition clears, so it does not flap.
  → [Conditions](conditions.md#how-long-recovery-must-last-clearafter-hysteresis)
- **Causes and symptoms**: `SuppressedBy` stops Homeostat from healing the effects of a failure while its
  cause is active. → [Conditions](conditions.md#causes-and-symptoms-suppressedby)
- **Human in the loop**: `RequireApproval` makes a risky step wait for a person.
  → [Healing plans](healing-plans.md#human-in-the-loop-requireapproval)

## The whole loop

The diagram puts the pieces together. Cooldown, concurrency and the cluster lease are checked once, when a run
starts. The other guardrails and approval are checked before each step: a paused or suppressed
plan, a spent budget or a rejected approval stops the run without acting, and in dry-run Homeostat logs every step it
would run, then stops. A compensating action (`UndoWith`) runs before escalating when the step was verified as
`NotHealed` or its action timed out, not when the action threw. When no step is left, the run ends as `NotHealed`.

```mermaid
flowchart LR
    S["Signal<br/><sub>an observation</sub>"] --> C{"Condition<br/><sub>has the problem lasted<br/>SustainedFor?</sub>"}
    C -- "active" --> P["Healing plan<br/><sub>next step</sub>"]
    P --> G{"Guardrails<br/><sub>dry-run, pause,<br/>suppression, budget</sub>"}
    G -- "blocked" --> N(["No action<br/><sub>logged</sub>"])
    G -- "allowed" --> A{"Approval<br/><sub>steps marked<br/>RequireApproval</sub>"}
    A -- "rejected or expired" --> N
    A -- "approved or not required" --> X["Action<br/><sub>own DI scope, timeout</sub>"]
    X --> V{"Verification"}
    V -- "Healed" --> OK(["Condition cleared<br/><sub>run ends</sub>"])
    V -- "Inconclusive" --> H(["Run stops<br/><sub>a person decides</sub>"])
    X -- "threw or timed out" --> E["Escalation<br/><sub>undo if needed,<br/>then the next step</sub>"]
    V -- "NotHealed" --> E
    E -- "next step" --> P
    E -- "no step left" --> H
```

## A full run, step by step

```mermaid
sequenceDiagram
    autonumber
    participant HC as Health check
    participant E as Engine
    participant A1 as Step 1 (renew client)
    participant A2 as Step 2 (backup provider)
    HC->>E: weather-feed Unhealthy
    Note over E: condition Pending
    HC->>E: weather-feed Unhealthy (30s later)
    Note over E: condition Active, plan starts
    E->>A1: execute
    HC->>E: still Unhealthy
    Note over E: verification NotHealed, escalate
    E->>A2: execute
    HC->>E: Degraded (served by backup)
    Note over E: condition cleared, Healed
```

## Vocabulary

| Term | Meaning |
|---|---|
| **Source** | Logical name of an observed component, usually a health check name |
| **Trigger** | Rule that says a signal indicates the problem |
| **Clear rule** | Rule that says a signal indicates the problem is gone |
| **Run** | One execution of a healing plan, from the first step to its outcome |
| **Step** | One action within a plan |
| **Escalation** | Moving to the next step after a `NotHealed` outcome or a failed action |
| **Verifier** | What judges a step: `ConditionClearedVerifier` by default, or your own with `VerifyWith<T>()` |
| **Guardrail** | A limit around the loop: dry-run, budget, cooldown, concurrency, timeouts, kill switch |
| **Hysteresis** | Requiring recovery to last (`ClearAfter`) before a condition clears |
| **Suppression** | Not healing a symptom while its cause (`SuppressedBy`) is active |
| **Approval** | A person's yes or no on a step marked with `RequireApproval` |
| **Switch** | A named on/off flag that plans turn on and your code reads |
| **Store** | Where plan state, budget, history and the shared control state live (in memory by default) |

See also: [Healing plans](healing-plans.md) · [Guardrails](guardrails.md) · [Design notes](design.md)
