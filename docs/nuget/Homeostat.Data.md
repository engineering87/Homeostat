# Homeostat.Data

Database integration for Homeostat, for any ADO.NET provider (SQL Server, PostgreSQL, MySQL, SQLite, Oracle): connectivity and latency probes with transient error tagging, connection pool exhaustion signals for SqlClient (event counters) and Npgsql 10 or later (metrics), and an action that clears the provider's connection pools after a failover. It references no database provider. For command-level signals of an Entity Framework Core `DbContext`, see [Homeostat.EntityFrameworkCore](https://www.nuget.org/packages/Homeostat.EntityFrameworkCore).

## Install

```bash
dotnet add package Homeostat.Data --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;
using Microsoft.Data.SqlClient;

var connectionString = builder.Configuration.GetConnectionString("Reports");

builder.Services.AddHomeostat(h => h
    .AddDatabaseProbe("reports-db", _ => new SqlConnection(connectionString), p =>
    {
        p.DegradedAbove = 200;      // ms to open a connection and run SELECT 1
        p.UnhealthyAbove = 2_000;
    })
    .AddSqlClientPoolSignal(o => o.MaxPoolSize = 100)   // source "sqlclient:pool"
    .AddCondition("reports-db-stale", c => c
        .ForSource("reports-db")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.ClearConnectionPools<SqlConnection>())));
```

## Documentation

[Databases](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#databases)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
