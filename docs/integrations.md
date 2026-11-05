# Integrations

Homeostat heals what it can see. This page shows how to bring the usual weak points of a .NET application into
view (databases, gRPC services, Redis, message brokers, caches, external monitoring) and which ready-made actions act on them
(pool resets, cache purges, circuit isolation, feature flags).

| Need | Package | API |
|---|---|---|
| Any number or round trip, polled periodically | `Homeostat` | `AddProbe`, `AddLatencyProbe` |
| Database connectivity, latency, transient errors; pool reset | `Homeostat.Data` | `AddDatabaseProbe`, `ClearConnectionPools<T>` |
| Entity Framework Core: command failures and latency of a `DbContext`, connectivity | `Homeostat.EntityFrameworkCore` | [`AddDbContextSignals<T>`, `AddDbContextProbe<T>`](#entity-framework-core) |
| gRPC clients: failures and latency judged on the gRPC status | `Homeostat.Grpc` | [`AddHomeostatGrpcSignals`](#grpc-clients), `HomeostatGrpcInterceptor` |
| Any other client (SDK, TCP protocol): failures and latency of the calls you record | `Homeostat` | [`AddCallSignals`, `CallRecorder`](detectors.md#signals-from-any-client-call-signals) |
| Redis client health | `Homeostat.Redis` | `AddRedisSignals` |
| MongoDB: ping, primary availability, pool exhaustion, replication lag | `Homeostat.MongoDB` | `AddMongoSignals`, `AddHomeostatSignals` on the client settings |
| Message brokers: backlog, dead letters, lag; pause and resume consumers | `Homeostat.ServiceBus`, `Homeostat.RabbitMQ`, `Homeostat.Kafka`, `Homeostat.Artemis` | [Message brokers](messaging.md) |
| Connection pool exhaustion (SqlClient, Npgsql) and other event counters | `Homeostat`, `Homeostat.Data` | `AddSqlClientPoolSignal`, `AddNpgsqlPoolSignal`, `AddEventCounterSignal` |
| .NET Aspire dashboard: link and Pause/Resume commands | `Homeostat.Aspire.Hosting` | `WithHomeostat` |
| Cache hit ratio; purge and compaction | `Homeostat.Caching` | `AddMemoryCacheSignals`, `PurgeHybridCacheTags`, `RemoveDistributedCacheKeys`, `CompactMemoryCache` |
| Alerts from Prometheus Alertmanager, Grafana, Azure Monitor | `Homeostat.AspNetCore` | `MapHomeostatAlerts` |
| Feature flags as degradation | `Homeostat.FeatureManagement`, `Homeostat.OpenFeature` | `AddHomeostatSwitchFilter`, `HomeostatSwitchProvider` |
| Circuit isolation and game days with Polly | `Homeostat.Polly` | `CircuitControl`, `IsolateCircuit`, `ChaosEnabledBySwitch` |

All snippets assume `builder.Services.AddHomeostat(h => ...)`. Conditions select signals with `ForSource`; see
[Conditions](conditions.md). By default a condition triggers on `Unhealthy` signals: add
`.WhenSeverityAtLeast(SignalSeverity.Degraded)` to act on `Degraded` ones too. Each package is installed with
`dotnet add package <Package> --prerelease` while the current version is a release candidate; the
[package index](packages.md) lists them all.

## Probes: any number as a signal

A probe is a function that Homeostat calls periodically, in its own service scope, with a timeout. The result
becomes the `Value` of a signal, classified by thresholds; an exception or a timeout publishes `Unhealthy`
(configurable with `FailureSeverity`) without a value.

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

| Setting | Default | Meaning |
|---|---|---|
| `Interval` | every detection interval | How often the probe runs |
| `Timeout` | 5 s | Longest call; then `outcome=timeout` |
| `DegradedAbove`, `UnhealthyAbove` | none | Upper thresholds: a value strictly above reaches them |
| `DegradedBelow`, `UnhealthyBelow` | none | Lower thresholds (free disk space, consumers online): a value strictly below reaches them |
| `DegradedLatency`, `UnhealthyLatency` | none | Latency probes only (`AddLatencyProbe`, database, Redis and MongoDB pings): the upper thresholds as durations |
| `FailureSeverity` | `Unhealthy` | Severity when the probe throws or times out |
| `Origin` | `probe`, `latency-probe` | Value of the `origin` tag; integration packages set their own (`database-probe`, `redis-ping`, ...) |

Probe, event counter and Kafka thresholds compare **strictly**: with `UnhealthyAbove = 10_000`, a backlog of exactly
10 000 is not `Unhealthy`. [Metric signals](detectors.md#metrics) (and the signals built on them, such as the server
error rate and latency signals and the Npgsql pool signal) compare **inclusively**: a value at or above
`UnhealthyAbove`, or at or below `UnhealthyBelow`, reaches it.

How probes behave:

- Probes run **in the background**: the detection loop only starts the due ones, so a slow probe delays neither the
  other probes nor the other detectors. A probe still within its timeout is not started again.
- A call that ignores cancellation is abandoned at the timeout, and the probe is **not called again until that call
  ends** (it publishes `outcome=still-running` meanwhile): a hanging dependency never piles up connections.
- Signals carry the tags `origin` (`probe`, `latency-probe`, `database-probe`, `redis-ping`, `servicebus`, `rabbitmq`, `artemis`, `mongodb`), `outcome`
  (`ok`, `failed`, `timeout`, `still-running`) and, on failure, `error` (the exception type) and, for a
  `DbException`, `transient`. Failure messages of a `DbException` keep only the exception type (provider messages
  can name servers and users); the details go to the application log.
- Without thresholds every successful call is `Healthy`, and conditions use the value directly:
  `WhenValueAbove`, `WhenRisingFasterThan`, `WhenAnomalouslyHigh`.

```csharp
// Backlog that keeps growing, even if it is still below any fixed threshold.
// IngestWorker derives from RestartableBackgroundService, so it can be restarted safely (see Actions).
h.AddCondition("backlog-growing", c => c
    .ForSource("ingest-backlog")
    .WhenRisingFasterThan(perMinute: 500, window: TimeSpan.FromMinutes(5))
    .Heal(p => p.RestartHostedService<IngestWorker>()));
```

## Databases

```bash
dotnet add package Homeostat.Data --prerelease
```

`Homeostat.Data` works with any ADO.NET provider: SqlClient, Npgsql, MySqlConnector, Microsoft.Data.Sqlite,
Oracle. It references no provider: you bring the one you already use. Applications that reach the database through
Entity Framework Core get command-level signals from `Homeostat.EntityFrameworkCore`: see
[Entity Framework Core](#entity-framework-core).

### Connectivity and latency

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

The factory must return a **new**, closed connection: the probe opens and disposes it. Never return a connection
owned by something else, such as the one of an Entity Framework Core `DbContext`.

| Setting | Default | Meaning |
|---|---|---|
| `CommandText` | `SELECT 1` | Query run after opening; `null` only opens the connection |
| Other settings | as for [probes](#probes-any-number-as-a-signal) | `Interval`, `Timeout` (also the command timeout), `DegradedLatency` and `UnhealthyLatency` (or the same thresholds in milliseconds), `FailureSeverity`, `Origin` (`database-probe`) |

### Transient errors

Failures carry `transient=true` when the provider says the error is transient (`DbException.IsTransient`):
throttling, failover in progress, a dropped connection. SQL Server's client (`Microsoft.Data.SqlClient`) does not
set `IsTransient`, so with it the tag is always `false`; the probe's failures still count. Do **not** filter transient
failures out: providers also mark "server unreachable" and timeouts as transient, so a filter would hide a full outage. Tell a blip from an outage with
time instead, and use the tag in logs, the audit trail and notifications:

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
(through its static `ClearAllPools`), and new connections are opened on demand:

```csharp
h.AddCondition("reports-db-stale", c => c
    .ForSource("reports-db")
    .SustainedFor(TimeSpan.FromSeconds(30))
    .Heal(p => p
        .ClearConnectionPools<SqlConnection>()                       // cheap, try first
        .Then("switch to read replica", sp => sp.GetRequiredService<ReportsRouting>().UseReplica())
        .RequireApproval()));
```

### Pool exhaustion

A pool that stays full makes every request wait for a connection, then fail with a timeout, often before the database
itself is in trouble. `Microsoft.Data.SqlClient` reports its pool through **event counters**, not metrics:

```csharp
h.AddSqlClientPoolSignal(o => o.MaxPoolSize = 100)   // sqlclient:pool: connections in use / Max Pool Size
 .AddCondition("sql-pool-full", c => c
     .ForSource("sqlclient:pool")
     .WhenSeverityAtLeast(SignalSeverity.Degraded)
     .SustainedFor(TimeSpan.FromMinutes(1))
     .Heal(p => p.PauseConsumer("ingest")));         // shed load: stop taking work that needs the database
```

Clearing the pools does not help here: connections in use are not closed by it. Shed load instead (pause consumers,
turn on a degraded mode), and look for the cause: slow queries holding connections, or connections not disposed
(`number-of-reclaimed-connections`). The counter covers every SqlClient pool of the process: with several connection strings, set
`MaxPoolSize` to the sum of their pool sizes. The SqlClient signal is an event counter, so its thresholds compare
strictly (above 0.8, above 0.95). Any other event counter becomes a signal with `AddEventCounterSignal(eventSource, counter, o => ...)`, with a
`Transform` and thresholds; only the counters are enabled, not the library's tracing events.

Npgsql publishes its pool through **metrics** (Npgsql 10 or later). `AddNpgsqlPoolSignal` reads the connections in
use (`db.client.connection.count` with state `used`, meter `Npgsql`) as a share of the pool size; it needs no
reference to Npgsql:

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
| `MaxPoolSize` | 100 | `Maximum Pool Size` of the connection string |
| `Source` | `sqlclient:pool`, `npgsql:pool` | Signal source |
| `DegradedAbove`, `UnhealthyAbove` | 0.8, 0.95 | Thresholds on the share in use |
| `DataSourceName` (Npgsql only) | all pools | Name of the data source (`NpgsqlDataSourceBuilder.Name`, by default the connection string without the password); `null` adds up every Npgsql pool of the process: then set `MaxPoolSize` to the sum of their sizes |

Providers that publish other pool metrics can be watched with [metric signals](detectors.md#metrics), using
`Scale(1.0 / poolSize)` to turn a count into a share.

### Entity Framework Core

```bash
dotnet add package Homeostat.EntityFrameworkCore --prerelease
```

`Homeostat.EntityFrameworkCore` watches what a `DbContext` actually does: the failure ratio and the p95 duration of
its commands, as the signal `efcore:{context type name}`. It works with any relational provider (SQL Server,
PostgreSQL, MySQL, SQLite, Oracle).

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
the context (`AddDbContext`, `AddDbContextPool` or `AddDbContextFactory`) stays as it is. What counts as
a failure:

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
| `IsFailure` | `DbContextSignalOptions.IsDatabaseFailure` | Which exceptions of failed commands count as failures of the database |
| `DegradedFailureRatio`, `UnhealthyFailureRatio` | 0.1, 0.5 | Failure ratio from which the signal is Degraded, Unhealthy |
| `DegradedLatency`, `UnhealthyLatency` | none | p95 command duration from which the signal is Degraded, Unhealthy |
| `MinimumCalls` | 10 | Commands an interval needs before its failure ratio is judged (as for [call signals](detectors.md#signals-from-any-client-call-signals)) |

Calling `AddDbContextSignals<TContext>()` again for the same context updates its settings without adding a second
interceptor.

The signal's value is the failure ratio; tags `calls`, `failures`, `failure_ratio`, `p95_ms`; `origin` = `efcore`.
To count more errors, combine the default with your own rule:
`o.IsFailure = ex => DbContextSignalOptions.IsDatabaseFailure(ex) || ex is SqlException { Number: 1205 }` (deadlocks,
with SqlClient).

Command signals depend on traffic: when the application sends no commands, no signal is published, and a plan that
waits for recovery may end `Inconclusive` in a quiet period. `AddDbContextProbe<TContext>()` covers that: at every
probe interval it calls `Database.CanConnectAsync` on a new context, in its own scope, and publishes the round trip
in milliseconds as `efcore:{context type name}:ping`, `Unhealthy` when the context cannot connect. Its options (`DbContextProbeOptions`) are the
[probe settings](#probes-any-number-as-a-signal) (`Interval`, `Timeout`, `DegradedLatency`, `UnhealthyLatency`) plus `Source`;
the context must be registered with `AddDbContext` or `AddDbContextPool`. Use the probe as the condition's source when
the plan needs verification even without traffic, and the command signals when what matters is what users see.

Notes:

- The package targets EF Core 10 (`Microsoft.EntityFrameworkCore.Relational` 10.0.14 or later); `ConfigureDbContext`
  appeared in EF Core 9.
- `ClearConnectionPools<SqlConnection>()` (package `Homeostat.Data`, or the connection type of your provider) works
  for EF Core contexts too: they use the provider's pool.
- For code that uses ADO.NET directly, without a `DbContext`, use [`AddDatabaseProbe`](#connectivity-and-latency).

## MongoDB

```bash
dotnet add package Homeostat.MongoDB --prerelease
```

`Homeostat.MongoDB` watches a deployment through the driver (MongoDB.Driver 3): a ping, and the driver's own events,
which know at once when the primary is lost or the connection pool runs dry.

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
| `mongodb:{name}:replication-lag` | seconds between the freshest data member and the furthest secondary or recovering member | above `o.Lag` thresholds (opt-in) |

- `primary` and `pool` come from the driver events, so the client must be created with `AddHomeostatSignals` on its
  settings (it keeps any `ClusterConfigurator` you set), with the same name as `AddMongoSignals`. Without it, only the
  probes are published. Nothing is published before the driver has heard from a server, so the start-up of the
  application is not reported as a lost primary.
- Register the client as a **singleton**, as MongoDB recommends. Give each deployment its own name: two clients of
  different deployments under one name would mix their states (the worst one wins). The probes use the `IMongoClient`
  registered in the services; with several clients, select one with `o.Client = sp => ...` (for example a keyed
  service).
- Replication lag: a single-node replica set reports 0; during an election the lag is measured against the freshest
  member; members that fell behind (`RECOVERING`) count, since that is when the lag matters. Leave out delayed or hidden
  members, which lag on purpose, with `o.LagExcludedMembers.Add("db-3:27017")`.
- The driver fails over by itself: the useful healing is to **degrade** while there is no primary (pause consumers,
  serve reads from secondaries or a cache) and to shed load when the pool is exhausted.

| `MongoSignalOptions` | Default | Meaning |
|---|---|---|
| `Client` | the registered `IMongoClient` | Returns the client the probes use |
| `Source` | `mongodb:{name}` | Prefix of the `:ping`, `:primary`, `:pool` and `:replication-lag` sources |
| `Interval` | 30 seconds | How often the probes run and the event-based signals are published |
| `Ping` | no thresholds | Thresholds of the ping round trip (`DegradedLatency`, `UnhealthyLatency`) |
| `PoolTimeoutsUnhealthyAbove` | 0 | Checkout timeouts per interval above which `pool` is Unhealthy |
| `ReplicationLag` | `false` | Publishes `replication-lag` (needs `replSetGetStatus`, for example the `clusterMonitor` role) |
| `Lag` | no thresholds | Thresholds of the replication lag, in seconds |
| `LagExcludedMembers` | empty | Members (`host:port`) left out of the lag, such as delayed or hidden ones |

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

StackExchange.Redis reconnects by itself, so Homeostat does not tear the multiplexer down: the useful healing is to
**degrade** (serve from memory, skip a non-essential cache) until the connection is back. Use
`abortConnect=false` so the application starts even when Redis does not, and the probe reports it
(`UseRedis(connectionString)` sets it for you). Connection events are subscribed the first time the probe reaches the
client, so they work even when Redis was down at startup.

`RedisSignalOptions` has the [probe settings](#probes-any-number-as-a-signal) (`DegradedLatency` and `UnhealthyLatency` of the `PING`
round trip, `Interval`, `Timeout`, `FailureSeverity`, `Origin`, by default `redis-ping`) and
`PublishConnectionEvents` (default `true`): set it to `false` to rely on the periodic ping alone. The default source
is `redis`. The Redis store and coordinator (`UseRedis`) are described in
[Persistence and clusters](persistence-and-clusters.md).

## gRPC clients

```bash
dotnet add package Homeostat.Grpc --prerelease
```

`Homeostat.Grpc` turns the calls of a client from the gRPC client factory (`Grpc.Net.ClientFactory`) into a signal:
the failure ratio and the p95 duration of its calls, judged on the **gRPC status** of each call.

```csharp
builder.Services.AddGrpcClient<Forecasts.ForecastsClient>(o => o.Address = new Uri("https://forecasts:5001"))
    .AddHomeostatGrpcSignals(o => o.UnhealthyLatency = TimeSpan.FromSeconds(2));

builder.Services.AddHomeostat(h => h
    .AddCondition("forecasts-failing", c => c
        .ForSource("grpc:ForecastsClient")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("serve-cached-forecast"))));
```

The source is `grpc:{client name}`. The client factory names a client after its type unless you give it a name, so
`AddGrpcClient<Forecasts.ForecastsClient>()` publishes `grpc:ForecastsClient`. `AddHomeostatGrpcSignals` needs
`AddHomeostat`.

| `GrpcSignalOptions` | Default | Meaning |
|---|---|---|
| `Source` | `grpc:{client name}` | Signal source |
| `IsFailure` | `GrpcSignalOptions.IsServiceFailure` | Which status codes count as failures |
| `DegradedFailureRatio`, `UnhealthyFailureRatio` | 0.1, 0.5 | Failure ratio from which the signal is Degraded, Unhealthy |
| `DegradedLatency`, `UnhealthyLatency` | none | p95 duration of the unary calls from which the signal is Degraded, Unhealthy |
| `MinimumCalls` | 10 | Calls an interval needs before its failure ratio is judged (as for [call signals](detectors.md#signals-from-any-client-call-signals)) |

`IsServiceFailure` counts `Unknown`, `DeadlineExceeded`, `ResourceExhausted`, `Internal`, `Unavailable` and
`DataLoss`: the statuses that point at the service or the network. Errors in the request (`NotFound`,
`InvalidArgument`, `PermissionDenied`...) and calls canceled by the caller do not count. To change the rule:
`o.IsFailure = s => GrpcSignalOptions.IsServiceFailure(s) || s == StatusCode.Aborted`.

How calls are recorded:

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

For a channel built without the client factory, add the interceptor yourself, with a recorder registered by
[`AddCallSignals`](detectors.md#signals-from-any-client-call-signals):

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

The interceptor takes an optional `isFailure` rule (by default `IsServiceFailure`) and a `TimeProvider`.

**Compared with `Homeostat.Http`.** gRPC clients of the client factory also go through `IHttpClientFactory`, so the
HTTP-level `AddHomeostatSignals` of `Homeostat.Http` works on them, with `HttpClientSignalOptions.IsGrpcFailure`
reading the `grpc-status` header (see [HTTP clients](detectors.md#grpc-clients)). That handler sees only the
response headers: a streaming call that fails after sending messages is missed, and its latency is the time to the
headers. Prefer `AddHomeostatGrpcSignals`, and do not use both on the same client.

## Message brokers

Azure Service Bus, RabbitMQ, Kafka and ActiveMQ Artemis have their own packages: backlog, consumers, dead letters and lag as signals,
and consumers that plans can pause and resume. See [Message brokers](messaging.md). For other brokers, a probe on
their client is enough:

```csharp
h.AddProbe("imports-backlog", async (sp, ct) => await sp.GetRequiredService<ImportQueue>().CountAsync(ct),
    p => { p.Interval = TimeSpan.FromSeconds(30); p.UnhealthyAbove = 5_000; });
```

## Caches

```bash
dotnet add package Homeostat.Caching --prerelease
```

### Hit ratio

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
| `DegradedBelow`, `UnhealthyBelow` | none | Hit ratio (0 to 1) below which the signal is `Degraded` or `Unhealthy`; without thresholds every interval is `Healthy` and the value carries the ratio |
| `MinimumLookups` | 20 | Hits plus misses needed in an interval to publish the ratio |

The first interval is a baseline. A falling hit ratio often explains a latency spike on the dependency behind the
cache, so it is a good `SuppressedBy` cause or a composite input.

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

## Alerts from your monitoring

Your monitoring already detects many problems; `MapHomeostatAlerts` lets Homeostat **heal** them. Each alert name
becomes a source, `alert:{name}`: the prefix keeps alerts from impersonating the signals of detectors and probes.

```bash
dotnet add package Homeostat.AspNetCore --prerelease
```

```csharp
app.MapHomeostatAlerts(configure: o => o.Token = builder.Configuration["Homeostat:AlertToken"]);
```

| Endpoint | Sender |
|---|---|
| `POST /homeostat/alerts/alertmanager` | Prometheus Alertmanager webhook receiver |
| `POST /homeostat/alerts/grafana` | Grafana alerting webhook contact point |
| `POST /homeostat/alerts/azure-monitor` | Azure Monitor action group webhook, common alert schema |
| `POST /homeostat/alerts/signals` | Anything else: `{"source":"...","severity":"Unhealthy","value":1.5,"message":"..."}` or an array |

Mapping:

| Alert | Signal |
|---|---|
| Source | `alert:` (`SourcePrefix`) + `alertname` label (`SourceLabel`), or the alert rule name for Azure Monitor |
| Firing, severity `critical`, `error`, `page`, `high`, `major`, `disaster`, `fatal`; Azure `Sev0`, `Sev1` | `Unhealthy` |
| Firing, any other severity | `DefaultSeverity` (`Degraded`) |
| Resolved | `Healthy` |
| Value | Grafana: the value of `GrafanaValueKey` (for example `"B"`), or the only numeric value; Azure Monitor metric alerts: `metricValue` |
| Tags | Labels of the worst firing series, `origin`, `status`, `fingerprint`, `firing` (number of firing series) |

**Alerts are kept as state.** One alert name often covers several series (one per instance or pod), and senders
notify each change once. So Homeostat keeps the firing series of each source: the source is as bad as its **worst
firing series**, and `Healthy` only when **none** fires, so a series resolving on one pod does not clear a condition
while another pod still fails. The aggregated signal is published when a notification arrives **and again at every
detection interval**, so `SustainedFor` and `ClearAfter` behave as with any other source. A firing series that is not
notified again within `FiringTimeToLive` (12 hours by default; keep it longer than Alertmanager's `repeat_interval`)
counts as resolved, which covers senders that never resolve, such as Azure Monitor stateless log alerts. The generic
`/signals` endpoint is not stateful: it publishes what it receives, so send signals periodically if conditions use
`SustainedFor`.

Protection: the endpoints need an authorization policy, a token, or an explicit `allowAnonymous: true` for demos.
The token is accepted as `Authorization: Bearer ...` (Alertmanager `authorization.credentials`, Grafana
authorization header); the Azure Monitor endpoint also accepts it as the `token` query parameter, because plain
action group webhooks cannot set headers (the URL may then appear in access logs: prefer a policy with secure
webhooks when you can). With a token and no policy, the endpoints allow anonymous access past a global fallback
policy. Bodies are limited to 1 MiB, 500 alerts per request and 1000 tracked sources.

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

Each endpoint answers with the number of alerts accepted and skipped, as JSON: `{ "accepted": 3, "skipped": 0 }`.

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

**Several instances.** The alert state lives in the memory of the instance that receives the notifications. Behind
a load balancer, "firing" and "resolved" can reach different instances, and the one that saw "firing" keeps the
source unhealthy until `FiringTimeToLive`. Either send notifications to every instance (one receiver per instance,
for example through a headless service), or to one instance and make the plan act for the cluster
(`OncePerCluster()` with the Redis store), keeping `FiringTimeToLive` short enough to bound a lost resolution.

## Feature flags

Switches are Homeostat's degradation primitive. If your code already reads feature flags, connect them to switches
instead of adding `IsOn` checks.

### Microsoft.FeatureManagement

```bash
dotnet add package Homeostat.FeatureManagement --prerelease
```

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

`ForecastMaps` is enabled while the switch is off; a plan with
`.TurnOnSwitch("shed-forecast-maps")` turns it off and brings it back when the
condition clears. Use `"RequirementType": "All"` to combine the filter with a rollout filter.

### OpenFeature

```bash
dotnet add package Homeostat.OpenFeature --prerelease
```

```csharp
await Api.Instance.SetProviderAsync(new HomeostatSwitchProvider(app.Services.GetRequiredService<IHomeostatSwitches>()));

var flags = Api.Instance.GetClient();
if (await flags.GetBooleanValueAsync("serve-cached-forecast", false)) { /* degraded path */ }
```

A boolean flag is `true` while the switch with the same name is on. With a `keyPrefix` (for example
`"homeostat."`), keys without the prefix return `FLAG_NOT_FOUND` whatever their type, so the provider can sit first
in a multi-provider next to your main flag service. Without a prefix every key is a switch (a non-boolean read gets
`TYPE_MISMATCH`), so put the provider **after** your main one. Switch changes emit `PROVIDER_CONFIGURATION_CHANGED`
events.

## Polly: circuit isolation and game days

```bash
dotnet add package Homeostat.Polly --prerelease
```

### Isolate a circuit from a plan

When a probe or an alert says a dependency is down, waiting for a circuit breaker to open wastes requests.
Attach a manual control and isolate it from a plan:

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

`IsolateCircuit("forecast")` turns on the switch `circuit:forecast:isolated`; the control follows the switch, so
you can also isolate or close by hand with the control endpoints (`POST /homeostat/switches/circuit:forecast:isolated/on`,
for every instance that shares the store, or `?scope=instance` for one). Like any
[`TurnOnSwitch`](actions.md#turnonswitchname-and-ihomeostatswitches) step, the plan isolates the circuit of the
instance that runs it, or of every instance in a `OncePerCluster()` plan.
Base the condition on a signal that isolation does not hide (a probe, an alert), not on the circuit's own state.

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

Turn `chaos:forecast` on through the control endpoints, watch conditions activate and plans heal in the status
endpoint and the audit log, then turn it off. Combine with dry-run to rehearse without acting.

Chaos stops by itself one hour after the switch was first seen on (`ChaosEnabledBySwitch("chaos:forecast",
maximumDuration: TimeSpan.FromMinutes(20))` to change it), and logs a warning: a game day nobody ended does not keep
degrading the application. Turn the switch off and on again for another round. The clock is kept by each instance,
from the first call it sees with the switch on.

## .NET Aspire

Homeostat works in Aspire applications as in any ASP.NET Core application: its traces and metrics appear in the
Aspire dashboard through OpenTelemetry (add the `Homeostat` source and meter, see [Observability](observability.md)),
and the Redis store can use the connection registered by the Aspire Redis integration.

```bash
dotnet add package Homeostat.Aspire.Hosting --prerelease    # in the app host project
```

In the app host, `Homeostat.Aspire.Hosting` puts Homeostat on the resource in the Aspire dashboard: a **Homeostat**
link to the application's dashboard, and **Pause healing** and **Resume healing** commands, handy while you debug a
failure that Homeostat would otherwise heal.

```csharp
// AppHost
builder.AddProject<Projects.WeatherApi>("weather-api")
       .WithHomeostat(o =>
       {
           o.PauseFor = TimeSpan.FromHours(1);    // Pause healing pauses for an hour (default 30 minutes)
           // o.Prefix = "/ops/homeostat";        // if the endpoints use another prefix
       });
```

The application maps the endpoints as usual (`MapHomeostatStatus`, `MapHomeostatControl`, `MapHomeostatDashboard`).
The commands call the control endpoints with `X-Requested-With`; when they require a policy, give the commands a token
with `o.BearerToken = (services, ct) => ...`, or map them with `allowAnonymous: true` in the development environment
only. Commands are enabled while the resource is running, healthy or not: pausing matters most while it is failing.

| `HomeostatResourceOptions` | Default | Meaning |
|---|---|---|
| `Prefix` | `/homeostat` | Route prefix of the Homeostat endpoints in the application |
| `DashboardLink` | `true` | Adds the **Homeostat** link to the dashboard (`{prefix}/dashboard`, needs `MapHomeostatDashboard`) |
| `Commands` | `true` | Adds the **Pause healing** and **Resume healing** commands (need `MapHomeostatControl`) |
| `PauseFor` | 30 minutes | How long **Pause healing** pauses; `null` pauses until resumed |
| `EndpointName` | the HTTPS endpoint, else the HTTP one | Endpoint of the resource to link and call |
| `BearerToken` | none | Returns a bearer token for the control endpoints when their policy needs one |
