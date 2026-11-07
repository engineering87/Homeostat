# Platforms

Where Homeostat runs: the .NET version it targets, the operating systems it is tested on, the minimum versions of the
client libraries its integration packages depend on, and Native AOT support per package.

## .NET

| | Supported |
|---|---|
| Target framework | `net10.0` (.NET 10, long-term support) |
| SDK to build the repository | .NET 10 SDK, 10.0.100 or a later feature band (`global.json`) |
| Application types | ASP.NET Core, worker services and any application on the generic host (`Microsoft.Extensions.Hosting`) |

.NET 8 and .NET 9 are not targeted: they reach end of support on November 10, 2026. New .NET versions are added as
they are released; see [Versioning](versioning.md#net-support).

## Operating systems

Continuous integration builds, tests and runs the Native AOT smoke test on **Linux** (Ubuntu, x64). Homeostat has no
operating-system-specific code paths beyond what .NET itself abstracts, so **Windows and macOS are expected to work**,
but they are not tested in CI: report any problem as an issue.

A few behaviors depend on the platform:

- Signal recording files are created readable and writable only by the application's user on Linux and macOS; on
  Windows the file gets the default permissions of its directory.
- Time zones in configuration (seasonal rules) are resolved with `TimeZoneInfo.FindSystemTimeZoneById`, which accepts
  IANA ids (`Europe/Berlin`) and Windows ids (`W. Europe Standard Time`) on every platform where .NET has ICU time
  zone data; containers built on minimal images may need the `tzdata` package.
- The Kubernetes integration reads the service account files mounted in Linux pods.

## Client libraries

Integration packages reference the client library of their technology. The versions below are the minimum
versions: the ones the packages are built and tested against, taken from
[`Directory.Packages.props`](../Directory.Packages.props). Later versions of the same major version work as usual
with NuGet.

| Package | Depends on | Minimum version |
|---|---|---|
| `Homeostat` | `Microsoft.Extensions.*` (hosting, DI, options, logging, configuration abstractions) | 10.0.14 |
| `Homeostat.HealthChecks` | `Microsoft.Extensions.Diagnostics.HealthChecks` | 10.0.14 |
| `Homeostat.Http`, `Homeostat.Kubernetes`, `Homeostat.Azure`, `Homeostat.Artemis` | `Microsoft.Extensions.Http` | 10.0.14 |
| `Homeostat.AspNetCore` | ASP.NET Core shared framework | .NET 10 |
| `Homeostat.Caching` | `Microsoft.Extensions.Caching.Memory` | 10.0.14 |
| `Homeostat.Polly` | `Polly.Core` | 8.9.0 |
| `Homeostat.Grpc` | `Grpc.Net.ClientFactory` | 2.85.0 |
| `Homeostat.EntityFrameworkCore` | `Microsoft.EntityFrameworkCore.Relational` | 10.0.14 |
| `Homeostat.Redis` | `StackExchange.Redis` | 3.4.31 |
| `Homeostat.MongoDB` | `MongoDB.Driver` | 3.13.0 |
| `Homeostat.ServiceBus` | `Azure.Messaging.ServiceBus` | 7.21.0 |
| `Homeostat.RabbitMQ` | `RabbitMQ.Client` | 7.2.2 |
| `Homeostat.Kafka` | `Confluent.Kafka` | 2.16.0 |
| `Homeostat.FeatureManagement` | `Microsoft.FeatureManagement` | 4.8.0 |
| `Homeostat.OpenFeature` | `OpenFeature` | 2.14.1 |
| `Homeostat.Aspire.Hosting` | `Aspire.Hosting` | 13.6.4 |
| `Homeostat.Testing` | `Microsoft.Extensions.TimeProvider.Testing` | 10.11.0 |
| `Homeostat.Cli` | `System.CommandLine` | 2.0.14 |

`Homeostat.EntityFrameworkCore` references EF Core's relational layer but no database provider: it works with the
relational provider the application already uses (SQL Server, PostgreSQL, MySQL, SQLite, Oracle). It adds its
interceptor with EF Core's `ConfigureDbContext`, which appeared in EF Core 9; the package is built against EF Core 10,
the minimum version above.

Packages without a client library:

- The database support of `Homeostat` references no database provider: bring yours (SqlClient, Npgsql, MySqlConnector,
  Microsoft.Data.Sqlite, Oracle). `AddNpgsqlPoolSignal` reads the metrics of **Npgsql 10 or later**.
- `Homeostat.Artemis` talks to the broker's management API over HTTP; any ActiveMQ Artemis version with Jolokia
  enabled on the web console works.
- `Homeostat.Kubernetes` and `Homeostat.Azure` call the platform REST APIs directly, with no SDK.

## Trimming and Native AOT

| Package | `IsAotCompatible` | Notes |
|---|---|---|
| `Homeostat` | Yes | Covered by the Native AOT smoke test |
| `Homeostat.AspNetCore` | Yes | Endpoints, dashboard, Slack and Teams endpoints covered by the smoke test |
| `Homeostat.HealthChecks` | Yes | |
| `Homeostat.Http` | Yes | Webhook, Slack, Teams, PagerDuty and Opsgenie notifiers covered by the smoke test |
| `Homeostat.Caching`, `Homeostat.Kubernetes`, `Homeostat.Azure` | Yes | Compiled and analyzed in the smoke test |
| `Homeostat.Grpc`, `Homeostat.Polly`, `Homeostat.Redis`, `Homeostat.MongoDB`, `Homeostat.ServiceBus`, `Homeostat.RabbitMQ`, `Homeostat.Kafka`, `Homeostat.Artemis`, `Homeostat.FeatureManagement`, `Homeostat.OpenFeature` | Yes | Homeostat's code is analyzed; whether the application publishes with Native AOT also depends on the client library |
| `Homeostat.EntityFrameworkCore` | No | EF Core's own Native AOT support is limited (compiled models, precompiled queries, still experimental) |
| `Homeostat.Testing` | Yes | Meant for test projects |
| `Homeostat.Aspire.Hosting` | No | Runs in the Aspire app host, which is not trimmed |
| `Homeostat.Cli` | No | A .NET tool |
| `Homeostat.Templates` | No | Project templates, no code |
| Analyzers | No | Bundled in `Homeostat`, run in the compiler |

See [Trimming and Native AOT](native-aot.md) for what is checked and what to watch for.
