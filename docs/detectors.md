# Detectors

Detectors produce signals **without you writing any code**. They sample part of the application at a
fixed interval and publish one signal per sample, ready to be used by [conditions](conditions.md).

| Detector | Package | Default source | Watches |
|---|---|---|---|
| Thread pool starvation | `Homeostat` | `threadpool` | How long work waits before running |
| Memory pressure | `Homeostat` | `memory` | Memory in use vs. memory available |
| Exception rate | `Homeostat` | `exceptions` | Exceptions thrown per second |
| HTTP clients | `Homeostat.Http` | `http:{client name}` | Failure ratio and p95 latency of each client |
| Circuit breakers | `Homeostat.Polly` | your choice | State of Polly v8 circuit breakers |
| Metrics | `Homeostat` | `metric:{instrument}` or your choice | Any `System.Diagnostics.Metrics` instrument: ASP.NET Core, HttpClient, Kestrel, runtime, your own |
| Health checks | `Homeostat.HealthChecks` | health check name | See [Signals](signals.md#health-checks-as-signals) |
| Probes | `Homeostat` | your choice | Any number or round trip you can fetch: backlogs, lag, free space, pings. See [Integrations](integrations.md#probes-any-number-as-a-signal) |
| Databases | `Homeostat.Data` | your choice | Connection round trip and transient errors of any ADO.NET provider |
| Redis client | `Homeostat.Redis` | `redis` | `PING` round trip and lost connections |
| Memory cache | `Homeostat.Caching` | `memory-cache` | Hit ratio of an `IMemoryCache` |
| Alerts | `Homeostat.AspNetCore` | `alert:{alert name}` | Alertmanager, Grafana and Azure Monitor notifications. See [Integrations](integrations.md#alerts-from-your-monitoring) |

All detectors except health checks (which follow the `AddHealthCheckSignals` period) run every
`DetectionInterval` (default **10 seconds**):

```csharp
.AddHomeostat(h => h.Configure(o => o.DetectionInterval = TimeSpan.FromSeconds(5)))
```

> Remember the rule from [Conditions](conditions.md#how-long-sustainedfor): keep `SustainedFor`
> longer than the detection interval.

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
dotnet add package Homeostat.Http
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
| Fewer than `MinimumRequests`, none failed | `Healthy` (latency is not considered) |
| Fewer than `MinimumRequests`, some failed | None (too few to judge) |
| Otherwise | By failure ratio, raised by p95 latency if thresholds are set |

| Option | Default | Meaning |
|---|---|---|
| `Source` | `"http:{name}"` | Signal source |
| `DegradedFailureRatio` | 0.1 | Failure ratio at or above which the signal is `Degraded` |
| `UnhealthyFailureRatio` | 0.5 | Failure ratio at or above which the signal is `Unhealthy` |
| `DegradedLatency` | none | p95 latency at or above which the signal is at least `Degraded` |
| `UnhealthyLatency` | none | p95 latency at or above which the signal is `Unhealthy` |
| `MinimumRequests` | 10 | Requests needed to judge a ratio |
| `IsFailure` | 5xx, 408, 429 | Which responses count as failures |
| `CountCancellationsAsFailures` | `true` | Whether cancelled requests (including `HttpClient` timeouts) count as failures |

Exceptions always count as failures. Cancelled requests count as failures too, because `HttpClient`
reports its own timeout as a cancellation and the two cannot be told apart from inside the handler;
set `CountCancellationsAsFailures = false` if your callers often cancel requests for unrelated reasons.
Tags: `requests`, `failures`, `failure_ratio`, `p95_ms`; `origin` = `http-client`.

> When traffic stops, no signal is published: a condition stays as it was. If the verification of a
> plan depends on these signals, expect `Inconclusive` outcomes during quiet periods.

---

## Circuit breakers (Polly v8)

```bash
dotnet add package Homeostat.Polly
```

Homeostat publishes the state of the circuit breakers you choose. Enable the integration and attach a
tracked state provider to each circuit breaker:

```csharp
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

No signal is published for a circuit breaker until its pipeline is built, which usually happens on
first use.

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
| `DegradedAbove`, `UnhealthyAbove`, `DegradedBelow`, `UnhealthyBelow` | Thresholds (at least one is required) |
| `MinimumCount(n)` | No signal for intervals with fewer measurements (default 1) |
| `Source(name)` | Signal source (default `metric:{instrument}`) |

### Ready-made: server errors and latency

```csharp
.AddServerErrorRateSignal()      // source "http-server-errors": share of 5xx; Degraded ≥ 1%, Unhealthy ≥ 5%
.AddServerLatencySignal()        // source "http-server-latency": p95 in seconds; Degraded ≥ 0.5, Unhealthy ≥ 2
```

Both read ASP.NET Core's `http.server.request.duration` and publish nothing in intervals with fewer than 20
requests; every threshold is a parameter.

Things to know:

- Units are those of the instrument: the OpenTelemetry conventions use **seconds** for durations.
- The first interval after startup is only a baseline and publishes nothing, so rates over a few milliseconds do
  not raise false alarms.
- Observable instruments are read at every detection interval. Observable counters report running totals, which
  Homeostat turns into increments (so `Rate` and `Sum` mean "since the previous interval").
- For up-down counters (such as active requests) and observable up-down counters, `Last` is the current
  **level**; for gauges it is the latest value. For instruments with several series (tag combinations), filter
  with `Where` to the series you mean.
- Filters and ratio rules run inside the code that records the metric; Homeostat catches their exceptions, so a
  faulty filter only skips measurements. Keep them cheap: they run for every measurement.
- The listener starts with the application; measurements recorded before it are not seen.

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
