# Persistence and clusters

By default Homeostat keeps its state in memory and every instance of your application acts on its own. That is
right for a single instance that can forget everything on restart. For anything else, choose a **store** (where
the state lives) and, for several instances, decide which plans run **once per cluster**.

## What is stored

| State | Why it matters |
|---|---|
| **Plan state**: end of the last run, failures in a row, held after a rejection | Cooldowns, [backoff](healing-plans.md#cooldown-backoff) and holds survive restarts and apply across instances |
| **Action budget** | A restart does not reset the budget; with a shared store, the budget is shared by every instance |
| **Run history** | `GetRecentRunsAsync()` and the status endpoint show what happened, on every instance, before the restart |
| **Condition history**: activations and clears, with the signal that caused them | `GetConditionHistoryAsync()`, `GET /homeostat/history`, the dashboard and the coverage reports see every instance, across restarts (`ConditionHistorySize`, 200 events by default). Events written while the store is down are retried every 30 seconds |
| **Documents**: small JSON documents kept by features, such as the learned baselines of [seasonal rules](conditions.md#seasonal-anomalies-whenseasonallyhigh-and-whenseasonallylow) | A deployment does not throw away weeks of learning |

The current state of the conditions (pending, active, clearing) is **not** stored: it is rebuilt from fresh signals
within a few detection intervals, which is what you want after a restart. The history of what they did is.

## Choosing a store

| Store | Package | Use it when |
|---|---|---|
| In memory (default) | `Homeostat` | One instance, state can be lost on restart |
| File | `Homeostat` | One instance that must remember cooldowns, budget and history across restarts |
| Redis | `Homeostat.Redis` | Several instances, or any deployment that already has Redis |
| Your own | — | Implement `IHomeostatStore` (for example on your database) and register it with `UseStore<T>()` |

```csharp
// One instance, state in a file (written atomically after every change)
.AddHomeostat(h => h.UseFileStore("data/homeostat.json"))

// Several instances
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("redis:6379"));
builder.Services.AddHomeostat(h => h.UseRedis(o => o.KeyPrefix = "weather-api:homeostat:"));
```

The file store writes atomically (temporary file, flushed, then renamed). A file that cannot be read is renamed
to `*.corrupt` and the store starts empty, and plans that have not run for 30 days are forgotten on load.

`UseRedis` uses the `IConnectionMultiplexer` already registered in your container; `UseRedis("redis:6379")`
creates one. Instances with the same `KeyPrefix` share their state: use one prefix per application and
environment.

### When the store fails

Homeostat **fails closed**: if it cannot read a plan's cooldown or consume the budget, it does not run the plan,
and logs an error (at most once a minute per condition). A failure to write the history is only a warning, and
the status endpoint keeps working, reporting the store error instead of budget and runs. Pair a shared store with the
[Homeostat health check](aspnetcore.md#homeostat-as-a-health-check), which reports the store as unavailable.

## Several instances

Each instance runs its own Homeostat: it receives its own signals and tracks its own conditions. What changes is
**where the plans run**.

### Instance plans (default)

Every instance heals its own copy of the problem: a stuck client in instance A is renewed by instance A. With a
shared store, each instance has its own cooldown and backoff (keys are prefixed with
`HomeostatOptions.InstanceId`), while the budget and the history are shared.

### Cluster plans: `OncePerCluster()`

Some problems are shared by all instances, and so is the fix: failing over to a backup provider, purging a shared
cache, scaling a dependency. Ten instances must not do it ten times.

```csharp
.AddCondition("forecast-provider-down", c => c
    .ForSource("http:forecast")
    .SustainedFor(TimeSpan.FromSeconds(30))
    .Heal(p => p
        .With<SwitchToBackupProviderAction>()
        .OncePerCluster()))
```

A cluster plan:

1. takes a **lease** named after the condition from the `IHomeostatCoordinator`; an instance that does not get it
   skips the run (the plan is running elsewhere);
2. reads and writes a **shared plan state**, so the cooldown applies to the whole cluster;
3. keeps the lease alive while the run lasts (renewed at a third of `ClusterLeaseDuration`, default 1 minute), and
   releases it at the end. If the instance dies, the lease expires and another instance can take over;
4. **stops before its next step if the lease may have been lost** (it could not be renewed for a whole lease
   duration, for example during a long Redis outage or severe thread pool starvation). The run ends `Cancelled`.

When the condition clears, only the instance that ran the plan last resets the shared hold and backoff: one
instance that recovers does not lift a rejection that concerns the whole cluster.

Leases are not fencing tokens: an action that is already running when the lease is lost finishes. Keep cluster
actions idempotent, and keep `ClusterLeaseDuration` well above the time a single step takes.

With the default in-process coordinator, "cluster" means the process: use `UseRedis` (or your own coordinator)
for real coordination.

### Instance identity

`HomeostatOptions.InstanceId` defaults to the machine name: stable across restarts (so the file store finds the
state of instance plans again) and, in containers, the pod or container name. It appears in run records, in the
status endpoint, in traces (`homeostat.instance`) and in the keys of instance plans. Set it explicitly when
several processes on the same machine share a store, or when your platform gives instances a better name.

## Redis keys

| Key | Type | Content |
|---|---|---|
| `{prefix}plan:{key}` | hash | `ended`, `failures`, `held`, `by`; updated by an atomic script; expires after `PlanStateRetention` (7 days) without changes |
| `{prefix}budget` | sorted set | One member per action, scored by time; updated by an atomic script |
| `{prefix}runs` | list | Run records as JSON, newest first, trimmed to `RunHistorySize` |
| `{prefix}lease:plan:{condition}` | string | Lease token, with an expiry |
| `{prefix}conditions` | list | Condition events as JSON, newest first, trimmed to `ConditionHistorySize` |
| `{prefix}doc:{key}` | string | A document, such as `doc:baseline:{condition}:{cycle}`; expires after `DocumentRetention` (90 days) without updates |

Set `ShareBudget = false` to count the budget per instance (`{prefix}budget:{instance}`). The budget uses the
clock of each instance: keep the clocks synchronized (NTP), as any distributed system expects.

## Writing your own store

`IHomeostatStore` has six required methods: read and update plan state, consume and count the budget, append and read
the run history. Five more have default implementations, so a store written for an earlier version keeps compiling:

- `SupportsConditionHistory`, `AppendConditionEventAsync`, `GetConditionEventsAsync`: the condition history. By default
  the store does not keep it (`false`), and Homeostat keeps it in process memory instead.
- `GetDocumentAsync`, `SetDocumentAsync`: small documents by key (a seasonal baseline is at most about 150 KB of JSON).
  By default nothing is kept, and seasonal baselines start over at every restart. The file store keeps documents until
  they are replaced: after renaming a seasonal condition, the old baseline stays in the file, unused.

Requirements:

- **Thread-safe**, and for a shared store, **atomic** plan-state updates and budget consumption across instances
  (the Redis store uses server-side scripts). `PlanStateUpdate.ApplyTo` defines what an update does: fields left
  `null` are kept, and `OnlyIfLastRunBy` is a precondition.
- Return `PlanState.Empty` for unknown plans.
- Throw on failure: Homeostat handles it (fail closed).

`InMemoryHomeostatStore` is a good reference; `FileHomeostatStore` shows how to persist it.
