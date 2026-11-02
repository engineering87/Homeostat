# Integrations

Homeostat heals what it can see. This page shows how to bring the usual weak points of a .NET application into
view (databases, Redis, message brokers, caches, external monitoring) and which ready-made actions act on them
(pool resets, cache purges, circuit isolation, feature flags).

| Need | Package | API |
|---|---|---|
| Any number or round trip, polled periodically | `Homeostat` | `AddProbe`, `AddLatencyProbe` |
| Database connectivity, latency, transient errors; pool reset | `Homeostat.Data` | `AddDatabaseProbe`, `ClearConnectionPools<T>` |
| Redis client health | `Homeostat.Redis` | `AddRedisSignals` |
| Message brokers (Service Bus, RabbitMQ, Kafka) | `Homeostat` | recipes with `AddProbe` below |
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
- Signals carry the tags `origin` (`probe`, `latency-probe`, `database-probe`, `redis-ping`), `outcome`
  (`ok`, `failed`, `timeout`, `still-running`) and, on failure, `error` (the exception type) and, for a
  `DbException`, `transient`. Failure messages of a `DbException` keep only the exception type (provider messages
  can name servers and users); the details go to the application log.
- Without thresholds every successful call is `Healthy`, and conditions use the value directly:
  `WhenValueAbove`, `WhenRisingFasterThan`, `WhenAnomalouslyHigh`.

```csharp
// Backlog that keeps growing, even if it is still below any fixed threshold.
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

Brokers expose their depth and dead letters through their own clients, so a probe is the most portable way to
watch them. The snippets below show the idea; they are **not compiled in this repository** (the client libraries
are not dependencies of Homeostat), so check them against the client version you use.

```csharp
// Azure Service Bus (Azure.Messaging.ServiceBus): active and dead-lettered messages.
h.AddProbe("documents-queue", async (sp, ct) =>
{
    var admin = sp.GetRequiredService<ServiceBusAdministrationClient>();
    var properties = await admin.GetQueueRuntimePropertiesAsync("documents", ct);
    return properties.Value.ActiveMessageCount;
}, p => { p.Interval = TimeSpan.FromSeconds(30); p.UnhealthyAbove = 5_000; });

h.AddProbe("documents-dead-letters", async (sp, ct) =>
    (await sp.GetRequiredService<ServiceBusAdministrationClient>()
        .GetQueueRuntimePropertiesAsync("documents", ct)).Value.DeadLetterMessageCount);

// RabbitMQ (RabbitMQ.Client 7): ready messages in a queue.
h.AddProbe("ingest-queue", async (sp, ct) =>
    await sp.GetRequiredService<IngestChannel>().Channel.MessageCountAsync("ingest", ct));

// Kafka (Confluent.Kafka): consumer lag = high watermark - committed offset, summed over the assignment.
h.AddProbe("events-lag", (sp, ct) =>
{
    var consumer = sp.GetRequiredService<IConsumer<string, string>>();
    var committed = consumer.Committed(consumer.Assignment, TimeSpan.FromSeconds(2));
    double lag = 0;
    foreach (var offset in committed)
    {
        var high = consumer.QueryWatermarkOffsets(offset.TopicPartition, TimeSpan.FromSeconds(2)).High;
        lag += offset.Offset.Value < 0 ? 0 : high.Value - offset.Offset.Value;
    }
    return Task.FromResult(lag);
});
```

Then heal with the actions you already have:

```csharp
h.AddCondition("ingest-stuck", c => c
    .ForSource("ingest-queue")
    .WhenRisingFasterThan(perMinute: 200, window: TimeSpan.FromMinutes(5))
    .Heal(p => p
        .RestartHostedService<IngestConsumer>()                    // restart the consumer loop
        .Then("pause producers", sp => sp.GetRequiredService<IHomeostatSwitches>().TurnOn("pause-ingest"))));

h.AddCondition("poison-messages", c => c
    .ForSource("documents-dead-letters")
    .WhenValueAbove(0)
    .Heal(p => p.TurnOnSwitch("quarantine-documents")));          // the consumer checks the switch
```

Native packages for the main brokers (lag and dead-letter signals, pause and resume of consumers) are on the
[roadmap](ROADMAP.md).

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
and the Redis store can use the connection registered by the Aspire Redis integration. A dedicated hosting
integration is on the [roadmap](ROADMAP.md).
