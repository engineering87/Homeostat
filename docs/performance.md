# Performance

What Homeostat costs an application, measured with `benchmarks/Homeostat.Benchmarks`: a console program without
external dependencies that runs each case after a warm-up, several times, and reports the median.

```bash
dotnet run -c Release --project benchmarks/Homeostat.Benchmarks            # 7 runs per case, about 30 seconds
dotnet run -c Release --project benchmarks/Homeostat.Benchmarks -- --quick # 3 shorter runs
```

## Results

Three complete runs on a small cloud VM (2 logical cores of an Intel Xeon at 2.10 GHz, Ubuntu 24.04, .NET 10.0.14,
workstation GC). The range is the lowest and highest median of the three runs. On two cores the engine thread
competes with the measuring thread, so the absolute figures vary from run to run; the allocations do not.

| Case | Time | Allocated |
|---|---:|---:|
| `TryPublish(signal)` | 130–240 ns | 0 B |
| `TryPublish(source, severity)` (creates the signal) | 350–390 ns | 80 B |
| Engine, 1 condition, severity rule | 230–280 ns per signal | 0 B |
| Engine, 10 conditions, severity rule | 220–370 ns per signal | 0 B |
| Engine, 100 conditions, severity rule | 200–220 ns per signal | 0 B |
| Engine, 100 conditions, value threshold | 200–340 ns per signal | 0 B |
| Engine, 100 conditions, trend over 5 minutes | 490–530 ns per signal | 0 B |
| Engine, 100 conditions, anomaly over 5 minutes | 280–370 ns per signal | 0 B |
| `HttpClient.SendAsync` to a stub handler, without Homeostat | 1.1–1.3 µs | 1,544 B |
| `HttpClient.SendAsync` to a stub handler, with `AddHomeostatSignals()` | 1.3–1.4 µs | 1,616 B |

How to read them:

- **Publishing** is a write to a bounded in-memory channel: tens to a few hundred nanoseconds, nothing allocated when
  the signal already exists. Publishing never blocks: when the buffer is full the oldest signal is dropped.
- **The engine** handles a few million signals per second on one thread, and the cost per signal does not grow with
  the number of conditions: a signal is offered only to the conditions that listen to its source (and to those with a
  custom filter only). Trend, projection and anomaly rules keep running sums over their window, so their cost does not
  grow with the number of points in it either. None of this allocates in steady state.
- **The HTTP handler** adds about a tenth of a microsecond and 72 bytes to a request, against a stub that answers at
  once; next to a real network call (hundreds of microseconds at least) it is not measurable.

## Memory

What Homeostat keeps does not grow with the traffic, the signal rate or the cardinality of what it observes: every
structure that grows with its input has a limit, and `ResourceBoundsTests` checks that retained memory stays flat over
a million signals per source.

| Structure | Limit |
|---|---|
| Signal buffer | `SignalBufferCapacity` signals (1,024); the oldest is dropped |
| Window of a trend, projection or anomaly rule | About 8,192 points (about 128 KB); a faster source keeps the latest value of each interval of window / 8,192 |
| Latency samples of a call recorder, an HTTP client or a metric | 4,096 per interval (reservoir sampling) |
| Series (tag combinations) of an observable metric | 10,000 per instrument; series that stop reporting are forgotten |
| External alerts | 1,000 sources, 1,000 firing series per source and 10,000 in all |
| Run and condition history | `RunHistorySize` (50) and `ConditionHistorySize` (200) |
| Seasonal baselines | A few running sums per time slot, at most 2,016 slots (a week of 5-minute slots) per condition |
| Actions abandoned at their timeout | One per step: an action still running is not started again |

Everything else is sized by your configuration (conditions, plans, detectors, switches, consumers), not by the load.

## Method

- Each engine case starts a host with the conditions, publishes 200,000 signals one millisecond apart (so windows
  hold what they would in production), and stops the clock when a final signal activates a marker condition, which an
  observer reports. Signals are processed in order, so the marker comes last.
- Allocations are `GC.GetAllocatedBytesForCurrentThread` for the publishing cases and
  `GC.GetTotalAllocatedBytes(precise: true)` for the others, divided by the number of operations; the signals
  themselves are created before the clock starts.
- The HTTP cases send 500,000 requests through `IHttpClientFactory` clients that differ only by
  `AddHomeostatSignals()`. The first client measured in a process is slower whichever it is, so one pass is discarded.

Run the program on your own hardware for figures that apply to you, and compare runs on the same machine.
