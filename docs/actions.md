# Actions

An action is what Homeostat does to fix a problem: the work of one step in a [healing plan](healing-plans.md). This
page covers the built-in actions, switches, inline actions and action classes. All three kinds can be mixed in the same
plan.

```csharp
.Heal(p => p
    .With("renew-client", sp => sp.GetRequiredService<ForecastClient>().Renew())   // inline
    .Then<FailOverToSecondaryRegionAction>()                                        // class
    .TurnOnSwitch("serve-cached-forecast"))                                         // built-in
```

## Built-in actions

| Method | What it does | Typical use |
|---|---|---|
| `RestartHostedService<T>()` | Stops and starts a hosted service | A background worker stuck in a bad state |
| `ReloadConfiguration()` | Reloads `IConfiguration` from all providers | Stale settings, rotated endpoints |
| `TurnOnSwitch(name)` | Turns on a named switch your code reads | Degraded mode: serve cache, skip non-essential work |
| `PauseConsumer(name)` | Pauses message consumers until the condition clears ([Message brokers](messaging.md)) | A dependency the consumer writes to is down: stop filling the dead-letter queue |
| `ClearConnectionPools<TConnection>()` | Empties every connection pool of the ADO.NET provider (its static `ClearAllPools()`) | Stale connections after a database failover |
| `PurgeHybridCacheTags(...)`, `RemoveHybridCacheKeys(...)` (`Homeostat.Caching`, preview) | Invalidates `HybridCache` entries | Stale or poisoned cached data |
| `RemoveDistributedCacheKeys(...)` (`Homeostat.Caching`, preview) | Removes `IDistributedCache` keys | Same, for a distributed cache |
| `CompactMemoryCache(percentage)` (`Homeostat.Caching`, preview) | Evicts a share of the memory cache entries (default 0.25) | Memory pressure |
| `IsolateCircuit(control)` (`Homeostat.Polly`) | Isolates Polly circuit breakers until the condition clears | Fail fast while a dependency is known to be down |
| `RestartApplication()` | Stops the process with an exit code so the platform starts a new one (approval by default) | State that only a restart clears |
| Kubernetes and Azure actions (`Homeostat.Kubernetes`; `Homeostat.Azure`, preview) | Restart pods and apps, scale, cordon, Front Door failover: see [Cloud and orchestrators](cloud.md) | Problems the platform must fix |

### `RestartHostedService<T>()`

Derive the worker from `RestartableBackgroundService` and put its loop in `RunAsync`:

```csharp
public sealed class ForecastPoller(ForecastClient client) : RestartableBackgroundService
{
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await client.PollAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}

builder.Services.AddHostedService<ForecastPoller>();

.Heal(p => p.RestartHostedService<ForecastPoller>())
```

The restart cancels the current run and starts a new one inside the same `ExecuteAsync`, so the generic host does not
notice. This matters: stopping a plain `BackgroundService` outside a shutdown makes the host treat it as failed and,
with the default `HostOptions.BackgroundServiceExceptionBehavior` (`StopHost`), stop the whole application.

> [!WARNING]
> The step refuses a plain `BackgroundService` (it fails and the plan escalates), unless the host is configured with
> `BackgroundServiceExceptionBehavior.Ignore`; the [analyzer](analyzers.md#homeo005) flags it at build time.

Other `IHostedService` implementations are stopped and started again: make sure starting after a stop is supported.
If the service is not registered with `AddHostedService`, the step fails.

`RestartableBackgroundService` also survives its own crashes: when `RunAsync` throws (or returns), the service stays
alive but idle, with `IsRunning = false` and the exception in `LastError`, instead of taking the application down.
`AddBackgroundServiceSignals()` turns that into a signal, so a condition can restart it:

```csharp
h.AddBackgroundServiceSignals()                        // source: hosted-service:{type name}
 .AddCondition("poller-crashed", c => c
     .ForSource("hosted-service:ForecastPoller")
     .Heal(p => p.RestartHostedService<ForecastPoller>()));
```

`AddBackgroundServiceSignals(o => o.SourcePrefix = "worker")` changes the prefix (`worker:ForecastPoller`).

| Member | Purpose |
|---|---|
| `RunAsync(stoppingToken)` | The work loop you write. The token is canceled on restart and on shutdown: honor it |
| `IsRunning` | `RunAsync` is running |
| `IsIdle` | The service is started but its run ended by itself (threw or returned): it waits to be restarted. `false` before the first run, during a restart and while stopping |
| `LastError` | The exception that ended the latest run, or `null` |
| `Restarts` | How many times the service has been restarted |
| `RestartAsync(cancellationToken)` | Cancels the current run and waits until a new one has started; also restarts an idle service. Throws `InvalidOperationException` when the service is not started, is stopping, or is called from its own `RunAsync`. This is what `RestartHostedService<T>()` calls, and you can call it from your own code |
| `OnRunFaulted(exception)` | Override to react when `RunAsync` throws (log, count, publish a signal); the service then waits to be restarted |

### `ReloadConfiguration()`

```csharp
.Heal(p => p.ReloadConfiguration())
```

Calls `IConfigurationRoot.Reload()`. Components that read settings through `IOptionsMonitor<T>` see the new values;
`IOptions<T>` values do not change after the first read. If the registered `IConfiguration` is missing or is not an
`IConfigurationRoot`, the step fails.

### `TurnOnSwitch(name)` and `IHomeostatSwitches`

Switches are named on/off flags that plans turn on and your code reads. They are the simplest way to add a degraded
mode.

```csharp
.Heal(p => p.TurnOnSwitch("serve-cached-forecast"))
```

```csharp
public sealed class ForecastService(ForecastClient client, ForecastCache cache, IHomeostatSwitches switches)
{
    public async Task<Forecast> GetAsync(string city, CancellationToken ct)
    {
        if (switches.IsOn("serve-cached-forecast"))
        {
            return cache.GetLast(city);
        }

        var forecast = await client.GetAsync(city, ct);
        cache.Store(city, forecast);
        return forecast;
    }
}
```

By default (`turnOffWhenCleared: true`) the switch goes back off automatically when the condition clears. To keep it
on until someone turns it off, pass `false`:

```csharp
.Heal(p => p.TurnOnSwitch("serve-cached-forecast", turnOffWhenCleared: false))
```

In a plan that runs on every instance, the step turns on a switch for the instance that runs the plan. It stays on
until the last condition that turned it on clears.

In a `OncePerCluster()` plan the step turns on an application switch, on every instance, owned by the condition:

- It is turned off when the condition clears on the instance that ran the plan (the one whose recovery the plan
  waited for).
- The owning instance renews its claim while it holds the switch. If that instance stops or crashes first, the claim
  is no longer renewed, and any instance where the condition is not active turns the switch off within a minute or so
  (at once if it stopped cleanly; later if the owner renews less often, as the claim records its renewal period).
- If the switch was turned off after the step started (a later decision is in the store), the step fails with an
  `InvalidOperationException`: the switch is not on, so the step did not do what it reports.
- A switch a person turned on is never taken over. A person who turns the switch off while the plan holds it keeps it
  off: a renewal of the claim is not a new decision, so a turn-off decided between the claim and its renewal still
  applies. Writes to the store are ordered by decision time.
- An application switch has one owner. When two conditions with cluster plans turn on the same switch, the first one
  owns it, and it goes off when that condition clears even if the other is still active (the other's step reports
  success, as the switch is on; the same when a person turned it on). Give each condition its own switch when they must hold it independently.
- One edge case: an instance that restarts with the same `InstanceId` and claims the switch again within a few
  seconds (a plan with almost no cooldown) may see it released once by another instance; its next run claims it
  again.

See [Shared control state](persistence-and-clusters.md#shared-control-state).

When the condition clears, Homeostat turns the switch off and logs it, but only a switch that this plan turned on
(not in dry-run, not one you turned on by hand), and only when no other active condition that turned it on still
needs it. Three cautions:

- The switch must not hide the problem from the signal. If the health check of the forecast provider calls the
  provider directly, it keeps reporting the real state and the condition clears only when the provider is back. If it
  goes through the cached path, it turns healthy as soon as the switch is on, the condition clears, the switch goes
  off, and the cycle repeats.
- Combine it with [`ClearAfter`](conditions.md#how-long-recovery-must-last-clearafter-hysteresis) so that a provider
  that is barely back does not get the full traffic at the first good answer.
- The opposite trap: a switch that keeps its own condition active never comes off. A circuit isolated by a condition
  on that same circuit stays isolated (an isolated circuit reports Unhealthy); consumers paused by a condition on a
  dead-letter count stay paused (the count cannot fall while they are paused); an origin taken out of rotation by a
  condition on its own traffic leaves the condition without signals. Base the condition on another signal, and add
  `.HoldAtMost(duration)` after the step as a safety net: the switch goes off after that time even if the condition
  has not cleared, and the plan runs again after its cooldown if it is still active.

With `turnOffWhenCleared: false`, turning the switch off is up to you, for example from the
[control endpoints](aspnetcore.md) (`POST /homeostat/switches/{name}/off`), the CLI (`homeostat switch off`) or your
own code:

```csharp
await switches.TurnOffAsync("serve-cached-forecast");
```

Your code can turn switches on too. `TurnOnAsync` turns on an application switch by default, kept in the store and on
for every instance that shares it; `SwitchScope.Instance` turns it on for this instance only:

```csharp
await switches.TurnOnAsync("serve-cached-forecast", by: "on-call");
await switches.TurnOnAsync("verbose-client-logs", SwitchScope.Instance);
```

`IsOn` reads the instance's view of the store and never waits, so it is cheap enough for every request.
`IHomeostatSwitches.GetActive()` lists the switches that are on, handy for a status page.

To react when a switch changes instead of reading it on every call, subscribe to `Changed`:

```csharp
switches.Changed += (_, e) =>
{
    if (e.Name == "serve-cached-forecast")
    {
        logger.LogInformation("Cached forecast mode is now {State}.", e.IsOn ? "on" : "off");
    }
};
```

`SwitchChangedEventArgs` carries the switch `Name` and `IsOn`. The event is raised after a switch is turned on or
off, including application switches changed on other instances (seen at the next refresh of the control state), and
not when a call leaves the switch unchanged. Handlers run synchronously on the thread that changed the switch or
refreshed the state, so keep them quick; an exception thrown by a handler is logged and does not reach the caller.
Under concurrent changes, events for the same switch can arrive out of order: read `IsOn(name)` in the handler when the
current state matters. The [feature flag integrations](integrations.md#feature-flags) use this event.

## Inline actions

For a one-liner, a class is overkill:

```csharp
// Synchronous, receives the step's services
.With("renew-client", sp => sp.GetRequiredService<ForecastClient>().Renew())

// Asynchronous, receives the step's services (the returned Task is awaited)
.With("renew-client", sp => sp.GetRequiredService<ForecastClient>().RenewAsync())

// Asynchronous, receives the full context and the cancellation token
.With("flush-buffer", async (context, ct) =>
{
    var buffer = context.Services.GetRequiredService<UploadBuffer>();
    await buffer.FlushAsync(ct);
})
```

> [!WARNING]
> A one-parameter lambda that returns a `ValueTask` binds to the synchronous overload, so it is not awaited. Wrap it:
> `async sp => await sp.Get...().RenewAsync()` becomes a `Task`, or use the two-parameter form
> `async (context, ct) => ...`.

The name appears in logs, metrics and run results, so make it descriptive.

## Action classes

For anything longer, or that you want to unit test, write a class:

```csharp
public sealed class FailOverToSecondaryRegionAction(ForecastClient client, ILogger<FailOverToSecondaryRegionAction> logger)
    : IHealingAction
{
    public async Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        logger.LogWarning("Failing over after {Source} became unhealthy.", context.TriggeringSignal.Source);
        await client.UseRegionAsync("secondary", cancellationToken);
    }
}
```

The class is registered as transient automatically (unless you registered it yourself) and resolved in a new DI
scope for each step. The step name is the class name. See [Healing plans](healing-plans.md#writing-actions) for
guidelines.

## Choosing the right kind

| Use | When |
|---|---|
| Built-in action | It does what you need |
| Inline action | One or two lines, no dependencies worth testing |
| Class | More logic, several dependencies, or you want unit tests ([Testing](testing.md#unit-testing-actions)) |

See also: [Healing plans](healing-plans.md), [Guardrails](guardrails.md), [Cloud and orchestrators](cloud.md),
[Message brokers](messaging.md).
