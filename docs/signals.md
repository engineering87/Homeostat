# Signals

A signal is an observation about one part of your application. Homeostat reacts only to signals, so
choosing good signals is the first step to useful self-healing.

```csharp
public sealed record Signal(string Source, SignalSeverity Severity, DateTimeOffset Timestamp)
{
    public double? Value { get; init; }      // a measurement, for value, trend and anomaly rules
    public string? Message { get; init; }
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}
```

`Value` is optional. Detectors and probes fill it (a latency, a ratio, a queue length); conditions read it with
`WhenValueAbove`, trends, projections and anomalies (see [Conditions](conditions.md#values-trends-and-anomalies)).

## Severities

| Severity | Use it when |
|---|---|
| `Healthy` | The component works as expected |
| `Degraded` | It works, but not at full capacity (for example on a fallback) |
| `Unhealthy` | It does not work |

Severities are ordered (`Healthy < Degraded < Unhealthy`), so conditions can use thresholds such as
"at least `Degraded`".

> Most applications don't need to publish signals by hand: [detectors](detectors.md) watch the
> thread pool, memory, exceptions, HTTP clients and circuit breakers for you.

## Health checks as signals

With the `Homeostat.HealthChecks` package (`dotnet add package Homeostat.HealthChecks --prerelease`), every
registered health check becomes a signal source:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<WeatherFeedHealthCheck>("weather-feed")
    .AddCheck<AirQualityApiHealthCheck>("air-quality");

builder.Services.AddHomeostat(/* ... */)
    .AddHealthCheckSignals(period: TimeSpan.FromSeconds(10));
```

| Health check | Signal |
|---|---|
| Name (`"weather-feed"`) | `Source` |
| `Healthy` / `Degraded` / `Unhealthy` | Same `Severity` |
| `Description` (or the exception message) | `Message` |
| Duration | Tag `duration_ms`; tag `origin` = `health-check` |

Notes:

- `AddHealthCheckSignals` calls `AddHealthChecks()` for you and plugs into the framework's periodic
  health check publisher.
- Without `period`, the framework default applies: every **30 seconds**, after an initial delay of
  5 seconds.
- `period` sets `HealthCheckPublisherOptions.Period`, which is shared by **every** `IHealthCheckPublisher` of the
  application: other publishers (for example one that pushes results to a monitoring service) run at the same
  period. Configure `HealthCheckPublisherOptions` yourself instead if they must differ; the last configuration wins.
- Keep the period **shorter than the `SustainedFor`** of your conditions (see [Conditions](conditions.md)).

## Naming sources

A source names one component that you would fix independently. Built-in sources follow one convention, and your own
sources are easier to read in the dashboard and in alerts when they follow it too:

| Kind | Pattern | Examples |
|---|---|---|
| Integrations with a technology | `{technology}[:{name}][:{metric}]` | `redis`, `http:forecast`, `cache:memory`, `sqlclient:pool`, `npgsql:pool`, `http-server:errors`, `http-server:latency`, `rabbitmq:thumbnails:dead-letter`, `mongodb:archive:primary`, `alert:DiskAlmostFull` |
| The process's own runtime detectors | a bare name | `threadpool`, `memory`, `exceptions` |
| Health checks | the health check name | `weather-feed` |
| Your probes and signals | your choice; a bare name or the pattern above | `ingest-backlog`, `reports-db` |

Sources are case-sensitive. Every built-in default can be changed (a `source` parameter or a `Source` option), and
the default names are part of the public contract: they change only in a major version (see
[Versioning](versioning.md)).

## Publishing your own signals

Inject `ISignalPublisher` anywhere: a background worker, a message consumer, a metric listener.

```csharp
public sealed class ThumbnailWorker(ISignalPublisher signals) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pending = CountPendingImages();   // your own logic

            signals.TryPublish(
                source: "thumbnail-queue",
                severity: pending > 5_000 ? SignalSeverity.Unhealthy : SignalSeverity.Healthy,
                message: $"pending={pending}");

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
```

The short overload stamps the signal with the current time. To add a value or tags, create the record yourself:

```csharp
signals.TryPublish(new Signal("thumbnail-queue", SignalSeverity.Degraded, timeProvider.GetUtcNow())
{
    Value = pending,
    Message = "backlog growing",
    Tags = new Dictionary<string, string> { ["pending"] = pending.ToString() },
});
```

## Tips for good signals

- **Publish healthy signals too**, not only failures. They are what clears a condition and what
  verification looks at.
- **Publish regularly.** Conditions and verification reason about time; a source that goes silent
  leads to `Inconclusive` verification.
- **One source per component** you would fix independently.
- **Prefer symptoms users feel** (cannot fetch data, backlog growing) over internal details.

## Delivery semantics

- `TryPublish` never blocks. Signals go into an in-memory buffer of `SignalBufferCapacity` items
  (default 1024).
- When the buffer is full, the **oldest** pending signal is dropped: fresh information wins.
- `TryPublish` returns `false` only while the application is shutting down.
- Signals are in-process: each instance of your application has its own buffer.
