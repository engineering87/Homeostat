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
| **Pause**: who paused, when, and until when | With a shared store, a pause given on one instance stops every instance |
| **Application switches**: who turned them on, and the condition that owns them, if any | A switch turned on through any instance is on for all of them |
| **Approval requests**, pending or answered | Any instance lists and answers them; the answer reaches the instance whose run waits for it |

The current state of the conditions (pending, active, clearing) is **not** stored: it is rebuilt from fresh signals
within a few detection intervals, which is what you want after a restart. The history of what they did is.

## Shared control state

The pause, the application switches and the approval requests form the **control state**. Every instance keeps a
view of it in memory, read from the store at startup, every `HomeostatOptions.ControlRefreshInterval` (5 seconds by
default) and right after every change made on that instance. `IsPaused` and `IsOn` read the view, so they never wait
for the store. A change made on another instance applies here within one refresh interval; an answer to an approval
request reaches the waiting instance within the same delay. When the store cannot be read, the instance keeps its
last known view and logs a warning. Until the view has been read once (at startup, with the store unreachable),
Homeostat counts as paused, with the reason "waiting for the control state from the store": it cannot know whether a
pause is in place. A change written here whose read-back fails still applies here at once.

```csharp
await control.PauseAsync(until: time.GetUtcNow().AddMinutes(20), by: "deploy");   // every instance, within 5 s
await switches.TurnOnAsync("serve-cached-forecast");                            // SwitchScope.Application
await switches.TurnOnAsync("verbose-client-logs", SwitchScope.Instance);        // this instance only
```

Switches have a **scope** (`SwitchScope`):

| Scope | Where it is on | Kept |
|---|---|---|
| `Application` (default of `TurnOnAsync`) | Every instance that shares the store | In the store |
| `Instance` | The instance that turned it on | In its memory, until it stops |

`TurnOffAsync` turns a switch off for the application and for the instance that calls it; a switch that another
instance turned on for itself stays on there. `IsOn` and `GetActive()` cover both scopes, and `Changed` is raised for
application switches changed on other instances too.

The [`TurnOnSwitch`](actions.md#turnonswitchname-and-ihomeostatswitches) step picks the scope from the plan:

- in a `OncePerCluster()` plan it turns on an **application** switch, on every instance. With
  `turnOffWhenCleared: true` (the default) the condition owns the switch, and the switch is turned off when the
  condition clears on the instance that ran the plan, as for the plan state (if that instance stops first, turn the
  switch off by hand, or let the plan run again elsewhere: it then takes the switch over). The store claims and
  releases it atomically, so a switch a person turned on, even a moment before on another instance, stays theirs;
- in any other plan it turns on an **instance** switch, turned off when the condition clears on that instance.

Which store keeps what:

| Store | Pause | Switches turned on by a person or by code | Switches owned by a condition | Approval requests |
|---|---|---|---|---|
| In memory | Until the process stops | Until the process stops | Until the process stops | Until the process stops |
| File | Across restarts | Across restarts | Not kept: the condition starts again from scratch | Not kept: the run that waits does not survive a restart |
| Redis | Shared, across restarts | Shared, across restarts | Shared | Shared |

Requests whose instance stopped before collecting the answer are dropped by the Redis store a day after they expired.
The control endpoints (pause, resume, approvals, switches, release) answer `503` when the store cannot be reached: the
change was not made (see [ASP.NET Core](aspnetcore.md#control)).

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

// Several instances (dotnet add package Homeostat.Redis --prerelease)
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("redis:6379,abortConnect=false"));
builder.Services.AddHomeostat(h => h.UseRedis(o => o.KeyPrefix = "weather-api:homeostat:"));
```

### File store

The file store writes atomically (temporary file, flushed, then renamed). A change that cannot be written (a full
disk, a read-only volume) fails and is undone in memory too, so what the store reports always matches the file. A
file that cannot be read (bad JSON, or null records from a hand edit) is renamed to `*.corrupt` and the store starts
empty. While open, the store holds an exclusive lock on `{path}.lock`: a second process opening the same file (the
old and new process of a rolling deploy on a shared volume) fails at startup with a clear error instead of
overwriting the other's state. Use a path that survives a restart (a volume, in a container) and that only the
application's user can write.

```csharp
.AddHomeostat(h => h.UseFileStore("data/homeostat.json", o => o.PlanStateRetention = TimeSpan.FromDays(90)))
```

| `FileStoreOptions` | Default | Meaning |
|---|---|---|
| `PlanStateRetention` | 30 days | How long the cooldown and backoff state of a plan that no longer runs is kept. Older entries (renamed conditions, former instance names) are dropped when the file is loaded, so the file does not grow forever. Held plans, and plans that never completed a run, are always kept |

### Redis store

`UseRedis` uses the `IConnectionMultiplexer` already registered in your container; `UseRedis("redis:6379")`
creates one. Instances with the same `KeyPrefix` share their state: use one prefix per application and
environment. `UseRedis` also registers the Redis coordinator, so `OncePerCluster()` plans coordinate every instance.

| `RedisStoreOptions` | Default | Meaning |
|---|---|---|
| `KeyPrefix` | `homeostat:` | Prefix of every key; instances that share it share cooldowns, budget, history and leases |
| `ShareBudget` | `true` | One action budget for every instance; `false` counts it per instance |
| `PlanStateRetention` | 7 days | How long plan state is kept after its last change; refreshed at every run |
| `DocumentRetention` | 90 days | How long documents (such as seasonal baselines) are kept after their last update |

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
4. **stops before its next step if the lease may have been lost**. The run ends `Canceled`. The Redis coordinator
   declares the lease lost before the key can expire: when it could not renew it and less than a sixth of the lease
   duration is left (a margin for clock differences and for the run to notice), for example during a long Redis
   outage or severe thread pool starvation;
5. if the plan state cannot be saved at the end of the run (the store is down), **keeps the lease for the
   cooldown** instead of releasing it: the cooldown then exists only in this instance's memory, and releasing the
   lease would let another instance run the plan again at once.

When the condition clears, only the instance that ran the plan last resets the shared hold and backoff: one
instance that recovers does not lift a rejection that concerns the whole cluster. To lift a hold earlier, use
`IHomeostatControl.ReleaseHoldAsync(condition)` (see [Human in the loop](healing-plans.md#human-in-the-loop-requireapproval)).
Each instance caches a hold it has read and reads it again from the store after a minute, so a hold lifted on another
instance applies everywhere within a minute; `ReleaseHoldAsync` lifts it at once on the instance that calls it.

Leases are not fencing tokens: an action that is already running when the lease is lost finishes. Keep cluster
actions idempotent, and keep `ClusterLeaseDuration` well above the time a single step takes.

With the default in-process coordinator, "cluster" means the process: use `UseRedis` (or your own coordinator)
for real coordination.

### Writing your own coordinator

To coordinate through something you already run (a database, a lock service), implement `IHomeostatCoordinator` and
register it with `UseCoordinator<T>()` (a singleton) or `UseCoordinator(sp => ...)`. Pair it with a store that every
instance shares, since cluster plans read and write their state there.

```csharp
public sealed class DatabaseLeaseCoordinator(LeaseTable leases) : IHomeostatCoordinator
{
    public async ValueTask<IHomeostatLease?> TryAcquireAsync(string resource, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var token = Guid.NewGuid().ToString("N");
        return await leases.TryInsertAsync(resource, token, duration, cancellationToken)   // your code: insert if absent or expired
            ? new DatabaseLease(leases, resource, token, duration)
            : null;                                                                       // held elsewhere: the run is skipped
    }
}
```

```csharp
.AddHomeostat(h => h.UseStore<DatabaseStore>().UseCoordinator<DatabaseLeaseCoordinator>())
```

The contract:

- `TryAcquireAsync` returns `null` when another holder has the lease, and never waits for it.
- The returned `IHomeostatLease` keeps the lease alive until it is disposed: renew it in the background (Homeostat's
  coordinators renew at a third of `duration`), and release it in `DisposeAsync`. A holder that dies must let the
  lease expire after `duration`.
- `IHomeostatLease.Lost` is a `CancellationToken` canceled when the lease may have been lost (it could not be renewed
  in time). Homeostat then stops the run before its next step, with outcome `Canceled`.
- Renewal and release must check that the lease is still yours (compare the token), so a late instance does not
  extend or delete the lease of the next holder.

### Instance identity

`HomeostatOptions.InstanceId` defaults to the machine name: stable across restarts (so the file store finds the
state of instance plans again) and, in containers, the pod or container name. It appears in run records, in the
status endpoint, in traces (`homeostat.instance`), in the keys of instance plans and in approval requests (the
instance whose run waits for the answer). Set it explicitly when several processes on the same machine share a store,
or when your platform gives instances a better name.

Keep it **stable across restarts**: a new name starts with an empty cooldown and backoff for its instance plans. In
Kubernetes, a StatefulSet gives each pod a stable name; a Deployment gives a new name to every new pod.

## Redis keys

| Key | Type | Content |
|---|---|---|
| `{prefix}plan:{key}` | hash | `ended`, `failures`, `held`, `by`; updated by an atomic script; expires after `PlanStateRetention` (7 days) without changes |
| `{prefix}budget` | sorted set | One member per action, scored by time; updated by an atomic script |
| `{prefix}runs` | list | Run records as JSON, newest first, trimmed to `RunHistorySize` |
| `{prefix}lease:plan:{condition}` | string | Lease token, with an expiry |
| `{prefix}conditions` | list | Condition events as JSON, newest first, trimmed to `ConditionHistorySize` |
| `{prefix}doc:{key}` | string | A document, such as `doc:baseline:{condition}:{cycle}`; expires after `DocumentRetention` (90 days) without updates |
| `{prefix}{control}:pause` | string | The pause as JSON (`until`, `by`, `at`) |
| `{prefix}{control}:switches` | hash | Application switches by name, as JSON (`condition`, `instance`, `by`, `at`); a plan claims or releases one with an atomic script |
| `{prefix}{control}:approvals` | hash | Approval requests by id, as JSON, with the waiting instance |
| `{prefix}{control}:answers` | hash | Answers by request id, written by an atomic script so that only the first answer wins |

The control keys share the hash tag `{control}`, so on Redis Cluster they are in one slot, as the approval script needs.

Set `ShareBudget = false` to count the budget per instance (`{prefix}budget:{instance}`). The budget uses the
clock of each instance: keep the clocks synchronized (NTP), as any distributed system expects.

## Writing your own store

`IHomeostatStore` has fourteen required methods: read and update plan state, consume and count the budget, append and
read the run history, and the control state (`GetControlStateAsync`, `SetPauseAsync`, `SetSwitchAsync`,
`TryClaimSwitchAsync`, `TryReleaseSwitchAsync`, `AddApprovalAsync`, `TryResolveApprovalAsync`, `RemoveApprovalAsync`, with the types `ControlState`, `PauseState`,
`SwitchState` and `ApprovalRecord` of `Homeostat.Persistence`). Six more have default implementations, and members added after 1.0 will have them too:

- `SupportsConditionHistory`, `AppendConditionEventAsync`, `GetConditionEventsAsync`: the condition history. By default
  the store does not keep it (`false`), and Homeostat keeps it in process memory instead.
- `SupportsDocuments`, `GetDocumentAsync`, `SetDocumentAsync`: small documents by key (a seasonal baseline is at most
  about 150 KB of JSON). By default nothing is kept (`false`), seasonal baselines start over at every restart, and
  Homeostat logs a warning once. The file store keeps documents until
  they are replaced: after renaming a seasonal condition, the old baseline stays in the file, unused.

Requirements:

- **Thread-safe**, and for a shared store, **atomic** plan-state updates, budget consumption and answers to approval
  requests across instances (the Redis store uses server-side scripts). `PlanStateUpdate.ApplyTo` defines what an
  update does: fields left `null` are kept, and `OnlyIfLastRunBy` is a precondition. `TryResolveApprovalAsync`
  answers a request only if it is still pending: of two answers given at the same time, exactly one wins.
  `TryClaimSwitchAsync` sets a switch only if it is off or owned by the same condition, and `TryReleaseSwitchAsync`
  removes it only if that condition (and instance) still owns it, so a plan never overrides a person's decision.
- `GetControlStateAsync` skips records it cannot read rather than failing: one bad record must not hide the pause.
- Return `PlanState.Empty` for unknown plans.
- Throw on failure: Homeostat handles it (fail closed).

`InMemoryHomeostatStore` is a good reference; `FileHomeostatStore` shows how to persist it. Both are sealed: wrap
them rather than derive from them.
