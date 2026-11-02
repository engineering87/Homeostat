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
| `ClearConnectionPools<TConnection>()` (`Homeostat.Data`) | Empties the ADO.NET provider's connection pools | Stale connections after a database failover |
| `PurgeHybridCacheTags(...)`, `RemoveHybridCacheKeys(...)` (`Homeostat.Caching`) | Invalidates `HybridCache` entries | Stale or poisoned cached data |
| `RemoveDistributedCacheKeys(...)` (`Homeostat.Caching`) | Removes `IDistributedCache` keys | Same, for a distributed cache |
| `CompactMemoryCache(percentage)` (`Homeostat.Caching`) | Evicts part of the memory cache | Memory pressure |
| `IsolateCircuit(control)` (`Homeostat.Polly`) | Isolates Polly circuit breakers until the condition clears | Fail fast while a dependency is known to be down |

### `RestartHostedService<T>()`

```csharp
builder.Services.AddHostedService<ForecastPoller>();

.Heal(p => p.RestartHostedService<ForecastPoller>())
```

Calls `StopAsync` and then `StartAsync` on the registered instance. With `BackgroundService` this
cancels the running `ExecuteAsync` loop and starts a fresh one. For custom `IHostedService`
implementations, make sure starting again after a stop is supported. If the service is not registered
with `AddHostedService`, the step fails and the plan escalates.

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
