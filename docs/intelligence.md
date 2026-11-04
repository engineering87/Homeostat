# Learning and foresight

Five features that make Homeostat act earlier, learn from what worked, and show what it would do and what it has
exercised.

| Feature | API | Package |
|---|---|---|
| Heal before a threshold is crossed | `WhenProjectedAbove`, `WhenProjectedBelow` | `Homeostat` |
| Anomalies for the time of day or week | `WhenSeasonallyHigh`, `WhenSeasonallyLow` ([Conditions](conditions.md#seasonal-anomalies-whenseasonallyhigh-and-whenseasonallylow)) | `Homeostat` |
| Run first the step that usually works | `Adaptive()` | `Homeostat` |
| Record signals, replay them against new rules | `AddSignalRecording`, `HomeostatReplay`, `homeostat replay` | `Homeostat`, `Homeostat.Testing`, `Homeostat.Cli` |
| See what was exercised (game days) | `GetCoverageAsync`, `GET /homeostat/coverage`, `homeostat coverage` | `Homeostat`, `Homeostat.AspNetCore`, `Homeostat.Cli` |

## Predictive conditions

A threshold says "it is broken"; a trend says "it is getting worse". A projection says **"it will be broken in two
hours"**: the line fitted to the recent values (least squares over `window`) reaches the threshold within `within`.

```csharp
h.AddCondition("disk-filling", c => c
    .ForSource("disk-used")
    .WhenProjectedAbove(0.95, within: TimeSpan.FromHours(2), window: TimeSpan.FromHours(2))
    .Heal(p => p.With<PruneTempFilesAction>()));

h.AddCondition("workers-draining", c => c
    .ForSource("ingest-workers")
    .WhenProjectedBelow(2, within: TimeSpan.FromMinutes(30), window: TimeSpan.FromMinutes(30))
    .Heal(p => p.ScaleDeployment("ingest", by: 2, maximum: 12)));
```

- It looks at the **projected value**, not just the slope: a fast rise far from the threshold does not trigger, a slow
  one close to it does.
- Like the trend rules, it needs `minimumSamples` values (5 by default) spanning at least half the window, so a short
  burst, such as the warm-up after startup, is not extrapolated.
- Choose a window at least as long as the horizon: extrapolating two hours from five minutes of data is guessing.
- From configuration: `"ProjectedAbove": { "Threshold": 0.95, "Within": "2h", "Window": "2h" }` (and `ProjectedBelow`).
- **Clearing after a fix**: the fitted line still contains the values from before the fix for up to a whole window, so
  waiting for the projection alone would keep the condition active long after it healed. Unless you set `ClearWhen`,
  the condition clears when the current value is on the safe side of the threshold and either the projection is safe
  again or the latest `minimumSamples` values no longer move toward the threshold. A signal without a value never
  clears it.
- The verification of a plan step sees the same rule: set the step's `VerificationTimeout` to at least
  `minimumSamples` signal intervals, so that the latest values can show the new direction before the step is judged.
- Use `ClearAfter` so the condition does not flap while the trend wobbles around the horizon.

## Adaptive plans

When several steps can fix the same problem and none is riskier than the others, let the plan learn which one works:

```csharp
.Heal(p => p
    .With<RenewClientAction>()
    .Then<FlushDnsCacheAction>()
    .Then<ResetConnectionPoolAction>()
    .Then<FailoverAction>().RequireApproval()
    .Adaptive())
```

Before each run, the steps are ordered by how often each one healed this condition in the run history: successes over
attempts, smoothed as (healed + 1) / (attempts + 2), so one lucky run does not decide. Ties keep the declared order.

- **Approval steps are barriers**: steps only move among the steps between two approval steps, so a risky step never
  moves ahead of the cheaper ones before it (above, `FailoverAction` stays last).
- Only steps tried at least `minimumAttempts` times each (5 by default) are reordered, among the positions they
  occupy. A step with fewer attempts, such as a step just added to the plan, keeps its declared position.
- Steps reached only when the ones before them fail are tried less often, so they gain evidence slowly; this is
  intended: the declared order is the starting point, and the plan moves away from it only on evidence.
- The history is the store's run history (`RunHistorySize`, 50 runs by default): with `UseRedis`, every instance
  learns from every other. A changed order is logged.

## Recording and replay

Record the signals Homeostat receives:

```csharp
h.AddSignalRecording(o =>
{
    o.Path = "/var/log/weather-api/signals-{instance}-{date}.jsonl";   // one file per instance and UTC day
    o.Retention = TimeSpan.FromDays(7);                                 // older files are deleted (default 14 days)
    // o.Sources.Add("weather-feed");                                    // only some sources
    // o.IncludeDetails = true;                                          // also messages and tags
});
```

- `{instance}` is the `InstanceId` (the machine name by default): instances that share a volume write separate files,
  and each one deletes only its own old files. The default path is `signals/homeostat-signals-{instance}-{date}.jsonl`.
- Values, severities and sources are always recorded. Messages and tags can carry details from external systems (error
  messages, host names, alert annotations), so they are recorded only with `IncludeDetails = true`.
- On Linux and macOS the files are created readable and writable only by the application's user. They still hold
  operational data: keep them where your logs are, with the same access rules.

Then, in a test or a small console program, replay them against the conditions and plans you want to try: a new
threshold, a new condition, a new plan.

```csharp
var report = await HomeostatReplay.RunFilesAsync(
    Directory.GetFiles("/var/log/weather-api", "signals-*-2026-11-01.jsonl"),
    h => h.AddCondition("feed-down", c => c
        .ForSource("weather-feed")
        .SustainedFor(TimeSpan.FromSeconds(30))              // was 2 minutes: how often would it act?
        .Heal(p => p.With<RenewFeedClientAction>())));

Console.WriteLine(report.ToText());
// Replay of 8640 signals from 2026-11-01 00:00:00Z to 2026-11-01 23:59:50Z
//   feed-down: active 3 times, would heal 3 times (RenewFeedClientAction)
// Timeline:
//   2026-11-01 08:00:30Z  feed-down  Activated  timeout
//   ...
```

A replay **never acts**: it runs in dry-run (enforced after your configuration, so code that turns it off fails the
replay), on simulated time that follows the signal timestamps, with an in-memory store and coordinator, and it removes
detectors, notifiers, the audit log, logging providers and every other hosted service that the configuration registers;
its runs emit no traces or metrics. You can pass the same configuration code as the application.

- `RunFilesAsync` streams the files and merges them in time order, so the files of several days and several instances
  form one timeline, and a week of recordings does not need to fit in memory. The instances' signals then feed the
  same conditions, as with a shared store; to see one instance alone, pass only its files (`signals-web-1-*.jsonl`).
- Conditions with their own options: the `IConfiguration` overloads take the application's `HomeostatOptions`
  (`options: o => o.DefaultCooldown = ...`), since cooldowns and budget change how often a plan would run;
  `homeostat replay` reads them from `Homeostat:Options` of the same file.
- The timeline has `Activated`, `Cleared`, `WouldHeal` (the plan would have run) and `Blocked` (a run started but
  ended without acting: paused, over budget, suppressed). A condition still in its cooldown starts no run, so a
  cooldown shows as fewer `WouldHeal` events, not as `Blocked`.

Conditions kept in configuration can be replayed without the application's code, from a test or from the command line
(no running application needed): action names are accepted as they are, since a replay never executes them.

```csharp
var settings = new ConfigurationBuilder().AddJsonFile("appsettings.Production.json").Build();
var report = await HomeostatReplay.RunFilesAsync(files, settings.GetSection("Homeostat:Conditions"));
```

```bash
homeostat replay /var/log/weather-api --config appsettings.Production.json --max-heals 3
```

The report has the timeline (`Events`), a summary per condition and the dry-run results (`Runs`), so it fits in an
assertion too:

```csharp
Assert.All(report.Conditions, c => Assert.True(c.WouldHeal <= 3, $"{c.Condition} would act too often"));
```

To work with the files yourself, `RecordedSignal.Read(path)` streams the records of one file, and `ToSignal()` turns a
record back into a `Signal`. `HomeostatReplay.RunAsync` takes any sequence of records, for example a filtered one:

```csharp
var records = RecordedSignal.Read("signals-web-1-2026-11-01.jsonl")
    .Where(r => r.Source == "weather-feed");
var report = await HomeostatReplay.RunAsync(records, h => h.AddCondition(/* ... */));
```

| `SignalRecordingOptions` | Default | Meaning |
|---|---|---|
| `Path` | `signals/homeostat-signals-{instance}-{date}.jsonl` | File path; `{instance}` and `{date}` (UTC) are replaced |
| `Retention` | 14 days | How long files are kept, rounded up to whole days; older files of this instance are deleted; `TimeSpan.Zero` keeps everything |
| `Sources` | empty: every source | Sources to record |
| `IncludeDetails` | `false` | Also records messages and tags |
| `BufferCapacity` | 10000 | Signals queued for writing; the oldest are dropped when full |

The recording writes in the background and drops the oldest signals if the disk falls behind
(`BufferCapacity`). While the application stops, it writes what is already queued, within the host's shutdown timeout;
signals still queued when the timeout expires, or when the process is killed, are lost.

## Coverage

After a game day (chaos enabled with switches, see [Integrations](integrations.md#validate-plans-with-chaos)), ask what
was actually exercised:

```bash
homeostat coverage --last 4h --fail-below 0.8
```

```text
Coverage since 2026-11-02 09:00: 67% of plan steps ran

  Condition        Activations  Runs  Outcomes
  feed-down                  3     3  Healed 2, NotHealed 1
                                       never ran: failover
  disk-full                  0     0
Never activated: disk-full
```

| Field | Meaning |
|---|---|
| `Activations` | Times the condition became active, from the condition history: with the built-in stores, on every instance sharing the store and across restarts; otherwise on the instance that answers, since it started |
| `Runs`, `Outcomes` | Runs of its plan, from the run history (all instances with a shared store) |
| `StepsRun` | Plan steps whose action completed at least once |
| `StepsFailed` | Plan steps that were tried but never completed: tried, but not shown to work |
| `StepsNeverRun` | Plan steps that were never tried: escalation steps the earlier steps made unnecessary, or untested ones |
| `Truncated` | The run history or the condition history does not reach back to the start of the period, so older entries are missing |
| `NeverActivated`, `PlansNeverRun` | What the experiments did not reach |
| `StepCoverage` | Share of plan steps that completed at least once (failed steps are not covered) |

In code, the report is a `CoverageReport` from the `Homeostat.Diagnostics` namespace:

```csharp
using Homeostat.Diagnostics;

CoverageReport report = await control.GetCoverageAsync(since: timeProvider.GetUtcNow().AddHours(-4));
foreach (var condition in report.Conditions.Where(c => c.StepsNeverRun.Count > 0))
{
    Console.WriteLine($"{condition.Name}: never ran {string.Join(", ", condition.StepsNeverRun)}");
}
```

The same report is `GET /homeostat/coverage?last=04:00:00` (or `?since=2026-11-02T09:00:00Z`; default the last 24
hours), mapped with `MapHomeostatStatus`, and `IHomeostatControl.GetCoverageAsync(since)` in code. With
`--fail-below`, the CLI exits with code 4 when the step coverage is lower, so a pipeline can fail a game day that
missed too much. The run history keeps the latest `RunHistorySize` runs (50 by default): for a busy game day, raise it,
and `ConditionHistorySize` (200 events by default), or check `Truncated`, which the CLI and the dashboard report as a
warning. The dashboard shows the coverage of the last 24 hours.
