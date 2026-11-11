# Analyzers

The `Homeostat` package includes Roslyn analyzers. They run in every project that references it, in the IDE and in the
build, and catch mistakes that compile but misbehave at runtime. Each rule below shows the code it reports and the fix.

| Id | Severity | Finds |
|---|---|---|
| [HOMEO001](#homeo001) | Warning | An inline action that returns a task nobody awaits |
| [HOMEO002](#homeo002) | Warning | A condition that does not select its signals |
| [HOMEO003](#homeo003) | Warning | A healing action that ignores its cancellation token |
| [HOMEO004](#homeo004) | Info | Endpoints mapped without authorization outside development |
| [HOMEO005](#homeo005) | Warning | `RestartHostedService<T>` on a plain `BackgroundService` |

Change a severity, or turn a rule off, in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.HOMEO004.severity = warning
```

## HOMEO001

**An inline action returns a task that is never awaited.**

```csharp
// FeedClient.RenewAsync returns a ValueTask
.With("renew", sp => sp.GetRequiredService<FeedClient>().RenewAsync())         // HOMEO001
.With("renew", async sp => await sp.GetRequiredService<FeedClient>().RenewAsync())   // fixed
```

A lambda that returns a `ValueTask` or `ValueTask<T>` (or a `Task`-derived type the compiler cannot convert) binds to
the `Action<IServiceProvider>` overload: the step completes as soon as the call returns, verification starts while the
work is still running, and its exceptions are lost. Writing `async sp => await ...` binds to the
`Func<IServiceProvider, Task>` overload instead. The rule checks the lambdas passed to `With`, `Then` and `UndoWith`.

## HOMEO002

**A condition does not select its signals.**

```csharp
.AddCondition("feed-down", c => c
    .ClearWhen(s => s.Severity == SignalSeverity.Healthy)                     // HOMEO002
    .SustainedFor(TimeSpan.FromSeconds(30)))
.AddCondition("feed-down", c => c.ForSource("weather-feed").SustainedFor(TimeSpan.FromSeconds(30)))
```

Without `ForSource`, `Where` or a composite rule (`WhenAllActive`, `WhenAnyActive`), the condition listens to every
source: an unhealthy signal from any component triggers it, so any problem anywhere runs its plan. A condition that has
neither `ForSource` nor `ClearWhen` is also refused when the application starts (a healthy signal from any component
would clear it); the analyzer reports both cases at build time. A lambda that hands the builder to another method
(`c => ConfigureFeed(c)`) or calls an extension method of yours on it (`c => c.Feed()`, with
`static ConditionBuilder Feed(this ConditionBuilder c)`) is not reported, since the rule cannot see inside it.

## HOMEO003

**A healing action ignores its cancellation token.**

```csharp
public async Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    => await client.RenewAsync();                                              // HOMEO003
    => await client.RenewAsync(cancellationToken);                             // fixed
```

The token is canceled at the action timeout and when the application stops. An action that awaits work without it
keeps running after its timeout, holding a concurrency slot and delaying shutdown. If the work really cannot be
canceled, give the parameter a name that starts with `_` (`_` or `_cancellationToken`) to say so. The rule applies to `IHealingAction`
implementations (their `ExecuteAsync`) and to inline actions with a `(context, cancellationToken)` lambda, when the body
awaits something or, written with `=>`, returns the task of a call (`(ctx, ct) => client.RenewAsync()`, or a method
`=> client.RenewAsync();`). A body that returns a task already complete (`=> Task.CompletedTask`,
`=> Task.FromResult(...)`) does synchronous work and is not reported, in a method or in a lambda alike.

## HOMEO004

**Endpoints are mapped with `allowAnonymous: true`.**

```csharp
app.MapHomeostatControl(allowAnonymous: true);                                  // HOMEO004

if (app.Environment.IsDevelopment())
{
    app.MapHomeostatControl(allowAnonymous: true);                              // not reported
}
```

Anonymous status, control, dashboard and alert endpoints let anyone who can reach them see internals, pause Homeostat
or approve steps. The rule is informational. It reports `allowAnonymous: true` written as a constant, and ignores calls
in the `then` branch of an `if (...IsDevelopment())` check (not the `else` branch, not negated or combined
conditions). Raise it to `warning` in `.editorconfig` if anonymous endpoints must never reach production.

## HOMEO005

**`RestartHostedService<T>` targets a plain `BackgroundService`.**

```csharp
public sealed class IngestWorker : BackgroundService { ... }                   // HOMEO005 on RestartHostedService<IngestWorker>
public sealed class IngestWorker : RestartableBackgroundService { ... }        // fixed: put the loop in RunAsync
```

Stopping a `BackgroundService` outside a shutdown makes the generic host treat it as failed and, by default, stop the
application, so Homeostat refuses that restart at runtime. If your host sets
`HostOptions.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore`, the restart is allowed:
suppress the rule for that call (`#pragma warning disable HOMEO005`) or in `.editorconfig`. Otherwise derive the
worker from `RestartableBackgroundService`, which restarts inside the service: see
[Actions](actions.md#restarthostedservicet).

See also: [Security](security.md#authorization-policies), [Healing plans](healing-plans.md).
