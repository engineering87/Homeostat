# Actions

An action is **what Homeostat does** to fix a problem. You can use built-in actions, write an inline
lambda, or write a class. All three can be mixed in the same plan.

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
| `ClearConnectionPools<TConnection>()` (`Homeostat.Data`) | Empties the ADO.NET provider's connection pools | Stale connections after a database failover |
| `PurgeHybridCacheTags(...)`, `RemoveHybridCacheKeys(...)` (`Homeostat.Caching`) | Invalidates `HybridCache` entries | Stale or poisoned cached data |
| `RemoveDistributedCacheKeys(...)` (`Homeostat.Caching`) | Removes `IDistributedCache` keys | Same, for a distributed cache |
| `CompactMemoryCache(percentage)` (`Homeostat.Caching`) | Evicts part of the memory cache | Memory pressure |
| `IsolateCircuit(control)` (`Homeostat.Polly`) | Isolates Polly circuit breakers until the condition clears | Fail fast while a dependency is known to be down |
| `RestartApplication()` | Stops the process with an exit code so the platform starts a new one (approval by default) | State that only a restart clears |
| Kubernetes and Azure actions | Restart pods and apps, scale, cordon, Front Door failover: see [Cloud and orchestrators](cloud.md) | Problems the platform must fix |

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

The restart cancels the current run and starts a new one **inside the same `ExecuteAsync`**, so the generic host does
not notice. This matters: stopping a plain `BackgroundService` outside a shutdown makes the host treat it as failed
and, with the default `HostOptions.BackgroundServiceExceptionBehavior` (`StopHost`), **stop the whole application**.
So the step refuses a plain `BackgroundService` (it fails and the plan escalates), unless the host is configured with
`BackgroundServiceExceptionBehavior.Ignore`; the [analyzer](analyzers.md#homeo005) flags it at build time. Other
`IHostedService` implementations are stopped and started again: make sure starting after a stop is supported. If the
service is not registered with `AddHostedService`, the step fails.

`RestartableBackgroundService` also survives its own crashes: when `RunAsync` throws (or returns), the service stays
alive but idle, with `IsRunning = false` and the exception in `LastError`, instead of taking the application down.
`AddBackgroundServiceSignals()` turns that into a signal, so a condition can restart it:

```csharp
h.AddBackgroundServiceSignals()                        // source: service:{type name}
 .AddCondition("poller-crashed", c => c
     .ForSource("service:ForecastPoller")
     .Heal(p => p.RestartHostedService<ForecastPoller>()));
```

| Member | Purpose |
|---|---|
| `RunAsync(stoppingToken)` | The work loop you write. The token is canceled on restart and on shutdown: honor it |
| `IsRunning` | `RunAsync` is running |
| `IsIdle` | The service is started but its run ended by itself (threw or returned): it waits to be restarted. `false` before the first run, during a restart and while stopping |
| `LastError` | The exception that ended the latest run, or `null` |
| `Restarts` | How many times the service has been restarted |
| `RestartAsync(cancellationToken)` | Cancels the current run and waits until a new one has started; also restarts an idle service. Throws `InvalidOperationException` when the service is not started or is stopping. This is what `RestartHostedService<T>()` calls, and you can call it from your own code |
| `OnRunFaulted(exception)` | Override to react when `RunAsync` throws (log, count, publish a signal); the service then waits to be restarted |

### `ReloadConfiguration()`

```csharp
.Heal(p => p.ReloadConfiguration())
```

Calls `IConfigurationRoot.Reload()`. Components that read settings through `IOptionsMonitor<T>` see the
new values; `IOptions<T>` values do not change after the first read.

### `TurnOnSwitch(name)` and `IHomeostatSwitches`

Switches are named on/off flags that **plans turn on and your code reads**. They are the simplest way
to add a degraded mode.

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

To go back automatically when the condition clears, ask for it:

```csharp
.Heal(p => p.TurnOnSwitch("serve-cached-forecast", turnOffWhenCleared: true))
```

When the condition clears, Homeostat turns the switch off and logs it, but only a switch that this plan actually
turned on (not in dry-run, not one you turned on by hand), and only when no other active condition that turned
it on still needs it. Two cautions:

- The switch must not hide the problem from the signal. If the health check of the forecast provider calls
  the provider directly, it keeps reporting the real state and the condition clears only when the provider is
  back. If it goes through the cached path, it turns healthy as soon as the switch is on, the condition
  clears, the switch goes off, and the cycle repeats.
- Combine it with [`ClearAfter`](conditions.md#how-long-recovery-must-last-clearafter-hysteresis) so that
  a provider that is barely back does not get the full traffic at the first good answer.

Without `turnOffWhenCleared`, turning the switch off is up to you, for example from the
[control endpoints](aspnetcore.md) (`POST /homeostat/switches/{name}/off`) or your own code:

```csharp
switches.TurnOff("serve-cached-forecast");
```

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
off, not when a call leaves it unchanged. Handlers run synchronously on the thread that changed the switch, so keep
them quick; an exception thrown by a handler is logged and does not reach the caller. Under concurrent changes,
events for the same switch can arrive out of order: read `IsOn(name)` in the handler when the current state matters.
The [feature flag integrations](integrations.md#feature-flags) use this event.

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

> **Watch out for `ValueTask`.** A one-parameter lambda that returns a `ValueTask` binds to the
> synchronous overload, so it is **not awaited**. Wrap it: `async sp => await sp.Get...().RenewAsync()`
> becomes a `Task`, or use the two-parameter form `async (context, ct) => ...`.

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

The class is registered as transient automatically and resolved in a new DI scope for each step.
The step name is the class name. See [Healing plans](healing-plans.md#writing-actions) for guidelines.

## Choosing the right kind

| Use | When |
|---|---|
| Built-in action | It does what you need |
| Inline action | One or two lines, no dependencies worth testing |
| Class | More logic, several dependencies, or you want unit tests ([Testing](testing.md#unit-testing-actions)) |
