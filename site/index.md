---
_layout: landing
---

# Homeostat

Self-healing for .NET applications: your application notices when something stays broken, fixes it, and checks that
the fix worked. Safe by default (dry-run, budgets, cooldowns, approvals), lightweight, and configured through
dependency injection.

![The Homeostat dashboard: a condition activates, a first step does not heal it, a person approves the failover, and the run is verified as healed](assets/demo.gif)

```bash
dotnet add package Homeostat --prerelease
dotnet add package Homeostat.HealthChecks --prerelease
```

| Start here | |
|---|---|
| [Why Homeostat](docs/why-homeostat.md) | The gap it fills, how it compares with Polly, health checks, probes and platform auto-heal |
| [Getting started](docs/getting-started.md) | Install, write a first action, watch the dry-run, then let it act |
| [Concepts](docs/concepts.md) | Signals, conditions, healing plans, verification and guardrails |
| [Packages](docs/packages.md) | Every package, what it adds and its stability |
| [API reference](xref:Homeostat) | Every public type and member, from the XML documentation |

Homeostat is open source under the MIT license: [source, issues and releases on GitHub](https://github.com/engineering87/Homeostat).
