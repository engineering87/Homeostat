# Testing

Self-healing logic is code, and it deserves tests. The `Homeostat.Testing` package lets you test
**whole healing scenarios** in milliseconds, with simulated time, and unit test your actions in isolation.

```bash
dotnet add package Homeostat.Testing
```

## Scenario tests with `HomeostatTestHost`

`HomeostatTestHost` runs Homeostat in memory with a simulated clock (`FakeTimeProvider`). From the moment
the host starts, simulated time moves forward automatically in the background (1 simulated second every
5 ms of real time by default), so a 5-minute cooldown or a 1-minute verification
takes a fraction of a second.

```csharp
[Fact]
public async Task Renewing_the_client_heals_the_feed()
{
    await using var host = await HomeostatTestHost.StartAsync(
        h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.With("renew", sp => sp.GetRequiredService<FakeFeed>().Renew()))),
        services => services.AddSingleton<FakeFeed>());

    host.Publish("feed", SignalSeverity.Unhealthy);
    var run = await host.WaitForRunAsync("feed-down");

    Assert.Equal(HealingRunOutcome.Inconclusive, run.Outcome);   // no signal after the action
}
```

The outcome in this example is `Inconclusive` on purpose: nothing published a new signal after the
action, so the default verifier had no evidence. To test a full recovery, publish the recovery signal
yourself or let your fake publish it:

```csharp
host.Publish("feed", SignalSeverity.Unhealthy);
// ... the action runs; then the dependency recovers:
host.Publish("feed", SignalSeverity.Healthy);
var run = await host.WaitForRunAsync("feed-down");
Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
```

### What you can assert on

`WaitForRunAsync` returns a `HealingRunResult`:

| Property | Content |
|---|---|
| `Outcome` | `Healed`, `NotHealed`, `Inconclusive`, `DryRun`, `BudgetExhausted`, `Paused`, `NotApproved`, `Suppressed`, `ConditionCleared`, `Cancelled`, `Failed` |
| `Steps` | One `HealingStepResult` per executed step: `Name`, `Succeeded`, `Verification`, `Error` |
| `StartedAt`, `CompletedAt` | Simulated times |

```csharp
Assert.Collection(run.Steps,
    s => Assert.Equal(VerificationOutcome.NotHealed, s.Verification),
    s => Assert.Equal(VerificationOutcome.Healed, s.Verification));
```

### Host API

| Member | Purpose |
|---|---|
| `StartAsync(configure, services, options)` | Build and start a host |
| `Publish(source, severity, message)` | Publish a signal stamped with simulated time |
| `WaitForRunAsync(condition)` | Wait for the next completed run of a condition; each run is returned once |
| `WaitForConditionAsync(condition, status)` | Wait until a condition reaches a status |
| `WaitForApprovalAsync(condition)` | Wait until a step of the condition asks for approval; returns the request |
| `CompletedRuns` | All runs completed so far |
| `Control`, `Switches`, `Approvals`, `Signals`, `Services` | The real Homeostat services |
| `Time` | The `FakeTimeProvider`, to advance time manually |

### Host options

| Option | Default | Meaning |
|---|---|---|
| `DryRun` | `false` | Tests usually want real actions |
| `AutoAdvanceStep` | 1 s | Simulated time added at each tick; `null` disables automatic advancing |
| `AutoAdvanceInterval` | 5 ms | Real time between ticks |
| `DefaultTimeout` | 10 s | Real time before waiting methods throw `TimeoutException` |
| `StartTime` | 2030-01-01 | Initial simulated time |

Timeout messages include the current state of the condition, which usually explains what went wrong.

### Testing approvals

```csharp
host.Publish("weather-feed", SignalSeverity.Unhealthy);

var request = await host.WaitForApprovalAsync("weather-feed-down");
Assert.Equal(nameof(SwitchToBackupProviderAction), request.StepName);
host.Approvals.Approve(request.Id, "test");

var run = await host.WaitForRunAsync("weather-feed-down");
Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
```

Simulated time keeps moving while the test waits, so the approval timeout elapses quickly (15 simulated
minutes take about 4.5 seconds). Answer right after `WaitForApprovalAsync`, or use `RequireApproval(timeout)`
with a longer timeout in the test. To test expiry, do nothing and expect `NotApproved`.

> Action timeouts (`ActionTimeout`) follow simulated time too. With automatic advancing, 30 simulated
> seconds pass in about 150 ms of real time, so a fake action that waits on real time (for example
> `Task.Delay` without the `TimeProvider`) can time out. Keep fake actions fast, or raise `ActionTimeout`.

## Simulating faults

`FaultSwitch` and `FaultInjectingHandler` simulate an unreachable HTTP dependency:

```csharp
var fault = new FaultSwitch();

services.AddHttpClient("forecast")
    .AddHomeostatSignals()
    .AddHttpMessageHandler(() => new FaultInjectingHandler(fault));   // 503 while active

fault.Activate();     // the dependency "goes down"
fault.Deactivate();   // and comes back
```

Add the fault handler after `AddHomeostatSignals()`: an outer fault handler would answer before the
Homeostat handler sees the request.

Pass a different status code to the constructor if you need one, for example `HttpStatusCode.BadGateway`.

## Unit testing actions

`HealingContextFactory` creates a context so you can call an action directly:

```csharp
[Fact]
public async Task Failover_switches_region()
{
    var client = new FakeForecastClient();
    var action = new FailOverToSecondaryRegionAction(client, NullLogger<FailOverToSecondaryRegionAction>.Instance);

    await action.ExecuteAsync(HealingContextFactory.Create(conditionName: "feed-down"), CancellationToken.None);

    Assert.Equal("secondary", client.Region);
}
```

All parameters are optional: condition name, triggering signal, services, condition state, step name
and index, start time.

## Testing custom detectors

Detectors are plain classes: call `DetectAsync` with your own `ISignalPublisher` implementation that
collects signals, then assert on them. To test a detector end to end, register it in a
`HomeostatTestHost` with `AddDetector<T>()`: the detection interval runs on simulated time.

## Replaying recorded signals

`HomeostatReplay` runs recorded signals (`RecordSignals`) through the conditions and plans you pass in, in dry-run on
simulated time, and reports what would have happened: a regression test on real traffic for a new threshold or plan.
See [Learning and foresight](intelligence.md#recording-and-replay).

