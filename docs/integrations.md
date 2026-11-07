# Integrations

Homeostat heals what it can see. This page is for developers who want to bring the usual weak points of a .NET
application into view (databases, MongoDB, Redis, gRPC services, caches, external monitoring) and use the ready-made
actions that act on them (pool resets, cache purges, circuit isolation, feature flags).

| Section | What it covers | Package | Stability |
|---|---|---|---|
| [Probes](#probes-any-number-as-a-signal) | Any number or round trip, polled periodically: `AddProbe`, `AddLatencyProbe` | `Homeostat` | Stable |
| [Databases](#databases) | ADO.NET connectivity, latency and transient errors (`AddDatabaseProbe`), pool reset (`ClearConnectionPools<T>`), pool exhaustion (`AddSqlClientPoolSignal`, `AddNpgsqlPoolSignal`, `AddEventCounterSignal`) | `Homeostat` | Stable |
| [Entity Framework Core](#entity-framework-core) | Command failures and latency of a `DbContext`, connectivity: `AddDbContextSignals<T>`, `AddDbContextProbe<T>` | `Homeostat.EntityFrameworkCore` | Preview |
| [MongoDB](#mongodb) | Ping, primary availability, pool exhaustion, replication lag: `AddMongoSignals`, `AddHomeostatSignals` on the client settings | `Homeostat.MongoDB` | Stable |
| [Redis](#redis) | Client health: `AddRedisSignals` | `Homeostat.Redis` | Stable |
| [gRPC clients](#grpc-clients) | Failures and latency judged on the gRPC status: `AddHomeostatGrpcSignals`, `HomeostatGrpcInterceptor` | `Homeostat.Grpc` | Preview |
| [Message brokers](#message-brokers) | Backlog, dead letters, lag; pausing consumers (full page: [Message brokers](messaging.md)) | `Homeostat.ServiceBus`, `Homeostat.RabbitMQ`, `Homeostat.Kafka`, `Homeostat.Artemis` | Stable |
| [Caches](#caches) | Memory cache hit ratio; purge and compaction: `AddMemoryCacheSignals`, `PurgeHybridCacheTags`, `RemoveHybridCacheKeys`, `RemoveDistributedCacheKeys`, `CompactMemoryCache` | `Homeostat.Caching` | Preview |
| [Alerts from your monitoring](#alerts-from-your-monitoring) | Prometheus Alertmanager, Grafana and Azure Monitor alerts as signals: `MapHomeostatAlerts` | `Homeostat.AspNetCore` | Stable |
| [Feature flags](#feature-flags) | Switches as feature flags: `AddHomeostatSwitchFilter`, `HomeostatSwitchProvider` | `Homeostat.FeatureManagement`, `Homeostat.OpenFeature` | Preview |
| [Polly](#polly-circuit-isolation-and-game-days) | Circuit isolation and game days: `CircuitControl`, `IsolateCircuit`, `ChaosEnabledBySwitch` | `Homeostat.Polly` | Stable |
| [.NET Aspire](#net-aspire) | Dashboard link and Pause/Resume commands in the app host: `WithHomeostat` | `Homeostat.Aspire.Hosting` | Preview |

For a client that none of these packages covers (an SDK, a TCP protocol), record its calls with
[`AddCallSignals` and `CallRecorder`](detectors.md#signals-from-any-client-call-signals).

Every section below follows the same order: install, what it measures or does, an example, the options, then notes.
All snippets assume `builder.Services.AddHomeostat(h => ...)`. Conditions select signals with `ForSource` (see
[Conditions](conditions.md)). By default a condition triggers on `Unhealthy` signals: add
`.WhenSeverityAtLeast(SignalSeverity.Degraded)` to act on `Degraded` ones too.

Install each package with `dotnet add package <Package> --prerelease` while the current version is a release
candidate. Preview packages carry a `-preview` suffix even on stable releases, so they always need `--prerelease`
(see [Stable and preview packages](versioning.md#stable-and-preview-packages)). The [package index](packages.md) lists
every package.

## Probes: any number as a signal

Probes are part of the core package `Homeostat`: nothing else to install.

A probe is a function that Homeostat calls periodically, in its own service scope, with a timeout. The result
becomes the `Value` of a signal, classified by thresholds. An exception, a timeout or a value that is not a finite
number publishes `Unhealthy` (configurable with `FailureSeverity`) without a value. `AddLatencyProbe` measures how long
a call takes and publishes the round trip in milliseconds.

```csharp
h.AddProbe("ingest-backlog",
    async (sp, ct) => await sp.GetRequiredService<IngestQueue>().CountAsync(ct),
    p =>
    {
        p.Interval = TimeSpan.FromSeconds(30);   // default: every DetectionInterval (10 s)
        p.Timeout = TimeSpan.FromSeconds(5);     // default: 5 s
        p.DegradedAbove = 1_000;
        p.UnhealthyAbove = 10_000;
    });

h.AddLatencyProbe("search-index",
    (sp, ct) => sp.GetRequiredService<SearchClient>().PingAsync(ct),
    p => p.DegradedLatency = TimeSpan.FromMilliseconds(250));
```

Without thresholds every successful call is `Healthy`, and conditions use the value directly (`WhenValueAbove`,
`WhenRisingFasterThan`, `WhenAnomalouslyHigh`):

```csharp
// Backlog that keeps growing, even if it is still below any fixed threshold.
// IngestWorker derives from RestartableBackgroundService, so it can be restarted safely (see Actions).
h.AddCondition("backlog-growing", c => c
    .ForSource("ingest-backlog")
    .WhenRisingFasterThan(perMinute: 500, window: TimeSpan.FromMinutes(5))
    .Heal(p => p.RestartHostedService<IngestWorker>()));
```

| Option | Default | Meaning |
|---|---|---|
| `Interval` | every detection interval | How often the probe runs |
| `Timeout` | 5 s | Longest call; then `outcome=timeout` |
| `DegradedAbove`, `UnhealthyAbove` | none | Upper thresholds: a value strictly above reaches them |
| `DegradedBelow`, `UnhealthyBelow` | none | Lower thresholds (free disk space, consumers online): a value strictly below reaches them |
| `DegradedLatency`, `UnhealthyLatency` | none | Latency probes only (`AddLatencyProbe`, database, Redis and MongoDB pings): the upper thresholds as durations |
| `FailureSeverity` | `Unhealthy` | Severity when the probe throws, times out or returns a value that is not finite |
| `Origin` | `probe`, `latency-probe` | Value of the `origin` tag; integration packages set their own (`database-probe`, `redis-ping`, ...) |

Probe and Kafka thresholds compare **strictly**: with `UnhealthyAbove = 10_000`, a backlog of exactly 10 000 is not
`Unhealthy`. [Metric signals](detectors.md#metrics) and event counter signals, and the signals built on them (the
server error rate and latency signals, the SqlClient and Npgsql pool signals), compare **inclusively**: a value at or
above `UnhealthyAbove`, or at or below `UnhealthyBelow`, reaches it.

Notes:

- Probes run in the background: the detection loop only starts the due ones, so a slow probe delays neither the
  other probes nor the other detectors. A probe still within its timeout is not started again.
- A call that ignores cancellation is abandoned at the timeout, and the probe is not called again until that call
  ends (it publishes `outcome=still-running` meanwhile), so a hanging dependency never piles up connections.
- Signals carry the tags `origin` (`probe`, `latency-probe`, `database-probe`, `redis-ping`, `servicebus`, `rabbitmq`,
  `artemis`, `mongodb`), `outcome` (`ok`, `failed`, `timeout`, `still-running`) and, on failure, `error` (the exception
  type) and, for a `DbException`, `transient`. Failure messages of a `DbException` keep only the exception type
  (provider messages can name servers and users); the details go to the application log.
- A failed call publishes no value, so it neither triggers nor clears a value rule (`WhenValueAbove`,
  `WhenValueBelow`). Use a severity rule, or `FailureSeverity`, to act on failures.

## Databases

The database support is in the core package `Homeostat` (namespace `Homeostat.Data` for the option types): nothing
else to install. It works with any ADO.NET provider (SqlClient, Npgsql, MySqlConnector, Microsoft.Data.Sqlite, Oracle)
and references none of them: you bring the one you already use. Applications that reach the database through Entity
Framework Core also get command-level signals from [`Homeostat.EntityFrameworkCore`](#entity-framework-core).

### Connectivity and latency

`AddDatabaseProbe` opens a connection and runs a query at every interval, and publishes the round trip in
milliseconds.

```csharp
h.AddDatabaseProbe("reports-db",
    _ => new SqlConnection(builder.Configuration.GetConnectionString("Reports")),
    p =>
    {
        p.DegradedLatency = TimeSpan.FromMilliseconds(200);   // to open (from the pool) and run SELECT 1
        p.UnhealthyLatency = TimeSpan.FromSeconds(2);
        // p.CommandText = "SELECT 1 FROM DUAL";   // Oracle; null only opens the connection
    });
```

| `DatabaseProbeOptions` | Default | Meaning |
|---|---|---|
| `CommandText` | `SELECT 1` | Query run after opening; `null` only opens the connection |
| Other options | as for [probes](#probes-any-number-as-a-signal) | `Interval`, `Timeout` (also the command timeout, rounded up to whole seconds), `DegradedLatency` and `UnhealthyLatency` (or the same thresholds in milliseconds), `FailureSeverity`, `Origin` (`database-probe`) |

The factory must return a **new**, closed connection: the probe opens and disposes it. Never return a connection
owned by something else, such as the one of an Entity Framework Core `DbContext`.

### Transient errors

Failures carry `transient=true` when the provider says the error is transient (`DbException.IsTransient`):
throttling, failover in progress, a dropped connection. SQL Server's client (`Microsoft.Data.SqlClient`) does not set
`IsTransient`, so with it the tag is always `false`; the probe's failures still count.

Do **not** filter transient failures out: providers also mark "server unreachable" and timeouts as transient, so a
filter would hide a full outage. Tell a blip from an outage with time instead, and use the tag in logs, the audit
trail and notifications:

```csharp
h.AddCondition("reports-db-down", c => c
    .ForSource("reports-db")
    .SustainedFor(TimeSpan.FromMinutes(1))          // a blip clears before this
    .Heal(p => p.TurnOnSwitch("reports-read-only")));

h.AddCondition("reports-db-flapping", c => c
    .ForSource("reports-db")
    .Occurrences(5, within: TimeSpan.FromMinutes(10))   // intermittent failures
    .Heal(p => p.ClearConnectionPools<SqlConnection>()));
```

### Pool reset

After a failover or a network change, pooled connections can point to a server that no longer answers, and every
request pays a timeout before the pool notices. `ClearConnectionPools<TConnection>()` empties the provider's pools
through its static `ClearAllPools()` (or `ClearAllPoolsAsync()`) method, and new connections are opened on demand:

```csharp
h.AddCondition("reports-db-stale", c => c
    .ForSource("reports-db")
    .SustainedFor(TimeSpan.FromSeconds(30))
    .Heal(p => p
        .ClearConnectionPools<SqlConnection>()                       // cheap, try first
        .Then("switch to read replica", sp => sp.GetRequiredService<ReportsRouting>().UseReplica())
        .RequireApproval()));
```

SqlClient, Npgsql, MySqlConnector and Microsoft.Data.Sqlite have such a method. For a connection type without one,
building the plan throws `InvalidOperationException`.

### Pool exhaustion

A pool that stays full makes every request wait for a connection, then fail with a timeout, often before the database
itself is in trouble. `Microsoft.Data.SqlClient` (3.0 or later) reports its pool through **event counters**;
`AddSqlClientPoolSignal` reads the connections in use (`number-of-active-connections`) as a share of the pool size:

```csharp
h.AddSqlClientPoolSignal(o => o.MaxPoolSize = 100)   // sqlclient:pool: connections in use / Max Pool Size
 .AddCondition("sql-pool-full", c => c
     .ForSource("sqlclient:pool")
     .WhenSeverityAtLeast(SignalSeverity.Degraded)
     .SustainedFor(TimeSpan.FromMinutes(1))
     .Heal(p => p.PauseConsumer("ingest")));         // shed load: stop taking work that needs the database
```

Npgsql publishes its pool through **metrics** (Npgsql 10 or later). `AddNpgsqlPoolSignal` reads the connections in
use (`db.client.connection.count` with state `used`, meter `Npgsql`) as a share of the pool size. Neither signal needs
a reference to the provider:

```csharp
h.AddNpgsqlPoolSignal(o => { o.MaxPoolSize = 50; o.DataSourceName = "reports"; })   // npgsql:pool; Degraded ≥ 0.8, Unhealthy ≥ 0.95
 .AddCondition("reports-pool-full", c => c
     .ForSource("npgsql:pool")
     .WhenSeverityAtLeast(SignalSeverity.Degraded)
     .SustainedFor(TimeSpan.FromMinutes(1))
     .Heal(p => p.PauseConsumer("ingest")));
```

| `SqlClientPoolSignalOptions`, `NpgsqlPoolSignalOptions` | Default | Meaning |
|---|---|---|
| `MaxPoolSize` | 100 | `Maximum Pool Size` of the connection string (at least 1) |
| `Source` | `sqlclient:pool`, `npgsql:pool` | Signal source |
| `DegradedAbove`, `UnhealthyAbove` | 0.8, 0.95 | Thresholds on the share in use; a share at or above a threshold reaches it |
| `DataSourceName` (Npgsql only) | all pools | Name of the data source (`NpgsqlDataSourceBuilder.Name`, by default the connection string without the password); `null` adds up every Npgsql pool of the process: then set `MaxPoolSize` to the sum of their sizes |

Notes:

- Clearing the pools does not help here: it does not close connections in use. Shed load instead (pause consumers,
  turn on a degraded mode), and look for the cause: slow queries holding connections, or connections not disposed
  (`number-of-reclaimed-connections`).
- The SqlClient counter covers every SqlClient pool of the process: with several connection strings, set
  `MaxPoolSize` to the sum of their pool sizes.
- Any other event counter becomes a signal with `AddEventCounterSignal(eventSource, counter, o => ...)`, with a
  `Transform` and thresholds (see [Event counters](detectors.md#event-counters)); only the counters are enabled, not
  the library's tracing events.
- Providers that publish other pool metrics can be watched with [metric signals](detectors.md#metrics), using
  `Scale(1.0 / poolSize)` to turn a count into a share.

## Entity Framework Core

```bash
dotnet add package Homeostat.EntityFrameworkCore --prerelease
```

`Homeostat.EntityFrameworkCore` is a preview package. It watches what a `DbContext` actually does: the failure ratio
and the p95 duration of its commands, as the signal `efcore:{context type name}`. It works with any relational
provider (SQL Server, PostgreSQL, MySQL, SQLite, Oracle). `AddDbContextProbe<TContext>()` adds a connectivity probe
that does not depend on traffic.

```csharp
builder.Services.AddDbContext<ReportsDbContext>(o => o.UseNpgsql(connectionString));   // unchanged

builder.Services.AddHomeostat(h => h
    .AddDbContextSignals<ReportsDbContext>(o => o.UnhealthyLatency = TimeSpan.FromSeconds(3))
    .AddDbContextProbe<ReportsDbContext>(p => p.DegradedLatency = TimeSpan.FromMilliseconds(200))   // efcore:ReportsDbContext:ping
    .AddCondition("reports-db-failing", c => c
        .ForSource("efcore:ReportsDbContext")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("reports-read-only"))));
```

`AddDbContextSignals<TContext>()` adds an interceptor through EF Core's `ConfigureDbContext`, so the registration of
the context (`AddDbContext`, `AddDbContextPool` or `AddDbContextFactory`) stays as it is. What counts as a failure:

| Outcome | Recorded as |
|---|---|
| Command executed (query, scalar, non-query) | Success, with its duration |
| Command failed with a `TimeoutException` or a `DbException` that the provider marks as transient (`IsTransient`), also as an inner exception; for SQL Server, which does not set `IsTransient`, a timeout (error -2) or an error EF Core's retrying strategy treats as transient (deadlock, throttling, failover) | Failure |
| Command failed because of the command itself (a constraint violation, a syntax error) | Success: the database answered |
| Connection that cannot be opened (for example, an exhausted pool or an unreachable server) or closed | Always a failure |
| Command or connection canceled | Not recorded |

| `DbContextSignalOptions` | Default | Meaning |
|---|---|---|
| `Source` | `efcore:{context type name}` | Signal source |
| `IsFailure` | `DbContextSignalOptions.IsDatabaseFailure` | Which exceptions of failed commands count as failures of the database; cannot be `null` |
| `DegradedFailureRatio`, `UnhealthyFailureRatio` | 0.1, 0.5 | Failure ratio from which the signal is Degraded, Unhealthy |
| `DegradedLatency`, `UnhealthyLatency` | none | p95 command duration from which the signal is Degraded, Unhealthy |
| `MinimumCalls` | 10 | Commands an interval needs before its failure ratio is judged (as for [call signals](detectors.md#signals-from-any-client-call-signals)) |

| `DbContextProbeOptions` | Default | Meaning |
|---|---|---|
| `Source` | `efcore:{context type name}:ping` | Signal source |
| Other options | as for [probes](#probes-any-number-as-a-signal) | `Interval`, `Timeout`, `DegradedLatency`, `UnhealthyLatency`, `FailureSeverity`, `Origin` |

Notes:

- The signal's value is the failure ratio; tags `calls`, `failures`, `failure_ratio`, `p95_ms`; `origin` = `efcore`.
- Calling `AddDbContextSignals<TContext>()` again for the same context updates its settings without adding a second
  interceptor.
- To count more errors, combine the default with your own rule, for example lock timeouts with SqlClient:
  `o.IsFailure = ex => DbContextSignalOptions.IsDatabaseFailure(ex) || ex is SqlException { Number: 1222 }`. Deadlocks
  (1205) are already in the default.
- Command signals depend on traffic: when the application sends no commands, no signal is published, and a plan that
  waits for recovery may end `Inconclusive` in a quiet period. The probe covers that: at every probe interval it calls
  `Database.CanConnectAsync` on a new context, in its own scope, and publishes the round trip in milliseconds,
  `Unhealthy` when the context cannot connect. The context can be registered with `AddDbContext`, `AddDbContextPool` or
  `AddDbContextFactory` (the probe then creates a context from the factory and disposes it). Use the probe as the
  condition's source when the plan needs verification even without traffic, and the command signals when what matters
  is what users see.
- The package targets EF Core 10 (`Microsoft.EntityFrameworkCore.Relational` 10.0.14 or later); `ConfigureDbContext`
  appeared in EF Core 9.
- `ClearConnectionPools<SqlConnection>()` (core package, with the connection type of your provider) works for EF Core
  contexts too: they use the provider's pool.
- For code that uses ADO.NET directly, without a `DbContext`, use [`AddDatabaseProbe`](#connectivity-and-latency).

## MongoDB

```bash
dotnet add package Homeostat.MongoDB --prerelease
```

`Homeostat.MongoDB` watches a deployment through the driver (MongoDB.Driver 3): a ping, and the driver's own events,
which report at once when the primary is lost or the connection pool runs dry. The replication lag is opt-in.

```csharp
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var settings = MongoClientSettings.FromConnectionString(builder.Configuration.GetConnectionString("archive"));
    settings.AddHomeostatSignals(sp, "archive");       // driver events: primary and pool
    return new MongoClient(settings);
});

builder.Services.AddHomeostat(h => h
    .AddMongoSignals("archive", o =>
    {
        o.Ping.DegradedLatency = TimeSpan.FromMilliseconds(100);
        o.ReplicationLag = true;                       // needs the clusterMonitor role
        o.Lag.UnhealthyAbove = 30;                     // seconds
    })
    .AddCondition("archive-no-primary", c => c
        .ForSource("mongodb:archive:primary")
        .SustainedFor(TimeSpan.FromSeconds(30))        // an election takes a few seconds: do not act on it
        .Heal(p => p.PauseConsumer("ingest"))));
```

| Signal | Value | Unhealthy when |
|---|---|---|
| `mongodb:{name}:ping` (`{name}` is `default` unless given) | round trip of `ping`, ms | the ping fails or times out (thresholds with `o.Ping`) |
| `mongodb:{name}:primary` | servers connected | no writable server (primary, mongos, standalone) is connected; published at once when it changes, then every `Interval` |
| `mongodb:{name}:pool` | connection checkouts that timed out in the `Interval` (30 seconds by default) | more than `PoolTimeoutsUnhealthyAbove` (0: any) |
| `mongodb:{name}:replication-lag` | seconds between the freshest data member and the furthest secondary, recovering or syncing member | above `o.Lag` thresholds (opt-in) |

| `MongoSignalOptions` | Default | Meaning |
|---|---|---|
| `Client` | the registered `IMongoClient` | Returns the client the probes use |
| `Source` | `mongodb:{name}` | Prefix of the `:ping`, `:primary`, `:pool` and `:replication-lag` sources |
| `Interval` | 30 seconds | How often the probes run and the event-based signals are published; an `Interval` set on `Ping` or `Lag` wins over it for that probe |
| `Ping` | no thresholds | Settings of the ping (`DegradedLatency`, `UnhealthyLatency`, `Timeout`, ...) |
| `PoolTimeoutsUnhealthyAbove` | 0 | Checkout timeouts per interval above which `pool` is Unhealthy |
| `ReplicationLag` | `false` | Publishes `replication-lag` (needs `replSetGetStatus`, for example the `clusterMonitor` role) |
| `Lag` | no thresholds | Thresholds of the replication lag, in seconds |
| `LagExcludedMembers` | empty | Members (`host:port`) left out of the lag, such as delayed or hidden ones |

Notes:

- `primary` and `pool` come from the driver events, so the client must be created with `AddHomeostatSignals` on its
  settings (it keeps any `ClusterConfigurator` you set), with the same name as `AddMongoSignals`; without a matching
  `AddMongoSignals` it throws. Without it, only the probes are published. Nothing is published before the driver has
  heard from a server, so the start-up of the application is not reported as a lost primary.
- Register the client as a **singleton**, as MongoDB recommends. Give each deployment its own name: two clients of
  different deployments under one name would mix their states (the worst one wins). The probes use the `IMongoClient`
  registered in the services; with several clients, select one with `o.Client = sp => ...` (for example a keyed
  service).
- Replication lag: a single-node replica set reports 0; during an election the lag is measured against the freshest
  member; members that fell behind (`RECOVERING`) or are syncing (`STARTUP2`) count, since that is when the lag
  matters. Leave out delayed or hidden members, which lag on purpose, with `o.LagExcludedMembers.Add("db-3:27017")`.
- The driver fails over by itself: the useful healing is to **degrade** while there is no primary (pause consumers,
  serve reads from secondaries or a cache) and to shed load when the pool is exhausted.

## Redis

```bash
dotnet add package Homeostat.Redis --prerelease
```

`AddRedisSignals` watches the `IConnectionMultiplexer` registered in the container (the one used by `UseRedis`, or
your caching one): a periodic `PING`, whose round trip in milliseconds is the value, plus an immediate signal when
the client loses or restores a connection.

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect("redis:6379,abortConnect=false"));

builder.Services.AddHomeostat(h => h
    .AddRedisSignals(o => { o.Source = "session-cache"; o.DegradedLatency = TimeSpan.FromMilliseconds(50); })
    .AddCondition("session-cache-down", c => c
        .ForSource("session-cache")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .ClearAfter(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("sessions-in-memory"))));
```

| `RedisSignalOptions` | Default | Meaning |
|---|---|---|
| `Source` | `redis` | Signal source |
| `PublishConnectionEvents` | `true` | Publishes a signal as soon as the client reports a lost or restored connection; `false` relies on the periodic ping alone |
| Other options | as for [probes](#probes-any-number-as-a-signal) | `DegradedLatency` and `UnhealthyLatency` of the `PING` round trip, `Interval`, `Timeout`, `FailureSeverity`, `Origin` (`redis-ping`) |

Notes:

- StackExchange.Redis reconnects by itself, so Homeostat does not tear the multiplexer down: the useful healing is to
  **degrade** (serve from memory, skip a non-essential cache) until the connection is back.
- Use `abortConnect=false` so the application starts even when Redis does not, and the probe reports it
  (`UseRedis(connectionString)` sets it for you). A client that is not connected publishes `FailureSeverity`.
- Connection events are subscribed the first time the probe reaches the client, so they work even when Redis was down
  at startup. Their signals carry `origin=redis-connection` and `outcome` `connection-failed` or `connection-restored`;
  a restored connection publishes `Healthy` only when the client reports itself connected, `Degraded` while other
  endpoints are still down.
- The Redis store and coordinator (`UseRedis`) are described in [Persistence and clusters](persistence-and-clusters.md).

## gRPC clients

```bash
dotnet add package Homeostat.Grpc --prerelease
```

`Homeostat.Grpc` is a preview package. It turns the calls of a client from the gRPC client factory
(`Grpc.Net.ClientFactory`) into a signal: the failure ratio and the p95 duration of its calls, judged on the **gRPC
status** of each call. The source is `grpc:{client name}`. The client factory names a client after its type unless you
give it a name, so `AddGrpcClient<Forecasts.ForecastsClient>()` publishes `grpc:ForecastsClient`.

```csharp
builder.Services.AddGrpcClient<Forecasts.ForecastsClient>(o => o.Address = new Uri("https://forecasts:5001"))
    .AddHomeostatGrpcSignals(o => o.UnhealthyLatency = TimeSpan.FromSeconds(2));

builder.Services.AddHomeostat(h => h
    .AddCondition("forecasts-failing", c => c
        .ForSource("grpc:ForecastsClient")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("serve-cached-forecast"))));
```

For a channel built without the client factory, add the interceptor yourself, with a recorder registered by
[`AddCallSignals`](detectors.md#signals-from-any-client-call-signals). The interceptor takes an optional `isFailure`
rule (by default `IsServiceFailure`) and a `TimeProvider`.

```csharp
using Grpc.Core.Interceptors;   // Intercept
using Homeostat.Calls;          // CallRecorder
using Homeostat.Grpc;           // HomeostatGrpcInterceptor

builder.Services.AddHomeostat(h => h.AddCallSignals("grpc:forecasts"));
builder.Services.AddSingleton(sp =>
{
    var recorder = sp.GetRequiredKeyedService<CallRecorder>("grpc:forecasts");
    var channel = GrpcChannel.ForAddress("https://forecasts:5001");
    return new Forecasts.ForecastsClient(channel.Intercept(new HomeostatGrpcInterceptor(recorder)));
});
```

| `GrpcSignalOptions` | Default | Meaning |
|---|---|---|
| `Source` | `grpc:{client name}` | Signal source |
| `IsFailure` | `GrpcSignalOptions.IsServiceFailure` | Which status codes count as failures; cannot be `null` |
| `DegradedFailureRatio`, `UnhealthyFailureRatio` | 0.1, 0.5 | Failure ratio from which the signal is Degraded, Unhealthy |
| `DegradedLatency`, `UnhealthyLatency` | none | p95 duration of the unary calls from which the signal is Degraded, Unhealthy |
| `MinimumCalls` | 10 | Calls an interval needs before its failure ratio is judged (as for [call signals](detectors.md#signals-from-any-client-call-signals)) |

`IsServiceFailure` counts `Unknown`, `DeadlineExceeded`, `ResourceExhausted`, `Internal`, `Unavailable` and
`DataLoss`: the statuses that point at the service or the network. Errors in the request (`NotFound`,
`InvalidArgument`, `PermissionDenied`...) and calls canceled by the caller do not count. To change the rule:
`o.IsFailure = s => GrpcSignalOptions.IsServiceFailure(s) || s == StatusCode.Aborted`.

Notes:

- `AddHomeostatGrpcSignals` needs `AddHomeostat`, and a named or typed client.
- A unary call is recorded with its duration when its response arrives. Only unary calls feed the p95 and the
  latency thresholds.
- Streaming calls count for the failure ratio but have no duration, since a stream lasts as long as the caller keeps
  it open. A client streaming call is recorded when its response arrives; a server or duplex streaming call when its
  response stream ends or fails, with the final status from the trailers, or as soon as it cannot start (the server is
  unreachable), even if nobody reads the stream. A streaming call abandoned before its end is not recorded.
- Calls canceled by the caller (`Cancelled`) are not recorded.
- Calling `AddHomeostatGrpcSignals()` again on the same client updates its settings without adding a second
  interceptor.
- The signal's value is the failure ratio; tags `calls`, `failures`, `failure_ratio`, `p95_ms`; `origin` =
  `grpc-client`.
- Compared with `Homeostat.Http`: gRPC clients of the client factory also go through `IHttpClientFactory`, so the
  HTTP-level `AddHomeostatSignals` of `Homeostat.Http` works on them, with `HttpClientSignalOptions.IsGrpcFailure`
  reading the `grpc-status` header (see [HTTP clients](detectors.md#grpc-clients)). That handler sees only the
  response headers: a streaming call that fails after sending messages is missed, and its latency is the time to the
  headers. Prefer `AddHomeostatGrpcSignals`, and do not use both on the same client.

## Message brokers

Azure Service Bus, RabbitMQ, Kafka and ActiveMQ Artemis have their own packages (`Homeostat.ServiceBus`,
`Homeostat.RabbitMQ`, `Homeostat.Kafka`, `Homeostat.Artemis`): backlog, consumers, dead letters and lag as signals, and
consumers that plans can pause and resume. Installation, options and examples are on the
[Message brokers](messaging.md) page.

For other brokers, a probe on their client is enough:

```csharp
h.AddProbe("imports-backlog", async (sp, ct) => await sp.GetRequiredService<ImportQueue>().CountAsync(ct),
    p => { p.Interval = TimeSpan.FromSeconds(30); p.UnhealthyAbove = 5_000; });
```

## Caches

```bash
dotnet add package Homeostat.Caching --prerelease
```

`Homeostat.Caching` is a preview package. It publishes the hit ratio of a memory cache, and adds steps that purge
`HybridCache` and `IDistributedCache` entries and compact a `MemoryCache`.

### Hit ratio

`AddMemoryCacheSignals` publishes, at every detection interval, the share of lookups that hit the cache. The cache
must track statistics.

```csharp
builder.Services.AddMemoryCache(o => o.TrackStatistics = true);   // required
h.AddMemoryCacheSignals(o =>
{
    o.Source = "lookup-cache";
    o.DegradedBelow = 0.6;      // hit ratio over the last detection interval
    o.UnhealthyBelow = 0.2;
    o.MinimumLookups = 20;      // no signal for quieter intervals (the default)
});
```

| `MemoryCacheSignalOptions` | Default | Meaning |
|---|---|---|
| `Source` | `cache:memory` | Signal source |
| `Cache` | the registered `IMemoryCache` | Selects another cache: `o.Cache = sp => sp.GetRequiredService<LookupCache>().Inner` |
| `DegradedBelow`, `UnhealthyBelow` | none | Hit ratio (0 to 1) strictly below which the signal is `Degraded` or `Unhealthy`; without thresholds every interval is `Healthy` and the value carries the ratio |
| `MinimumLookups` | 20 | Hits plus misses needed in an interval to publish the ratio (at least 1) |

Notes:

- The first interval is a baseline, and so is the interval after the counters go back (a recreated cache). A cache
  without statistics publishes nothing and logs a warning once.
- Tags: `hits`, `misses`, `entries` and, when the cache has a size limit, `estimatedSize`.
- A falling hit ratio often explains a latency spike on the dependency behind the cache, so it is a good
  `SuppressedBy` cause or a composite input.

### Purge and compaction

```csharp
.Heal(p => p.PurgeHybridCacheTags("reference-data"))                 // HybridCache.RemoveByTagAsync
.Heal(p => p.RemoveHybridCacheKeys("regions", "units"))             // HybridCache.RemoveAsync
.Heal(p => p.RemoveDistributedCacheKeys("reference:regions"))       // IDistributedCache.RemoveAsync
.Heal(p => p.CompactMemoryCache(0.5))                                // MemoryCache.Compact: evicts half the entries
```

`CompactMemoryCache` pairs well with the memory pressure detector:

```csharp
h.AddMemoryPressureDetector()
 .AddCondition("memory-high", c => c
     .ForSource("memory")
     .SustainedFor(TimeSpan.FromMinutes(2))
     .Heal(p => p.CompactMemoryCache(0.25).Then("compact more", sp => (sp.GetRequiredService<IMemoryCache>() as MemoryCache)?.Compact(0.5))));
```

| Step | Default | Meaning |
|---|---|---|
| `PurgeHybridCacheTags(tags)`, `RemoveHybridCacheKeys(keys)` | none | Tags or keys to remove from the registered `HybridCache`; at least one, none empty |
| `RemoveDistributedCacheKeys(keys)` | none | Keys to remove from the registered `IDistributedCache` |
| `CompactMemoryCache(percentage)` | 0.25 | Share of entries to evict (greater than 0, at most 1), lowest priority and least recently used first |

`CompactMemoryCache` acts on the registered `IMemoryCache` (not on a cache selected with `o.Cache`), and needs the
default `MemoryCache` implementation: with another one the step fails.

## Alerts from your monitoring

```bash
dotnet add package Homeostat.AspNetCore --prerelease
```

Your monitoring already detects many problems; `MapHomeostatAlerts` lets Homeostat **heal** them. It maps endpoints
that receive alert notifications and publish each alert name as a source, `alert:{name}`: the prefix keeps alerts
from impersonating the signals of detectors and probes.

```csharp
app.MapHomeostatAlerts(configure: o => o.Token = builder.Configuration["Homeostat:AlertToken"]);
```

| Endpoint | Sender |
|---|---|
| `POST /homeostat/alerts/alertmanager` | Prometheus Alertmanager webhook receiver |
| `POST /homeostat/alerts/grafana` | Grafana alerting webhook contact point |
| `POST /homeostat/alerts/azure-monitor` | Azure Monitor action group webhook, common alert schema |
| `POST /homeostat/alerts/signals` | Anything else: `{"source":"...","severity":"Unhealthy","value":1.5,"message":"..."}` or an array |

Each endpoint answers `202 Accepted` with the number of alerts accepted and skipped, as JSON:
`{ "accepted": 3, "skipped": 0 }`.

An Alertmanager receiver and a condition on one of its alerts:

```yaml
# Alertmanager
receivers:
  - name: homeostat
    webhook_configs:
      - url: https://weather-api.internal/homeostat/alerts/alertmanager
        send_resolved: true
        http_config:
          authorization:
            credentials_file: /etc/alertmanager/homeostat-token
```

```csharp
h.AddCondition("disk-almost-full", c => c
    .ForSource("alert:DiskAlmostFull")
    .WhenSeverityAtLeast(SignalSeverity.Degraded)           // warnings map to Degraded
    .ClearAfter(TimeSpan.FromMinutes(5))                    // works: the state is republished
    .Heal(p => p.With<PruneTempFilesAction>().OncePerCluster()));
```

Keep `send_resolved: true`, so conditions clear and the switches their plans turned on are turned off.

How alerts become signals:

| Alert | Signal |
|---|---|
| Source | `alert:` (`SourcePrefix`) + `alertname` label (`SourceLabel`), or the alert rule name for Azure Monitor |
| Firing, severity `critical`, `error`, `page`, `high`, `major`, `disaster`, `fatal`; Azure `Sev0`, `Sev1` | `Unhealthy` |
| Firing, any other severity | `DefaultSeverity` (`Degraded`) |
| Resolved | `Healthy` |
| Value | Grafana: the value of `GrafanaValueKey` (for example `"B"`), or the only numeric value; Azure Monitor metric alerts: `metricValue` |
| Tags | Labels of the worst firing series, `origin`, `status`, `fingerprint`, `firing` (number of firing series) |

| `AlertIngestionOptions` | Default | Meaning |
|---|---|---|
| `Token` | none | Shared secret sent as `Authorization: Bearer {token}` (or `?token=` for Azure Monitor); at least 16 characters, no white space |
| `SourceLabel` | `alertname` | Label whose value becomes the source (Alertmanager, Grafana); alerts without it are skipped |
| `SourcePrefix` | `alert:` | Prefix of every source; cannot be empty, so alerts cannot impersonate other sources |
| `SeverityLabel` | `severity` | Label read for the severity (Alertmanager, Grafana) |
| `UnhealthySeverities` | `critical`, `error`, `page`, `high`, `major`, `disaster`, `fatal` | Label values (case-insensitive) mapped to `Unhealthy`; add or remove values to match your rules |
| `DefaultSeverity` | `Degraded` | Severity of other firing alerts and of alerts without the label; `Degraded` or `Unhealthy` |
| `GrafanaValueKey` | none | Grafana query whose value becomes the signal value, for example `"B"` |
| `FiringTimeToLive` | 12 hours | Time after which a firing series that is not notified again counts as resolved |
| `MaxTrackedSources` | 1000 | Distinct sources tracked; alerts for further sources are skipped (each source tracks at most 1,000 firing series, and all sources together 10,000) |
| `MaxRequestBodySize` | 1 MiB | Largest body accepted, in bytes (at least 1024) |
| `MaxAlertsPerRequest` | 500 | Alerts accepted per request; the rest are skipped |

Notes:

- **Alerts are kept as state.** One alert name often covers several series (one per instance or pod), and senders
  notify each change once. So Homeostat keeps the firing series of each source: the source is as bad as its worst
  firing series, and `Healthy` only when none fires, so a series resolving on one pod does not clear a condition
  while another pod still fails. The aggregated signal is published when a notification arrives and again at every
  detection interval, so `SustainedFor` and `ClearAfter` behave as with any other source.
- A firing series that is not notified again within `FiringTimeToLive` (keep it longer than Alertmanager's
  `repeat_interval`) counts as resolved, which covers senders that never resolve, such as Azure Monitor stateless log
  alerts.
- The generic `/signals` endpoint is not stateful: it publishes what it receives, so send signals periodically if
  conditions use `SustainedFor`.
- Protection: the endpoints need an authorization policy, a token, or an explicit `allowAnonymous: true` for demos.
  The token is accepted as `Authorization: Bearer ...` (Alertmanager `authorization.credentials`, Grafana
  authorization header). The Azure Monitor endpoint also accepts it as the `token` query parameter, because plain
  action group webhooks cannot set headers; the URL may then appear in access logs, so prefer a policy with secure
  webhooks when you can. With a token and no policy, the endpoints allow anonymous access past a global fallback
  policy. Bodies are limited to 1 MiB, 500 alerts per request and 1000 tracked sources.
- **Several instances.** The alert state lives in the memory of the instance that receives the notifications. Behind
  a load balancer, "firing" and "resolved" can reach different instances, and the one that saw "firing" keeps the
  source unhealthy until `FiringTimeToLive`. Either send notifications to every instance (one receiver per instance,
  for example through a headless service), or to one instance and make the plan act for the cluster
  (`OncePerCluster()` with the Redis store), keeping `FiringTimeToLive` short enough to bound a lost resolution.

## Feature flags

Switches are Homeostat's degradation primitive. If your code already reads feature flags, connect them to switches
instead of adding `IsOn` checks. Both packages are previews.

### Microsoft.FeatureManagement

```bash
dotnet add package Homeostat.FeatureManagement --prerelease
```

`AddHomeostatSwitchFilter` registers the feature filter `Homeostat.Switch`, which enables a feature according to the
state of a switch.

```csharp
builder.Services.AddFeatureManagement().AddHomeostatSwitchFilter();
```

```json
"FeatureManagement": {
  "ForecastMaps": {
    "EnabledFor": [ { "Name": "Homeostat.Switch", "Parameters": { "Switch": "shed-forecast-maps" } } ]
  },
  "DegradedBanner": {
    "EnabledFor": [ { "Name": "Homeostat.Switch", "Parameters": { "Switch": "shed-forecast-maps", "EnabledWhen": "On" } } ]
  }
}
```

| Filter parameter | Default | Meaning |
|---|---|---|
| `Switch` | required | Name of the switch |
| `EnabledWhen` | `Off` | `Off`: the feature is enabled while the switch is off; `On`: while it is on |

`ForecastMaps` is enabled while the switch is off; a plan with `.TurnOnSwitch("shed-forecast-maps")` turns it off and
brings it back when the condition clears. Use `"RequirementType": "All"` to combine the filter with a rollout filter.
Invalid parameters never make the feature check throw: the feature is enabled, as when the switch is off, and a
warning is logged once per feature (for up to 1,000 features).

### OpenFeature

```bash
dotnet add package Homeostat.OpenFeature --prerelease
```

`HomeostatSwitchProvider` is an OpenFeature provider that exposes switches as boolean flags: a flag is `true` while
the switch with the same name is on.

```csharp
await Api.Instance.SetProviderAsync(new HomeostatSwitchProvider(app.Services.GetRequiredService<IHomeostatSwitches>()));

var flags = Api.Instance.GetClient();
if (await flags.GetBooleanValueAsync("serve-cached-forecast", false)) { /* degraded path */ }
```

| Constructor parameter | Default | Meaning |
|---|---|---|
| `keyPrefix` | none | Prefix that flag keys must carry, removed to get the switch name, for example `"homeostat."` |

With a `keyPrefix`, keys without the prefix return `FLAG_NOT_FOUND` whatever their type, so the provider can sit first
in a multi-provider next to your main flag service. Without a prefix every key is a switch (a non-boolean read gets
`TYPE_MISMATCH`), so put the provider **after** your main one. Switch changes emit `PROVIDER_CONFIGURATION_CHANGED`
events.

## Polly: circuit isolation and game days

```bash
dotnet add package Homeostat.Polly --prerelease
```

`Homeostat.Polly` connects Polly v8 resilience pipelines to switches: a plan can isolate a circuit breaker, and a
switch can turn chaos strategies on for a game day.

### Isolate a circuit from a plan

When a probe or an alert says a dependency is down, waiting for a circuit breaker to open wastes requests. Attach a
manual control and isolate it from a plan:

```csharp
using Homeostat.Polly;   // TrackCircuitBreaker, CircuitControl, ChaosEnabledBySwitch

builder.Services.AddResiliencePipeline("forecast", (pipeline, context) =>
    pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        StateProvider = context.ServiceProvider.TrackCircuitBreaker("forecast-circuit"),
        ManualControl = context.ServiceProvider.CircuitControl("forecast"),
    }));

builder.Services.AddHomeostat(h => h
    .AddCircuitBreakerSignals()
    .AddLatencyProbe("forecast-provider", (sp, ct) => sp.GetRequiredService<ForecastClient>().PingAsync(ct))
    .AddCondition("forecast-provider-down", c => c
        .ForSource("forecast-provider")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.IsolateCircuit("forecast"))));     // closes again when the condition clears
```

| Parameter of `IsolateCircuit` | Default | Meaning |
|---|---|---|
| `control` | required | Name of the manual control (`CircuitControl(name)`) |
| `closeWhenCleared` | `true` | Closes the circuit again when the condition clears; `false` keeps it isolated until a person turns the switch off |

`IsolateCircuit("forecast")` turns on the switch `circuit:forecast:isolated`, and the control follows the switch. You
can also isolate or close by hand with the control endpoints (`POST /homeostat/switches/circuit:forecast:isolated/on`,
for every instance that shares the store, or `?scope=instance` for one). Like any
[`TurnOnSwitch`](actions.md#turnonswitchname-and-ihomeostatswitches) step, the plan isolates the circuit of the
instance that runs it, or of every instance in a `OncePerCluster()` plan. Base the condition on a signal that
isolation does not hide (a probe, an alert), not on the circuit's own state.

### Validate plans with chaos

Polly's chaos strategies can stay in production code, off, and be enabled by a switch during a game day:

```csharp
// using Homeostat.Polly;
pipeline.AddChaosLatency(new ChaosLatencyStrategyOptions
{
    Latency = TimeSpan.FromSeconds(3),
    InjectionRate = 0.5,
    EnabledGenerator = context.ServiceProvider.ChaosEnabledBySwitch("chaos:forecast"),
});
```

| Parameter of `ChaosEnabledBySwitch` | Default | Meaning |
|---|---|---|
| `switchName` | required | Switch that enables chaos |
| `maximumDuration` | 1 hour | How long chaos lasts at most once the switch is on |

Turn `chaos:forecast` on through the control endpoints, watch conditions activate and plans heal in the status
endpoint and the audit log, then turn it off. Combine with dry-run to rehearse without acting.

> [!IMPORTANT]
> Chaos stops by itself one hour after the switch was first seen on (`ChaosEnabledBySwitch("chaos:forecast",
> maximumDuration: TimeSpan.FromMinutes(20))` to change it), and logs a warning, so a game day nobody ended does not
> keep degrading the application.

Turn the switch off and on again for another round: a new round starts even if no call went through the pipeline
while the switch was off. The clock is kept by each instance, from the first call it sees with the switch on, and is
shared by every pipeline that uses the switch, so a pipeline built again does not restart it.

## .NET Aspire

```bash
dotnet add package Homeostat.Aspire.Hosting --prerelease    # in the app host project
```

`Homeostat.Aspire.Hosting` is a preview package for the app host. It puts Homeostat on the resource in the Aspire
dashboard: a **Homeostat** link to the application's dashboard, and **Pause healing** and **Resume healing** commands,
useful while you debug a failure that Homeostat would otherwise heal. The application needs nothing specific to
Aspire: Homeostat's traces and metrics appear in the Aspire dashboard through OpenTelemetry (add the `Homeostat` source and
meter, see [Observability](observability.md)), and the Redis store can use the connection registered by the Aspire
Redis integration.

```csharp
// AppHost
builder.AddProject<Projects.WeatherApi>("weather-api")
       .WithHomeostat(o =>
       {
           o.PauseFor = TimeSpan.FromHours(1);    // Pause healing pauses for an hour (default 30 minutes)
           // o.Prefix = "/ops/homeostat";        // if the endpoints use another prefix
       });
```

| `HomeostatResourceOptions` | Default | Meaning |
|---|---|---|
| `Prefix` | `/homeostat` | Route prefix of the Homeostat endpoints in the application |
| `DashboardLink` | `true` | Adds the **Homeostat** link to the dashboard (`{prefix}/dashboard`, needs `MapHomeostatDashboard`) |
| `Commands` | `true` | Adds the **Pause healing** and **Resume healing** commands (need `MapHomeostatControl`) |
| `PauseFor` | 30 minutes | How long **Pause healing** pauses; `null` pauses until resumed |
| `EndpointName` | the HTTPS endpoint, else the HTTP one | Endpoint of the resource to link and call |
| `BearerToken` | none | Returns a bearer token for the control endpoints when their policy needs one |

Notes:

- The application maps the endpoints as usual (`MapHomeostatStatus`, `MapHomeostatControl`, `MapHomeostatDashboard`).
- The commands call the control endpoints with `X-Requested-With`. When the endpoints require a policy, give the
  commands a token with `o.BearerToken = (services, ct) => ...`, or map the endpoints with `allowAnonymous: true` in
  the development environment only.
- Commands are enabled while the resource is running, healthy or not: pausing matters most while it is failing.

See also: [Message brokers](messaging.md), [Cloud and orchestrators](cloud.md), [Detectors](detectors.md),
[Package index](packages.md).
