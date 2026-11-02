# Recipes

Ready-to-adapt solutions for common scenarios. Methods marked *(your code)* stand for your own logic.

- [Fail over to a backup provider](#fail-over-to-a-backup-provider)
- [Ask before failing over](#ask-before-failing-over)
- [Serve cached data until the provider is back](#serve-cached-data-until-the-provider-is-back)
- [Do not heal symptoms of a shared failure](#do-not-heal-symptoms-of-a-shared-failure)
- [Post to a team chat](#post-to-a-team-chat)
- [Drain a growing backlog](#drain-a-growing-backlog)
- [Reload configuration](#reload-configuration)
- [Verify with a direct probe](#verify-with-a-direct-probe)
- [Pause during deployments](#pause-during-deployments)
- [Observe before acting](#observe-before-acting)
- [Unit test an action](#unit-test-an-action)

---

## Fail over to a backup provider

**Problem:** an external data provider stops answering; recreating the client sometimes helps,
otherwise you want to switch to a secondary provider.

```csharp
.AddCondition("weather-feed-down", c => c
    .ForSource("weather-feed")
    .SustainedFor(TimeSpan.FromSeconds(30))
    .Heal(p => p
        .With<RenewFeedClientAction>()
        .Then<SwitchToBackupProviderAction>()))
```

Make the health check report `Degraded` while serving from the backup. With the default clear rule,
`Degraded` clears a condition that triggers on `Unhealthy`, so the run ends as `Healed`.
Full code: [`samples/Homeostat.Samples.WeatherFeed`](../samples/Homeostat.Samples.WeatherFeed).

---

## Ask before failing over

**Problem:** renewing the client is harmless, but failing over changes the data users see: someone should
agree first.

```csharp
.Heal(p => p
    .With<RenewFeedClientAction>()
    .Then<SwitchToBackupProviderAction>().RequireApproval(TimeSpan.FromMinutes(10)))
```

```csharp
app.MapHomeostatControl("operations");
```

Combine it with the [webhook notifier](notifications.md), whose `ApprovalRequested` event carries the
request id, so the person on call gets the request where they already are. See
[Human in the loop](healing-plans.md#human-in-the-loop-requireapproval).

---

## Serve cached data until the provider is back

**Problem:** while the forecast provider is down, serve the last known forecast, and go back to live data
once the provider has been stable for a while.

```csharp
.AddCondition("forecast-down", c => c
    .ForSource("forecast")
    .SustainedFor(TimeSpan.FromSeconds(30))
    .ClearAfter(TimeSpan.FromMinutes(2))
    .Heal(p => p.TurnOnSwitch("serve-cached-forecast", turnOffWhenCleared: true)))
```

The `forecast` health check must call the provider directly, not through the cached path, otherwise the switch
would make it healthy and the condition would clear at once. See
[switches](actions.md#turnonswitchname-and-ihomeostatswitches).

---

## Do not heal symptoms of a shared failure

**Problem:** when the outbound network fails, every HTTP client fails too, and each condition would start
its own plan.

```csharp
.AddCondition("network-down", c => c
    .ForSource("network")
    .Heal(p => p.With<ResetNetworkClientAction>()))
.AddCondition("forecast-failing", c => c
    .ForSource("http:forecast")
    .SuppressedBy("network-down")
    .Heal(p => p.With("renew-forecast", sp => sp.GetRequiredService<ForecastClient>().Renew())))
.AddCondition("air-quality-failing", c => c
    .ForSource("http:air-quality")
    .SuppressedBy("network-down")
    .Heal(p => p.With("renew-air-quality", sp => sp.GetRequiredService<AirQualityClient>().Renew())))
```

While `network-down` is active, the two client conditions are reported as suppressed and their plans wait.
See [Causes and symptoms](conditions.md#causes-and-symptoms-suppressedby).

---

## Post to a team chat

**Problem:** the team wants to know when Homeostat acts, without reading logs.

```csharp
.AddWebhookNotifications(o =>
{
    o.Url = new Uri(builder.Configuration["Homeostat:ChatWebhookUrl"]!);
    o.CreatePayload = e => new JsonObject { ["text"] = $"[{e.Application}] {e.Summary}" };
})
.AddAuditLog("logs/homeostat-{date}.jsonl")
```

The chat gets activations, outcomes and approval requests; the audit log keeps everything. See
[Notifications and audit](notifications.md).

---

## Drain a growing backlog

**Problem:** a background worker produces thumbnails; when the queue keeps growing, you want to raise
its parallelism, and as a last resort pause non-essential producers.

```csharp
// The worker reports its backlog as a signal.
signals.TryPublish("thumbnail-queue",
    pending > 5_000 ? SignalSeverity.Unhealthy : SignalSeverity.Healthy,
    $"pending={pending}");
```

```csharp
.AddCondition("thumbnail-backlog", c => c
    .ForSource("thumbnail-queue")
    .SustainedFor(TimeSpan.FromMinutes(2))
    .Heal(p => p
        .With<IncreaseWorkerParallelismAction>()
        .Then<PausePreviewGenerationAction>()
        .VerificationTimeout(TimeSpan.FromMinutes(3))))
```

```csharp
public sealed class IncreaseWorkerParallelismAction(ThumbnailWorkerSettings settings) : IHealingAction
{
    public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        settings.MaxParallelism = Math.Min(settings.MaxParallelism * 2, 16);   // (your code)
        return Task.CompletedTask;
    }
}
```

A backlog drains slowly: give verification enough time and remember that the condition clears only
when the queue drops below the threshold.

---

## Reload configuration

**Problem:** a component reads settings that can go stale (a rotated endpoint, a feature flag);
reloading configuration fixes it.

```csharp
.Heal(p => p.ReloadConfiguration())
```

A built-in action: it calls `IConfigurationRoot.Reload()`. Components that read options through
`IOptionsMonitor<T>` see the new values after the reload. See [Actions](actions.md#reloadconfiguration).

---

## Verify with a direct probe

**Problem:** your health checks run every minute, but you want to know within seconds whether the
action worked.

Implement `IHealingVerifier` with a direct check and attach it to the plan with `VerifyWith<T>()`.
See [Custom verifiers](healing-plans.md#custom-verifiers) for a complete example.

---

## Pause during deployments

**Problem:** during a rolling deployment, health checks fail on purpose and you do not want
Homeostat to react.

```csharp
public sealed class DeploymentWindow(IHomeostatControl control)
{
    public void Begin() => control.Pause();    // call from your deployment hook
    public void End()   => control.Resume();
}
```

Or call the control endpoints of [Homeostat.AspNetCore](aspnetcore.md) from your pipeline, most simply with the
[`homeostat` tool](tools.md): `homeostat pause --for 20m` before the deployment, `homeostat resume` after it.

---

## Observe before acting

**Problem:** you are not sure yet how to fix a problem, but you want to know how often it happens.

Declare the condition **without** `Heal(...)`: it is tracked, logged and visible through
`IHomeostatControl`, but it never acts.

```csharp
.AddCondition("air-quality-slow", c => c
    .ForSource("air-quality")
    .WhenSeverityAtLeast(SignalSeverity.Degraded)
    .SustainedFor(TimeSpan.FromMinutes(2)))
```

---

## Unit test an action

Actions are plain classes. With `HealingContextFactory` from
[Homeostat.Testing](testing.md#unit-testing-actions) you can call them directly:

```csharp
[Fact]
public async Task Renew_unsticks_the_client()
{
    var client = new WeatherFeedClient();
    client.SimulateStuckClient();
    var action = new RenewFeedClientAction(client, NullLogger<RenewFeedClientAction>.Instance);   // sample version, which also takes a logger

    await action.ExecuteAsync(HealingContextFactory.Create(), CancellationToken.None);

    Assert.False(client.IsStuck);
}
```

To test the whole loop (condition, plan, verification) use `HomeostatTestHost`: see [Testing](testing.md).
