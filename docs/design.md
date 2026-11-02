# Design notes

## Pipeline

```
DetectorHost (BackgroundService, PeriodicTimer on TimeProvider)
  └─ ISignalDetector.DetectAsync ──┐
Health check publisher ────────────┤
Your code ─────────────────────────┤
                                   ▼
                         ISignalPublisher ──► SignalChannel (bounded, drop-oldest)
                         │
                         ▼
                   HomeostatEngine (BackgroundService, single reader)
                         │  for each condition: ConditionTracker.Observe(signal)
                         ▼
                   Activated / StillActive ──► HealingRunner.TryStart
                   Cleared ──► reset switches          │ guardrails: paused? suppressed? cooldown? running?
                                                  ▼
                                            run (background task, concurrency-limited)
                                              for each step:
                                                paused / suppressed? → stop
                                                approval required? → wait (IHomeostatApprovals) → stop if not approved
                                                budget → action (own DI scope, timeout) → verifier
                                                Healed → stop
                                                NotHealed / action failed → next step
                                                Inconclusive → stop
                                              RunHistory ← run result
                                              ObserverNotifier → IHealingObserver (conditions, steps, runs, approvals)
                                                                   ├─ AuditLogSink  ┐ queue + background writer
                                                                   └─ WebhookSink   ┘ (EventSink)
```

## Principles

1. **Safe by default.** Dry-run is on until the user turns it off. Every action is bounded by a timeout,
   a cooldown and a global budget.
2. **Never escalate blindly.** Escalation needs evidence that the previous step did not work.
   Without evidence (`Inconclusive`) the run stops and logs.
3. **Easy to adopt.** One package, one `AddHomeostat` call, plain DI classes for actions.
   No base classes, no attributes, no required infrastructure.
4. **Observable.** Every decision is logged and traced; metrics cover runs, actions, outcomes and duration.
5. **Testable.** Time comes from `TimeProvider`; actions and verifiers are ordinary DI services.

## Condition semantics

- A signal is ignored unless the condition considers it *relevant* (source filter and/or custom filter).
- A relevant triggering signal moves `Inactive → Pending`; once triggering signals have persisted for
  `SustainedFor`, the condition becomes `Active`. Activation needs a signal arriving after the sustain
  window, so keep the signal period shorter than `SustainedFor`.
- A relevant clearing signal returns the condition to `Inactive`.
- While `Active`, each triggering signal gives the plan a chance to run again, subject to cooldown and budget.

## Threading

- The engine is the only writer of condition state; trackers lock internally so healing runs can read
  snapshots concurrently.
- Runs execute on the thread pool; at most `MaxConcurrentHealings` at a time and at most one per condition.
