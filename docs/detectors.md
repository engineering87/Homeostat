# Detectors

Detectors produce signals **without you writing any code**. They sample part of the application at a
fixed interval and publish one signal per sample, ready to be used by [conditions](conditions.md).

| Detector | Package | Default source | Watches |
|---|---|---|---|
| Thread pool starvation | `Homeostat` | `threadpool` | How long work waits before running |
| Memory pressure | `Homeostat` | `memory` | Memory in use vs. memory available |
| Exception rate | `Homeostat` | `exceptions` | Exceptions thrown per second |
| HTTP clients | `Homeostat.Http` | `http:{client name}` | Failure ratio and p95 latency of each client |
| gRPC clients | `Homeostat.Grpc` | `grpc:{client name}` | Failure ratio and p95 latency of each client, judged on the gRPC status. See [Integrations](integrations.md#grpc-clients) |
| Entity Framework Core | `Homeostat.EntityFrameworkCore` | `efcore:{context type name}` | Failure ratio and p95 latency of the commands of a `DbContext`. See [Integrations](integrations.md#entity-framework-core) |
| Call signals | `Homeostat` | your choice | Failure ratio and p95 latency of calls you record, for any client. See [Call signals](#signals-from-any-client-call-signals) |
| Circuit breakers | `Homeostat.Polly` | your choice | State of Polly v8 circuit breakers |
| Metrics | `Homeostat` | `metric:{instrument}` or your choice | Any `System.Diagnostics.Metrics` instrument: ASP.NET Core, HttpClient, Kestrel, runtime, your own |
| Health checks | `Homeostat.HealthChecks` | health check name | See [Signals](signals.md#health-checks-as-signals) |
| Probes | `Homeostat` | your choice | Any number or round trip you can fetch: backlogs, lag, free space, pings. See [Integrations](integrations.md#probes-any-number-as-a-signal) |
| Databases | `Homeostat.Data` | your choice | Connection round trip and transient errors of any ADO.NET provider |
| Redis client | `Homeostat.Redis` | `redis` | `PING` round trip and lost connections |
| Memory cache | `Homeostat.Caching` | `cache:memory` | Hit ratio of an `IMemoryCache` |
| Alerts | `Homeostat.AspNetCore` | `alert:{alert name}` | Alertmanager, Grafana and Azure Monitor notifications. See [Integrations](integrations.md#alerts-from-your-monitoring) |

Most detectors take a sample every `DetectionInterval` (default **10 seconds**). The exceptions have their own
interval: health checks follow the `AddHealthCheckSignals` period; probes with their own `Interval`; the Service Bus,
RabbitMQ, Artemis and MongoDB signals (`Interval`, default 30 seconds); and event counters (`Interval`, default
10 seconds). To change the detection interval:

```csharp
.AddHomeostat(h => h.Configure(o => o.DetectionInterval = TimeSpan.FromSeconds(5)))
```

> Remember the rule from [Conditions](conditions.md#how-long-sustainedfor): keep `SustainedFor`
> longer than the detection interval.

---

## Starter presets

Most applications want the same first signals. A preset registers them, with a condition for each, in one line:

```csharp
builder.Services.AddHomeostat(h => h.AddWebApiDefaults());     // ASP.NET Core application
builder.Services.AddHomeostat(h => h.AddWorkerDefaults());     // worker service
```

`AddWebApiDefaults` registers the thread pool, memory and exception detectors, the
[server error rate and latency signals](#ready-made-server-errors-and-latency), and these conditions.
`AddWorkerDefaults` registers the runtime detectors and their conditions, plus the state of every
`RestartableBackgroundService` (`AddBackgroundServiceSignals`, sources `hosted-service:{type name}`) without a condition.

| Condition | Property | Source | Active when | Sustained for | Web API | Worker | Suppressed by |
|---|---|---|---|---|---|---|---|
| `threadpool-starved` | `ThreadPoolStarvation` | `threadpool` | Work waits 1 s or more | 30 s | On | On | |
| `memory-pressure` | `MemoryPressure` | `memory` | Memory load is 0.95 or more | 2 min | On | On | |
| `exception-rate-high` | `ExceptionRate` | `exceptions` | 50 exceptions per second or more | 1 min | On | On | |
| `server-errors-high` | `ServerErrors` | `http-server:errors` | 5% or more of the requests answer 5xx | 1 min | On | Off | `threadpool-starved` |
| `server-latency-high` | `ServerLatency` | `http-server:latency` | p95 request duration is 2 s or more | 2 min | On | Off | `threadpool-starved`, `memory-pressure` |

"Active when" is the `Unhealthy` threshold of the signal: like any condition, the preset conditions trigger on
`Unhealthy` signals. A starved thread pool or memory pressure makes requests slow and failing, so the server
conditions wait while those causes are active instead of acting on the symptom (see
[`SuppressedBy`](conditions.md#causes-and-symptoms-suppressedby)); a cause that is turned off is not used for
suppression. The server signals need ASP.NET Core, and publish nothing in intervals with fewer than 20 requests.

**The conditions only observe.** They are tracked, logged and shown by the endpoints and the dashboard, and none
acts until you give it a plan. Watch their activations first, then add plans one at a time. The
`HomeostatDefaults` object passed to `configure` has one `DefaultCondition` per row of the table:

```csharp
builder.Services.AddHomeostat(h => h.AddWebApiDefaults(d =>
{
    d.MemoryPressure.Enabled = false;                          // no condition and no detector
    d.ServerLatency.SustainedFor = TimeSpan.FromMinutes(5);    // the problem must last longer
    d.ExceptionRate.Configure(c => c.ClearAfter(TimeSpan.FromMinutes(2)));   // any other condition setting
    d.ServerErrors.Heal(p => p.TurnOnSwitch("serve-cached-forecast"));   // a plan
}));
```

| `DefaultCondition` | Meaning |
|---|---|
| `Name`, `Source` | The condition's name and the source it watches (read-only) |
| `Enabled` | Whether the preset registers the condition and the detector or signal behind it |
| `SustainedFor` | How long the problem must last before the condition becomes active |
| `Configure(c => ...)` | Further settings on the `ConditionBuilder`, applied after the preset's own: `ClearAfter`, `WhenSeverityAtLeast`, more `SuppressedBy` causes, a plan |
| `Heal(p => ...)` | Gives the condition a healing plan; same as `Configure(c => c.Heal(...))` |

Plans added this way follow the usual guardrails, dry-run included. Other conditions can refer to the preset ones by
name, for example `.SuppressedBy("threadpool-starved")`.

Do not also call the methods that the preset calls (`AddServerErrorRateSignal`, `AddServerLatencySignal`,
`AddThreadPoolStarvationDetector` and the others): the server signals would be published twice. To make a condition
trigger at a different level, change its rule; the preset's duration and suppression stay as they are:

```csharp
h.AddWebApiDefaults(d =>
{
    d.ServerErrors.Configure(c => c.WhenValueAbove(0.02));    // 2% of 5xx responses instead of 5%
    d.ServerLatency.Configure(c => c.WhenValueAbove(1.0));    // p95 of 1 s instead of 2 s
});
```

The value of `http-server:errors` is the share of 5xx responses (0 to 1), that of `http-server:latency` the p95 in
seconds. Only one preset can be added to an application: a second `AddWebApiDefaults` or `AddWorkerDefaults` throws.

---

## Thread pool starvation

```csharp
.AddHomeostat(h => h
    .AddThreadPoolStarvationDetector()
    .AddCondition("threadpool-starved", c => c
        .ForSource("threadpool")
        .SustainedFor(TimeSpan.FromSeconds(30))))
```

At each sample the detector queues a tiny work item and measures how long it waits before running.
When the pool is starved, typically by blocking calls such as `.Result` or `.Wait()` on async code,
that wait grows and every request in the application slows down with it.

| Option | Default | Meaning |
|---|---|---|
| `Source` | `"threadpool"` | Signal source |
| `DegradedLatency` | 100 ms | Wait at or above which the signal is `Degraded` |
| `UnhealthyLatency` | 1 s | Wait at or above which the signal is `Unhealthy` |

Tags: `latency_ms`, `threads`, `pending`; `origin` = `detector`.

If the work item has not run after twice `UnhealthyLatency`, the sample is reported as `Unhealthy`.
Latency uses the real clock, even in the test host.

Thread pool starvation is usually a code problem that no action can truly fix. A common use is an
**observe-only condition** (no `Heal(...)`) that makes the problem visible in logs and metrics.

---

## Memory pressure

```csharp
.AddMemoryPressureDetector(o => o.UnhealthyLoad = 0.9)
```

Reports memory load as seen by the garbage collector: physical memory in use on the machine (or in the
container, when it has a memory limit) relative to the memory available, so other processes count too.

| Option | Default | Meaning |
|---|---|---|
| `Source` | `"memory"` | Signal source |
| `DegradedLoad` | 0.85 | Load (0–1) at or above which the signal is `Degraded` |
| `UnhealthyLoad` | 0.95 | Load (0–1) at or above which the signal is `Unhealthy` |

Tags: `load`, `heap_bytes`, `gc_pause_percent`; `origin` = `detector`.

Values refresh at each garbage collection; until the first collection, no signal is published.
Typical actions: clear an in-memory cache, turn on a switch that reduces batch sizes.

---

## Exception rate

```csharp
.AddExceptionRateDetector(o =>
{
    o.DegradedPerSecond = 20;
    o.UnhealthyPerSecond = 100;
})
```

Counts every exception thrown in the process (first-chance, including handled ones) and reports the
rate since the previous sample.

| Option | Default | Meaning |
|---|---|---|
| `Source` | `"exceptions"` | Signal source |
| `DegradedPerSecond` | 10 | Rate at or above which the signal is `Degraded` |
| `UnhealthyPerSecond` | 50 | Rate at or above which the signal is `Unhealthy` |

Tags: `rate_per_second`; `origin` = `detector`.

Every application has its own normal baseline: watch it in dry-run first and **tune the thresholds
before acting on them**.

---

## HTTP clients

```bash
dotnet add package Homeostat.Http --prerelease
```

One line per client built with `IHttpClientFactory`:

```csharp
builder.Services.AddHttpClient("forecast", c => c.BaseAddress = new Uri("https://forecast.example"))
    .AddHomeostatSignals(o => o.UnhealthyLatency = TimeSpan.FromSeconds(2));

builder.Services.AddHomeostat(h => h
    .AddCondition("forecast-api-failing", c => c
        .ForSource("http:forecast")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.TurnOnSwitch("serve-cached-forecast"))));
```

Typed clients work too: `AddHttpClient<ForecastApi>().AddHomeostatSignals()`; the client name is the
type name. Calling `AddHomeostatSignals` again on the same client replaces its options.

At each sample the detector looks at the requests sent since the previous one:

| Situation | Signal |
|---|---|
| No requests | None (nothing to judge) |
| Fewer than `MinimumCalls`, none failed | `Healthy` (latency is not considered) |
| Fewer than `MinimumCalls`, some failed | None (too few to judge) |
| Otherwise | By failure ratio, raised by p95 latency if thresholds are set |

| Option | Default | Meaning |
|---|---|---|
| `Source` | `"http:{name}"` | Signal source |
| `DegradedFailureRatio` | 0.1 | Failure ratio at or above which the signal is `Degraded` |
| `UnhealthyFailureRatio` | 0.5 | Failure ratio at or above which the signal is `Unhealthy` |
| `DegradedLatency` | none | p95 latency at or above which the signal is at least `Degraded` |
| `UnhealthyLatency` | none | p95 latency at or above which the signal is `Unhealthy` |
| `MinimumCalls` | 10 | Requests needed to judge a ratio |
| `IsFailure` | `HttpClientSignalOptions.IsServiceFailure`: 5xx, 408, 429, failed gRPC calls | Which responses count as failures. Combine the default with your own rule: `o.IsFailure = r => HttpClientSignalOptions.IsServiceFailure(r) \|\| r.StatusCode == HttpStatusCode.Conflict` |
| `CountCancellationsAsFailures` | `true` | Whether canceled requests (including `HttpClient` timeouts) count as failures |

`AddHomeostatSignals` refuses settings that could never give a meaningful signal: ratios outside (0, 1] or a
degraded ratio above the unhealthy one, latency thresholds that are not positive, `MinimumCalls` below 1, or a
null `IsFailure`.

Exceptions always count as failures. Canceled requests count as failures too, because `HttpClient`
reports its own timeout as a cancellation and the two cannot be told apart from inside the handler;
set `CountCancellationsAsFailures = false` if your callers often cancel requests for unrelated reasons.
Tags: `requests`, `failures`, `failure_ratio`, `p95_ms`; `origin` = `http-client`.

> When traffic stops, no signal is published: a condition stays as it was. If the verification of a
> plan depends on these signals, expect `Inconclusive` outcomes during quiet periods.

### gRPC clients

For gRPC clients, prefer the `Homeostat.Grpc` package and its `AddHomeostatGrpcSignals()`: it judges every call on
its gRPC status, including the status that a streaming call reports in the trailers, and measures the duration of
unary calls. See [Integrations](integrations.md#grpc-clients). Do not use both on the same client.

gRPC clients built with `Grpc.Net.Client` and `IHttpClientFactory` (`Grpc.Net.ClientFactory`) also go through the
HTTP handler of `Homeostat.Http`, so `AddHomeostatSignals` gives them HTTP-level signals when adding a package is not
an option:

```csharp
builder.Services.AddGrpcClient<Forecaster.ForecasterClient>(o => o.Address = new Uri("https://forecast.example"))
    .AddHomeostatSignals(o => o.Source = "grpc:forecast");
```

A gRPC call answers HTTP 200 even when it fails, with the status in the `grpc-status` header. The default `IsFailure`
also counts as failures the `application/grpc` responses whose `grpc-status` header is `UNKNOWN` (2),
`DEADLINE_EXCEEDED` (4), `RESOURCE_EXHAUSTED` (8), `INTERNAL` (13), `UNAVAILABLE` (14) or `DATA_LOSS` (15): the
statuses that point at the server or the network rather than at the request. `HttpClientSignalOptions.IsGrpcFailure`
is the same test, to combine with your own rule:

```csharp
o.IsFailure = r => (int)r.StatusCode >= 500 || HttpClientSignalOptions.IsGrpcFailure(r);
```

Limitation: the handler sees the response headers, not the trailers. A call that fails before sending any message
puts its status in the headers (the usual case for these statuses) and is counted; a streaming call that fails after
sending messages reports its status in the trailers, after the handler has returned, so it is not counted. Latency is
the time to the response headers, which for a streaming call is not the duration of the stream.

---

## Signals from any client: call signals

Homeostat has ready-made signals for HTTP, gRPC, Entity Framework Core and several client libraries. For any other
client (a vendor SDK, a TCP protocol, an older service without HTTP), record the calls yourself and Homeostat turns
them into the same kind of signal: the failure ratio and the p95 duration of the calls of each detection interval.

```csharp
builder.Services.AddSingleton<GeocodingSdk>();
builder.Services.AddSingleton<Geocoder>();

builder.Services.AddHomeostat(h => h
    .AddCallSignals("geocoding:api", o => o.UnhealthyLatency = TimeSpan.FromSeconds(2))
    .AddCondition("geocoding-failing", c => c
        .ForSource("geocoding:api")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("use-cached-coordinates"))));

public sealed class Geocoder(GeocodingSdk sdk, [FromKeyedServices("geocoding:api")] CallRecorder calls)
{
    public Task<Coordinates> LookupAsync(string address, CancellationToken ct)
        => calls.TrackAsync(token => sdk.GeocodeAsync(address, token), cancellationToken: ct);
}
```

`AddCallSignals(source)` registers a `CallRecorder` (namespace `Homeostat.Calls`) as a **keyed singleton**, with the
source as its key: inject it with `[FromKeyedServices(source)]`, or resolve it with
`sp.GetRequiredKeyedService<CallRecorder>(source)`. Give each dependency its own source. Two ways to record a call:

| Method | Records |
|---|---|
| `TrackAsync(ct => call(ct), isFailure, cancellationToken)` | Runs the call and records its duration; an exception counts as a failure and is rethrown. A cancellation requested through `cancellationToken` is not recorded at all. `isFailure` (optional) decides which exceptions count, for example `ex => ex is not ArgumentException` |
| `Record(failed, duration)` | One call whose outcome you measured yourself; `failed` should mean a failure of the dependency, not a wrong request |

Pass the token by name (`cancellationToken: ct`), as in the example: the second parameter is `isFailure`.
`TrackAsync` has an overload for calls without a result. Recording is thread-safe and cheap: `Record` takes a short
lock and allocates nothing in most calls. To unit test code that records calls, create a recorder yourself:
`new CallRecorder("geocoding:api")`.

At each sample the signal follows the same rules as the [HTTP clients](#http-clients): no calls, no signal; fewer
than `MinimumCalls` and no failures, `Healthy`; fewer than `MinimumCalls` with failures, no signal; otherwise by
failure ratio, raised by p95 latency if latency thresholds are set.

| `CallSignalOptions` | Default | Meaning |
|---|---|---|
| `DegradedFailureRatio` | 0.1 | Failure ratio at or above which the signal is `Degraded` |
| `UnhealthyFailureRatio` | 0.5 | Failure ratio at or above which the signal is `Unhealthy` |
| `DegradedLatency` | none | p95 duration at or above which the signal is at least `Degraded` |
| `UnhealthyLatency` | none | p95 duration at or above which the signal is `Unhealthy` |
| `MinimumCalls` | 10 | Calls needed in an interval to judge the failure ratio |

The signal's `Value` is the failure ratio (0 to 1). Tags: `calls`, `failures`, `failure_ratio`, `p95_ms`; `origin` =
`calls`. The p95 comes from up to 2048 durations per interval, sampled evenly across it.

`Homeostat.Grpc` and `Homeostat.EntityFrameworkCore` are built on call signals. An integration package of your own
can do the same with `services.AddCallSignals(source, origin, configure)` on the `IServiceCollection`, where `origin`
names the integration in the `origin` tag; it needs `AddHomeostat` too. When several registrations use the same
source, the last one wins.

---

## Circuit breakers (Polly v8)

```bash
dotnet add package Homeostat.Polly --prerelease
```

Homeostat publishes the state of the circuit breakers you choose. Enable the integration and attach a
tracked state provider to each circuit breaker:

```csharp
using Homeostat.Polly;   // TrackCircuitBreaker

builder.Services.AddHomeostat(h => h
    .AddCircuitBreakerSignals()
    .AddCondition("forecast-circuit-open", c => c
        .ForSource("forecast-circuit")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("serve-cached-forecast"))));

builder.Services.AddResiliencePipeline("forecast", (pipeline, context) =>
    pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        StateProvider = context.ServiceProvider.TrackCircuitBreaker("forecast-circuit"),
    }));
```

| Circuit state | Signal |
|---|---|
| Closed | `Healthy` |
| HalfOpen | `Degraded` |
| Open, Isolated | `Unhealthy` |

Tags: `state`; `origin` = `circuit-breaker`.

Until its pipeline is built (usually on first use), Polly reports the circuit as Closed, so the signal is `Healthy`.

One source follows one pipeline instance: a pipeline rebuilt on reload replaces the previous one. With keyed or
dynamic pipelines (`AddResiliencePipeline<string, ...>`, a resilience handler that selects a pipeline per authority),
give each instance its own source, for example `TrackCircuitBreaker($"forecast-circuit:{context.PipelineKey}")`;
with one source shared by several instances, only the last one built is reported.

Notes:

- `AddResiliencePipeline` comes from the `Polly.Extensions` package; with HTTP clients the same pattern
  works inside `AddResilienceHandler` (`Microsoft.Extensions.Http.Resilience`), whose context also
  exposes `ServiceProvider`.
- State is sampled at each detection interval: a circuit that opens and closes between two samples is
  not seen. This is intended: Homeostat reacts to circuits that **stay** open.
- Use **one source per circuit breaker**. If several pipelines track the same source (for example keyed
  pipelines), the last one built replaces the others.
- A circuit breaker that keeps opening is a strong hint that retries alone are not enough, which is
  exactly where a healing plan helps.

---

## Metrics

.NET libraries publish their metrics through `System.Diagnostics.Metrics`, the API underneath OpenTelemetry
metrics: ASP.NET Core (`http.server.request.duration`), HttpClient (`http.client.request.duration`), Kestrel,
the runtime, Entity Framework Core, and your own meters. Homeostat can listen to any instrument and turn it into
a signal, **without OpenTelemetry**:

```csharp
.AddMetricSignal("http.client.request.duration", m => m
    .Source("forecast-latency")
    .Where("server.address", "forecast.example")     // only this dependency
    .Statistic(MetricStatistic.P95)
    .DegradedAbove(1)                                 // seconds, the unit of the instrument
    .UnhealthyAbove(3)
    .MinimumCount(20))                                // ignore quiet intervals
```

At every detection interval, the measurements of the interval are reduced to one statistic, compared with the
thresholds and published as a signal whose `Value` is the statistic.

| Statistic | Meaning |
|---|---|
| `Mean`, `Min`, `Max`, `Sum`, `Count` | Over the measurements of the interval |
| `P50`, `P90`, `P95`, `P99` | Percentiles (from up to 4096 sampled measurements) |
| `Rate` | Sum per second: increments of a counter per second |
| `CountRate` | Measurements per second: for example requests per second from a duration histogram |
| `Last` | Latest value: for gauges and observable instruments |
| `Ratio` | Share of measurements matching `RatioOf(...)`: for example failed requests |

| Method | Purpose |
|---|---|
| `FromMeter(name)` | Only the instrument of this meter (by default any meter with an instrument of that name) |
| `Where(tag, value)`, `Where(predicate)` | Only measurements with these tags |
| `RatioOf((value, tags) => ...)` | Uses `Ratio`, counting the matching measurements |
| `Statistic(statistic)` | Statistic compared with the thresholds (default `Mean`) |
| `DegradedAbove`, `UnhealthyAbove`, `DegradedBelow`, `UnhealthyBelow` | Thresholds (at least one is required), inclusive: a value at or above (at or below) the threshold reaches it |
| `MinimumCount(n)` | No signal for intervals with fewer measurements (default 1) |
| `Scale(factor)` | Multiplies the statistic before the thresholds and the published value (default 1), for example `Scale(1.0 / 200)` (a pool of 200 connections) to turn a count into a share, or `Scale(1000)` for milliseconds |
| `Source(name)` | Signal source (default `metric:{instrument}`) |

### Ready-made: server errors and latency

```csharp
.AddServerErrorRateSignal()      // source "http-server:errors": share of 5xx; Degraded ≥ 1%, Unhealthy ≥ 5%
.AddServerLatencySignal()        // source "http-server:latency": p95 in seconds; Degraded ≥ 500 ms, Unhealthy ≥ 2 s
```

Both read ASP.NET Core's `http.server.request.duration` and publish nothing in intervals with fewer than
`MinimumRequests` requests (20 by default). Change the source, the thresholds and the minimum with the options:

```csharp
.AddServerErrorRateSignal(o => { o.DegradedAbove = 0.02; o.UnhealthyAbove = 0.1; })   // ServerErrorRateSignalOptions
.AddServerLatencySignal(o => o.UnhealthyAbove = TimeSpan.FromSeconds(1))            // ServerLatencySignalOptions
```

| Option | Error rate default | Latency default | Meaning |
|---|---|---|---|
| `Source` | `http-server:errors` | `http-server:latency` | Signal source |
| `DegradedAbove` | 0.01 | 500 ms | Share of 5xx responses, or p95 as a `TimeSpan`, from which the signal is `Degraded` |
| `UnhealthyAbove` | 0.05 | 2 s | Same, for `Unhealthy` |
| `MinimumRequests` | 20 | 20 | Intervals with fewer requests publish no signal |

Both types are in the `Homeostat.Metrics` namespace. The latency signal still publishes the p95 in seconds.

Things to know:

- Units are those of the instrument: the OpenTelemetry conventions use **seconds** for durations.
- The first interval after startup is only a baseline and publishes nothing, so rates over a few milliseconds do
  not raise false alarms.
- Observable instruments are read at every detection interval. Observable counters report running totals, which
  Homeostat turns into increments (so `Rate` and `Sum` mean "since the previous interval").
- For up-down counters (such as active requests) and observable up-down counters, `Last` is the current
  **level**; for gauges it is the latest value. For instruments with several series (tag combinations), filter
  with `Where` to the series you mean. At most 10,000 series are tracked per instrument (a tag such as a user or a
  URL could otherwise grow memory without limit): further series are not counted, and the signal's message says so.
  Series that stop reporting are forgotten after 10 intervals.
- Filters and ratio rules run inside the code that records the metric; Homeostat catches their exceptions, so a
  faulty filter only skips measurements. Keep them cheap: they run for every measurement.
- The listener starts with the application; measurements recorded before it are not seen.

### Event counters

Some libraries report through **event counters** rather than metrics, for example the connection pool of
`Microsoft.Data.SqlClient`. `AddEventCounterSignal` turns one into a signal; only the counters are enabled, not the
library's tracing events:

```csharp
// Physical connections opened per second: a storm of reconnects after a failover, or a pool that keeps being cleared.
h.AddEventCounterSignal("Microsoft.Data.SqlClient.EventSource", "hard-connects", o =>
{
    o.Source = "sql-reconnects";
    o.Interval = TimeSpan.FromSeconds(30);
    o.DegradedAbove = 5;
});
```

| `EventCounterSignalOptions` | Default | Meaning |
|---|---|---|
| `Source` | `counter:{eventSource}:{counter}` | Signal source |
| `Interval` | 10 seconds | How often the counter is reported, in whole seconds from 1 second to 1 hour. The event source reports at the shortest interval any listener asks for, so signals can come more often |
| `Transform` | none | Turns the counter value into the signal value, for example a share of a maximum |
| `DegradedAbove`, `UnhealthyAbove`, `DegradedBelow`, `UnhealthyBelow` | none | Thresholds, strict: a value above (below) the threshold reaches it |

Polling counters (a level, such as connections in use) publish their value; incrementing counters (events, such as
connections opened) publish a **rate per second**, so thresholds hold whatever interval the event source reports at.

`AddSqlClientPoolSignal()` (package `Homeostat.Data`) is the ready-made one for SqlClient pool exhaustion, and
`AddNpgsqlPoolSignal()` the one for Npgsql, from its metrics: see [Integrations](integrations.md#pool-exhaustion).

## Writing your own detector

For a value you can fetch with one call, a [probe](integrations.md#probes-any-number-as-a-signal) is shorter:
`h.AddProbe("disk-free", (sp, ct) => ..., p => p.DegradedBelow = 10)`. For anything else, implement
`ISignalDetector` and register it:

```csharp
public sealed class DiskSpaceDetector(TimeProvider time) : ISignalDetector
{
    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        var drive = new DriveInfo(AppContext.BaseDirectory);
        var freeRatio = (double)drive.AvailableFreeSpace / drive.TotalSize;

        var severity = freeRatio < 0.05 ? SignalSeverity.Unhealthy
            : freeRatio < 0.15 ? SignalSeverity.Degraded
            : SignalSeverity.Healthy;

        signals.TryPublish(new Signal("disk", severity, time.GetUtcNow())
        {
            Message = $"{freeRatio:P0} free",
        });

        return ValueTask.CompletedTask;
    }
}
```

```csharp
.AddHomeostat(h => h.AddDetector<DiskSpaceDetector>())
```

Detectors are singletons. Exceptions are logged and the next sample runs as usual. Prefer a detector
over a hand-written background loop: it shares the interval, the logging and the testing support.

A library that has only an `IServiceCollection` (an extension method of your own package, for example) registers a
detector with `services.AddHomeostatDetector<DiskSpaceDetector>()`, the method `AddDetector<T>()` calls. It needs
`AddHomeostat` too, and registering the same detector type twice has no effect.
