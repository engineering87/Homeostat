# Integrations

Homeostat heals what it can see. This page shows how to bring the usual weak points of a .NET application into
view (databases, Redis, message brokers, caches, external monitoring) and which ready-made actions act on them
(pool resets, cache purges, circuit isolation, feature flags).

| Need | Package | API |
|---|---|---|
| Any number or round trip, polled periodically | `Homeostat` | `AddProbe`, `AddLatencyProbe` |
| Database connectivity, latency, transient errors; pool reset | `Homeostat.Data` | `AddDatabaseProbe`, `ClearConnectionPools<T>` |
| Redis client health | `Homeostat.Redis` | `AddRedisSignals` |
| MongoDB: ping, primary availability, pool exhaustion, replication lag | `Homeostat.MongoDB` | `AddMongoSignals`, `AddHomeostatSignals` on the client settings |
| Message brokers: backlog, dead letters, lag; pause and resume consumers | `Homeostat.ServiceBus`, `Homeostat.RabbitMQ`, `Homeostat.Kafka`, `Homeostat.Artemis` | [Message brokers](messaging.md) |
| Libraries that report through event counters (SqlClient pool) | `Homeostat`, `Homeostat.Data` | `AddEventCounterSignal`, `AddSqlClientPoolSignal` |
| .NET Aspire dashboard: link and Pause/Resume commands | `Homeostat.Aspire.Hosting` | `WithHomeostat` |
| Cache hit ratio; purge and compaction | `Homeostat.Caching` | `AddMemoryCacheSignals`, `PurgeHybridCacheTags`, `RemoveDistributedCacheKeys`, `CompactMemoryCache` |
| Alerts from Prometheus Alertmanager, Grafana, Azure Monitor | `Homeostat.AspNetCore` | `MapHomeostatAlerts` |
| Feature flags as degradation | `Homeostat.FeatureManagement`, `Homeostat.OpenFeature` | `AddHomeostatSwitchFilter`, `HomeostatSwitchProvider` |
| Circuit isolation and game days with Polly | `Homeostat.Polly` | `CircuitControl`, `IsolateCircuit`, `ChaosEnabledBySwitch` |

All snippets assume `builder.Services.AddHomeostat(h => ...)`. Conditions select signals with `ForSource`; see
[Conditions](conditions.md). By default a condition triggers on `Unhealthy` signals: add
`.WhenSeverityAtLeast(SignalSeverity.Degraded)` to act on `Degraded` ones too.

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
    p => p.DegradedAbove = 250);                 // milliseconds
```

| Setting | Default | Meaning |
|---|---|---|
| `Interval` | every detection interval | How often the probe runs |
| `Timeout` | 5 s | Longest call; then `outcome=timeout` |
| `DegradedAbove`, `UnhealthyAbove` | none | Upper thresholds |
| `DegradedBelow`, `UnhealthyBelow` | none | Lower thresholds (free disk space, consumers online) |
| `FailureSeverity` | `Unhealthy` | Severity when the probe throws or times out |

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

`Homeostat.Data` works with any ADO.NET provider: SqlClient, Npgsql, MySqlConnector, Microsoft.Data.Sqlite,
Oracle.

### Connectivity and latency

```csharp
h.AddDatabaseProbe("catalog-db",
    _ => new SqlConnection(builder.Configuration.GetConnectionString("Catalog")),
    p =>
    {
        p.DegradedAbove = 200;        // ms to open (from the pool) and run SELECT 1
        p.UnhealthyAbove = 2_000;
        // p.CommandText = "SELECT 1 FROM DUAL";   // Oracle; null only opens the connection
    });
```

The factory must return a **new** connection: the probe opens and disposes it. With Entity Framework Core, build a
connection from the context's connection string, or probe the context itself:

```csharp
// A new connection with the same connection string as the DbContext.
h.AddDatabaseProbe("catalog-db",
    sp => new SqlConnection(sp.GetRequiredService<CatalogDbContext>().Database.GetConnectionString()));

// Or ask EF Core (the probe gets its own scope, so a scoped DbContext is fine).
h.AddLatencyProbe("catalog-db", async (sp, ct) =>
{
    if (!await sp.GetRequiredService<CatalogDbContext>().Database.CanConnectAsync(ct))
    {
        throw new InvalidOperationException("The catalog database is not reachable.");
    }
});
```

### Transient errors

Failures carry `transient=true` when the provider says the error is transient (`DbException.IsTransient`):
throttling, failover in progress, a dropped connection. Do **not** filter transient failures out: providers also mark
"server unreachable" and timeouts as transient, so a filter would hide a full outage. Tell a blip from an outage with
time instead, and use the tag in logs, the audit trail and notifications:

```csharp
h.AddCondition("catalog-db-down", c => c
    .ForSource("catalog-db")
    .SustainedFor(TimeSpan.FromMinutes(1))          // a blip clears before this
    .Heal(p => p.TurnOnSwitch("catalog-read-only", turnOffWhenCleared: true)));

h.AddCondition("catalog-db-flapping", c => c
    .ForSource("catalog-db")
    .Occurrences(5, within: TimeSpan.FromMinutes(10))   // intermittent failures
    .Heal(p => p.ClearConnectionPools<SqlConnection>()));
```

### Pool reset

After a failover or a network change, pooled connections can point to a server that no longer answers, and every
request pays a timeout before the pool notices. `ClearConnectionPools<TConnection>()` empties the provider's pools
(through its static `ClearAllPools`), and new connections are opened on demand:

```csharp
h.AddCondition("catalog-db-stale", c => c
    .ForSource("catalog-db")
    .SustainedFor(TimeSpan.FromSeconds(30))
    .Heal(p => p
        .ClearConnectionPools<SqlConnection>()                       // cheap, try first
        .Then("switch to read replica", sp => sp.GetRequiredService<CatalogRouting>().UseReplica())
        .RequireApproval()));
```

### Pool exhaustion

A pool that stays full makes every request wait for a connection, then fail with a timeout, often before the database
itself is in trouble. `Microsoft.Data.SqlClient` reports its pool through **event counters**, not metrics:

```csharp
h.AddSqlClientPoolSignal(maxPoolSize: 100)        // sqlclient-pool: connections in use / Max Pool Size
 .AddCondition("sql-pool-full", c => c
     .ForSource("sqlclient-pool")
     .WhenSeverityAtLeast(SignalSeverity.Degraded)
     .SustainedFor(TimeSpan.FromMinutes(1))
     .Heal(p => p.PauseConsumer("ingest")));         // shed load: stop taking work that needs the database
```

Clearing the pools does not help here: connections in use are not closed by it. Shed load instead (pause consumers,
turn on a degraded mode), and look for the cause: slow queries holding connections, or connections not disposed
(`number-of-reclaimed-connections`). The counter covers every SqlClient pool of the process: with several connection strings, pass the sum of their pool
sizes. Any other event counter becomes a signal with `AddEventCounterSignal(eventSource, counter, o => ...)`, with a
`Transform` and thresholds; only the counters are enabled, not the library's tracing events.

Providers that publish pool metrics can be watched with [metric signals](detectors.md#metrics). Npgsql, for
example, exposes the pending connection requests on its `Npgsql` meter (check the instrument names of your provider
version):

```csharp
h.AddMetricSignal("db.client.connections.pending_requests", m => m
    .FromMeter("Npgsql")
    .Source("catalog-db-pool")
    .Statistic(MetricStatistic.Last)
    .UnhealthyAbove(20));
```

## MongoDB

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
        o.Ping.DegradedAbove = 100;                    // milliseconds
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
| `mongodb:{name}:ping` | round trip of `ping`, ms | the ping fails or times out (thresholds with `o.Ping`) |
| `mongodb:{name}:primary` | servers connected | no writable server (primary, mongos, standalone) is connected; published at once when it changes, then every `Interval` |
| `mongodb:{name}:pool` | connection checkouts that timed out in the `Interval` (30 seconds by default) | more than `PoolTimeoutsUnhealthyAbove` (0: any) |
| `mongodb:{name}:replication-lag` | seconds between the freshest data member and the furthest secondary or recovering member | above `o.Lag` thresholds (opt-in) |

- `primary` and `pool` come from the driver events, so the client must be created with `AddHomeostatSignals` on its
  settings (it keeps any `ClusterConfigurator` you set), with the same name as `AddMongoSignals`. Without it, only the
  probes are published. Nothing is published before the driver has heard from a server, so the start-up of the
  application is not reported as a lost primary.
- Register the client as a **singleton**, as MongoDB recommends. Give each deployment its own name: two clients of
  different deployments under one name would mix their states (the worst one wins).
- Replication lag: a single-node replica set reports 0; during an election the lag is measured against the freshest
  member; members that fell behind (`RECOVERING`) count, since that is when the lag matters. Leave out delayed or hidden
  members, which lag on purpose, with `o.LagExcludedMembers.Add("db-3:27017")`.
- The driver fails over by itself: the useful healing is to **degrade** while there is no primary (pause consumers,
  serve reads from secondaries or a cache) and to shed load when the pool is exhausted.

## Redis

`AddRedisSignals` watches the `IConnectionMultiplexer` registered in the container (the one used by `UseRedis`, or
your caching one): a periodic `PING`, whose round trip in milliseconds is the value, plus an immediate signal when
the client loses or restores a connection.

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect("redis:6379,abortConnect=false"));

builder.Services.AddHomeostat(h => h
    .AddRedisSignals("session-cache", o => o.DegradedAbove = 50)
    .AddCondition("session-cache-down", c => c
        .ForSource("session-cache")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .ClearAfter(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("sessions-in-memory", turnOffWhenCleared: true))));
```

StackExchange.Redis reconnects by itself, so Homeostat does not tear the multiplexer down: the useful healing is to
**degrade** (serve from memory, skip a non-essential cache) until the connection is back. Use
`abortConnect=false` so the application starts even when Redis does not, and the probe reports it
(`UseRedis(connectionString)` sets it for you). Connection events are subscribed the first time the probe reaches the
client, so they work even when Redis was down at startup.

## Message brokers

Azure Service Bus, RabbitMQ, Kafka and ActiveMQ Artemis have their own packages: backlog, consumers, dead letters and lag as signals,
and consumers that plans can pause and resume. See [Message brokers](messaging.md). For other brokers, a probe on
their client is enough:

```csharp
h.AddProbe("imports-backlog", async (sp, ct) => await sp.GetRequiredService<ImportQueue>().CountAsync(ct),
    p => { p.Interval = TimeSpan.FromSeconds(30); p.UnhealthyAbove = 5_000; });
```

## Caches

### Hit ratio

```csharp
builder.Services.AddMemoryCache(o => o.TrackStatistics = true);   // required
h.AddMemoryCacheSignals("lookup-cache", o =>
{
    o.DegradedBelow = 0.6;      // hit ratio over the last detection interval
    o.MinimumLookups = 20;      // no signal for quieter intervals
});
```

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

Keep `send_resolved: true`, so conditions clear and switches turned on with `turnOffWhenCleared` are turned off.

**Several instances.** The alert state lives in the memory of the instance that receives the notifications. Behind
a load balancer, "firing" and "resolved" can reach different instances, and the one that saw "firing" keeps the
source unhealthy until `FiringTimeToLive`. Either send notifications to every instance (one receiver per instance,
for example through a headless service), or to one instance and make the plan act for the cluster
(`OncePerCluster()` with the Redis store), keeping `FiringTimeToLive` short enough to bound a lost resolution.

## Feature flags

Switches are Homeostat's degradation primitive. If your code already reads feature flags, connect them to switches
instead of adding `IsOn` checks.

### Microsoft.FeatureManagement

```csharp
builder.Services.AddFeatureManagement().AddHomeostatSwitchFilter();
```

```json
"FeatureManagement": {
  "Recommendations": {
    "EnabledFor": [ { "Name": "Homeostat.Switch", "Parameters": { "Switch": "shed-recommendations" } } ]
  },
  "DegradedBanner": {
    "EnabledFor": [ { "Name": "Homeostat.Switch", "Parameters": { "Switch": "shed-recommendations", "EnabledWhen": "On" } } ]
  }
}
```

`Recommendations` is enabled while the switch is off; a plan with
`.TurnOnSwitch("shed-recommendations", turnOffWhenCleared: true)` turns it off and brings it back when the
condition clears. Use `"RequirementType": "All"` to combine the filter with a rollout filter.

### OpenFeature

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

### Isolate a circuit from a plan

When a probe or an alert says a dependency is down, waiting for a circuit breaker to open wastes requests.
Attach a manual control and isolate it from a plan:

```csharp
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
you can also isolate or close by hand with the control endpoints (`POST /homeostat/switches/circuit:forecast:isolated/on`).
Base the condition on a signal that isolation does not hide (a probe, an alert), not on the circuit's own state.

### Validate plans with chaos

Polly's chaos strategies can stay in production code, off, and be enabled by a switch during a game day:

```csharp
pipeline.AddChaosLatency(new ChaosLatencyStrategyOptions
{
    Latency = TimeSpan.FromSeconds(3),
    InjectionRate = 0.5,
    EnabledGenerator = context.ServiceProvider.ChaosEnabledBySwitch("chaos:forecast"),
});
```

Turn `chaos:forecast` on through the control endpoints, watch conditions activate and plans heal in the status
endpoint and the audit log, then turn it off. Combine with dry-run to rehearse without acting.

## .NET Aspire

Homeostat works in Aspire applications as in any ASP.NET Core application: its traces and metrics appear in the
Aspire dashboard through OpenTelemetry (add the `Homeostat` source and meter, see [Observability](observability.md)),
and the Redis store can use the connection registered by the Aspire Redis integration.

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
only. Commands are enabled when the resource is running and healthy.
