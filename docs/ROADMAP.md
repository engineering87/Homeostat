# Roadmap

The roadmap is indicative and may change based on feedback.

## 0.1 (preview) — current

**Core loop**
- [x] Signals and in-process signal buffer (`ISignalPublisher`)
- [x] Conditions with source filter, thresholds, `SustainedFor` and clear rules
- [x] Healing plans with escalation chain
- [x] Three-outcome verification (`Healed`, `NotHealed`, `Inconclusive`) with pluggable verifiers
- [x] Guardrails: dry-run by default, global action budget, cooldown, concurrency limit, timeouts, kill switch

**Ready-made signals**
- [x] Health checks as signals (`Homeostat.HealthChecks`)
- [x] Detectors: thread pool starvation, memory pressure, exception rate, custom `ISignalDetector`
- [x] HTTP client failure ratio and latency (`Homeostat.Http`)
- [x] Polly v8 circuit breaker state (`Homeostat.Polly`)

**Ready-made actions**
- [x] Inline lambda actions
- [x] Restart a hosted service, reload configuration
- [x] Named switches for degraded mode (`IHomeostatSwitches`)

**Coordination**
- [x] Hysteresis on recovery (`ClearAfter`) and on thresholds (trigger and clear rules)
- [x] Suppression of downstream symptoms (`SuppressedBy`)
- [x] Human approval for risky steps (`RequireApproval`, `IHomeostatApprovals`)
- [x] Automatic reset of switches when a condition clears

**Operations and testing**
- [x] Tracing and metrics via `System.Diagnostics`
- [x] Observers for conditions, steps, runs and approvals (`IHealingObserver`)
- [x] Run history and budget status (`IHomeostatControl`)
- [x] Audit log (JSON Lines) and webhook notifier
- [x] `Homeostat.AspNetCore`: status and control endpoints; Homeostat as a health check
- [x] `Homeostat.Testing`: in-memory host with simulated time, run results, approvals, `HealingContextFactory`, HTTP fault injection
- [x] Release pipeline with SourceLink, symbols and package validation

## 0.x

- [ ] Public API baseline enforced on every release
- [ ] Rate-limited retries of a plan with backoff between runs
- [ ] Persisting approvals and history across restarts (optional store)

## 1.0

- [ ] Stable API
- [ ] Complete documentation site

## Later

- [ ] Adaptive tuning of thresholds based on action history
- [ ] Out-of-process signal transports via adapters

## Out of scope

- Per-call resilience (retries, timeouts, circuit breakers): use Polly.
- Infrastructure orchestration (Kubernetes operators, autoscaling): Homeostat may expose hooks, not replace them.
