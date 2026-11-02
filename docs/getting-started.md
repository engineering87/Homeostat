# Getting started

In this tutorial you add a self-healing loop to an ASP.NET Core application in about five minutes.
The scenario: your app reads data from an external weather feed. Sometimes the feed client gets stuck
and needs to be recreated.

**You will:** install the packages → write an action → register a condition → watch the dry-run →
let Homeostat act.

> Prefer to run something first? The complete code is in
> [`samples/Homeostat.Samples.WeatherFeed`](../samples/Homeostat.Samples.WeatherFeed); for a worker service without
> ASP.NET Core, see [`samples/Homeostat.Samples.Worker`](../samples/Homeostat.Samples.Worker). Or start a new project
> with everything set up: `dotnet new install Homeostat.Templates`, then `dotnet new homeostat-webapi` or
> `dotnet new homeostat-worker` (see [Tools](tools.md)).

## 1. Install

```bash
dotnet add package Homeostat --prerelease
dotnet add package Homeostat.HealthChecks --prerelease
```

`--prerelease` is needed while the current version is a release candidate (`1.0.0-rc.1`); drop it once 1.0.0 is
published. See [Versioning](versioning.md). The examples below need `using Homeostat;` (and
`using Homeostat.Healing;` for `IHealingAction`).

## 2. Have a health check

Homeostat needs **signals** to know how your application is doing. The simplest source is a health
check you probably already have:

```csharp
public sealed class WeatherFeedHealthCheck(WeatherFeedClient client) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(client.IsStuck
            ? HealthCheckResult.Unhealthy("Weather feed is not responding.")
            : HealthCheckResult.Healthy());
}
```

```csharp
builder.Services.AddSingleton<WeatherFeedClient>();
builder.Services.AddHealthChecks()
    .AddCheck<WeatherFeedHealthCheck>("weather-feed");   // "weather-feed" becomes the signal source
```

## 3. Write a healing action

An action is what Homeostat does to fix the problem. It is a plain class resolved from dependency
injection, so it can receive any service in its constructor.

```csharp
public sealed class RenewFeedClientAction(WeatherFeedClient client) : IHealingAction
{
    public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        client.Renew();
        return Task.CompletedTask;
    }
}
```

You don't need to register the action yourself: the plan builder does it for you.

## 4. Register a condition and its plan

```csharp
builder.Services
    .AddHomeostat(h => h
        .AddCondition("weather-feed-down", c => c
            .ForSource("weather-feed")
            .SustainedFor(TimeSpan.FromSeconds(30))
            .Heal(p => p.With<RenewFeedClientAction>())))
    .AddHealthCheckSignals(period: TimeSpan.FromSeconds(10));
```

Read it as a sentence:

> *When `weather-feed` has been unhealthy for at least 30 seconds, run `RenewFeedClientAction`,
> then check whether the feed recovered.*

Two details matter here:

- `ForSource("weather-feed")` matches the health check name.
- Health checks run every 10 seconds, more often than the 30-second window. The condition activates
  on the first unhealthy signal that arrives once 30 seconds have passed, so **keep the signal period
  shorter than `SustainedFor`**, or activation comes later than you expect.

## 5. Watch the dry-run

Run the application and make the feed fail. Homeostat starts in **dry-run**: it detects the problem
and logs what it *would* do, without doing it.

```text
warn: Homeostat.Engine.HomeostatEngine
      Condition weather-feed-down is active (source weather-feed, severity Unhealthy).
warn: Homeostat.Engine.HealingRunner
      [dry-run] Condition weather-feed-down: step 1/1 would execute RenewFeedClientAction.
```

This is your chance to check that conditions trigger when you expect, and only then.

## 6. Let it act

When the dry-run decisions look right, turn it off:

```csharp
.AddHomeostat(h => h
    .Configure(o => o.DryRun = false)
    .AddCondition(/* as before */))
```

Now the same fault produces:

```text
warn: Condition weather-feed-down is active (source weather-feed, severity Unhealthy).
warn: Condition weather-feed-down: executing step 1/1 RenewFeedClientAction.
info: Condition weather-feed-down cleared.
info: Condition weather-feed-down: healed by RenewFeedClientAction.
```

Homeostat ran the action, saw the health check return to healthy and confirmed the fix.

## 7. Add an escalation step (optional)

What if renewing the client is not enough, for example because the primary provider is down?
Add a second step that runs only when the first one is verified as **not healed**, or when the first
action fails (throws or times out):

```csharp
.Heal(p => p
    .With<RenewFeedClientAction>()
    .Then<SwitchToBackupProviderAction>())
```

## 8. See what it is doing (optional)

With the `Homeostat.AspNetCore` package (`dotnet add package Homeostat.AspNetCore --prerelease`), one line gives you
a status page:

```csharp
// Who may see Homeostat's state: here, users with the "operations" role.
builder.Services.AddAuthorization(o => o.AddPolicy("operations", p => p.RequireRole("operations")));

var app = builder.Build();
app.UseAuthorization();
app.MapHomeostatStatus("operations");   // GET /homeostat/status: conditions, runs, approvals, budget
```

`"operations"` is the name of an [authorization policy](https://learn.microsoft.com/aspnet/core/security/authorization/policies)
of your application, defined above; the application also needs authentication (JWT bearer, cookies, ...) so that
users have roles. The endpoint reveals internal details, so Homeostat asks for a policy (for a local experiment,
`MapHomeostatStatus(allowAnonymous: true)`). The control endpoints (pause, resume, approvals) and the
dashboard (`MapHomeostatDashboard`, a page with one-click approvals) are described in
[ASP.NET Core endpoints](aspnetcore.md); the `homeostat` command-line tool in [Tools](tools.md).

## Start from a preset (optional)

The steps above build one condition for one dependency. For the problems almost every application can have, a
preset registers the usual detectors and a condition for each in one line: thread pool starvation, memory pressure
and exception rate, plus the server error rate and latency in a web API.

```csharp
builder.Services.AddHomeostat(h => h
    .AddWebApiDefaults()                       // or AddWorkerDefaults() in a worker service
    .AddCondition("weather-feed-down", c => c  /* as before */));
```

Preset conditions only observe: they show up in the logs and the status endpoint, and act only once you give them a
plan, for example `AddWebApiDefaults(d => d.ServerErrors.Heal(p => p.TurnOnSwitch("serve-cached-forecast")))`. The
list of conditions and how to change them is in [Detectors](detectors.md#starter-presets).

## Where to go next

- [Concepts](concepts.md): the mental model behind what you just built.
- [Guardrails](guardrails.md): budgets, cooldowns and the kill switch.
- [Healing plans](healing-plans.md#human-in-the-loop-requireapproval): let a person approve risky steps.
- [Notifications and audit](notifications.md): tell the team, keep a record.
- [Recipes](recipes.md): more scenarios, ready to adapt.
- [Security](security.md): before turning dry-run off in production.
- [FAQ & troubleshooting](faq.md): if something did not happen the way you expected.
