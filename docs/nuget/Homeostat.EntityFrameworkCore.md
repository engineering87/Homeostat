# Homeostat.EntityFrameworkCore

Entity Framework Core integration for Homeostat, for any relational provider. `AddDbContextSignals<TContext>()` turns the failure ratio and p95 duration of the commands of a `DbContext` into a signal (`efcore:{context type name}`), through an interceptor added with EF Core's `ConfigureDbContext`: your `AddDbContext`, `AddDbContextPool` or `AddDbContextFactory` call stays as it is. Timeouts, transient errors and connections that cannot be opened or closed count as failures; errors caused by the command itself (a constraint violation) and canceled commands do not. `AddDbContextProbe<TContext>()` adds a connectivity probe (`efcore:{context type name}:ping`) based on `Database.CanConnectAsync`, which reports even when there is no traffic.

## Install

```bash
dotnet add package Homeostat.EntityFrameworkCore --prerelease
```

This package is a **preview**: it works and is tested, but its API can still change in a minor version (see [Stable and preview packages](https://github.com/engineering87/Homeostat/blob/main/docs/versioning.md#stable-and-preview-packages)), so `--prerelease` stays needed after 1.0.0.

## Example

```csharp
using Homeostat;
using Microsoft.EntityFrameworkCore;

builder.Services.AddDbContext<ReportsDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Reports")));   // any relational provider

builder.Services.AddHomeostat(h => h
    .AddDbContextSignals<ReportsDbContext>(o => o.UnhealthyLatency = TimeSpan.FromSeconds(3))   // source "efcore:ReportsDbContext"
    .AddDbContextProbe<ReportsDbContext>(p => p.DegradedLatency = TimeSpan.FromMilliseconds(200))                           // source "efcore:ReportsDbContext:ping"
    .AddCondition("reports-db-failing", c => c
        .ForSource("efcore:ReportsDbContext")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("reports-read-only"))));

public sealed class ReportsDbContext(DbContextOptions<ReportsDbContext> options) : DbContext(options);
```

Requires EF Core 10. The package is not marked Native AOT compatible.

## Documentation

[Entity Framework Core](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#entity-framework-core)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
