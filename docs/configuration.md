# Configuration

Conditions and plans can live in configuration (`appsettings.json`, environment variables, Azure App
Configuration, Kubernetes ConfigMaps...) instead of code. Operators can then tune thresholds, durations and steps
without a new build, and Homeostat applies the changes **while the application runs**. This page lists the keys, how
changes are applied and how to bind `HomeostatOptions`.

## Example

```json
{
  "Homeostat": {
    "Conditions": {
      "weather-feed-down": {
        "Source": "weather-feed",
        "Severity": "Unhealthy",
        "SustainedFor": "30s",
        "ClearAfter": "2m",
        "SuppressedBy": [ "network-down" ],
        "Plan": {
          "Steps": [
            { "Action": "renew-feed-client", "ActionTimeout": "10s" },
            { "Action": "switch-to-backup", "RequireApproval": true, "ApprovalTimeout": "10m", "UndoAction": "switch-to-primary", "VerificationTimeout": "45s" },
            { "Action": "switch:serve-cached-forecast", "TurnOffWhenCleared": true }
          ],
          "Cooldown": "5m",
          "CooldownBackoff": { "Multiplier": 2, "Max": "1h" },
          "Scope": "Cluster"
        }
      },
      "network-down": {
        "Source": "network",
        "Occurrences": { "Count": 3, "Within": "5m" }
      }
    }
  }
}
```

```csharp
builder.Services.AddHomeostat(h => h
    .AddAction<RenewFeedClientAction>("renew-feed-client")
    .AddAction<SwitchToBackupProviderAction>("switch-to-backup")
    .AddAction<SwitchToPrimaryProviderAction>("switch-to-primary")
    .AddConditionsFromConfiguration(builder.Configuration.GetSection("Homeostat:Conditions")));
```

Action classes are code; configuration refers to them by the name given in `AddAction<T>(name)`, which registers the
class as transient. Action names cannot contain `:`, which is reserved for built-in actions such as `switch:<name>`.
Conditions defined in code and in configuration can be mixed, and can refer to each other (in `SuppressedBy` or in
composites).

## Condition keys

| Key | Same as | Example |
|---|---|---|
| `Source` | `ForSource` | `"weather-feed"` |
| `Severity` | `WhenSeverityAtLeast` | `"Degraded"`, `"Unhealthy"` |
| `ValueAbove`, `ValueBelow` | `WhenValueAbove`, `WhenValueBelow` | `0.9` |

A value rule is cleared only by a signal whose value is back on the safe side: a signal without a value (a probe that
failed or timed out) neither triggers nor clears it.

A condition has at most one trigger: `Severity`, `ValueAbove`, `ValueBelow`, `RisingFasterThan`, `FallingFasterThan`,
`AnomalouslyHigh`, `AnomalouslyLow`, `ProjectedAbove`, `ProjectedBelow`, `SeasonallyHigh` or `SeasonallyLow`.

| Key | Same as | Example |
|---|---|---|
| `ClearWhenHealthy` | `ClearWhen(s => s.Severity == Healthy)` | `true` |
| `SustainedFor`, `ClearAfter` | `SustainedFor`, `ClearAfter` | `"30s"`, `"00:02:00"` |
| `SuppressedBy` | `SuppressedBy` | `[ "network-down" ]` or `"a,b"` |
| `Group` | `InGroup` | `"reports"` (one name: not empty, at most 200 characters, no control characters) |
| `RisingFasterThan:PerMinute`, `:Window`, `:MinimumSamples` | `WhenRisingFasterThan` | `0.005`, `"15m"`, `5` (optional) |
| `FallingFasterThan:PerMinute`, `:Window`, `:MinimumSamples` | `WhenFallingFasterThan` | `-`, as above |
| `AnomalouslyHigh:ZScore`, `:Window`, `:MinimumHistory` | `WhenAnomalouslyHigh` | `3`, `"1h"`, `30` (optional) |
| `AnomalouslyLow:ZScore`, `:Window`, `:MinimumHistory` | `WhenAnomalouslyLow` | as above |
| `ProjectedAbove:Threshold`, `:Within`, `:Window`, `:MinimumSamples` | `WhenProjectedAbove` | `0.95`, `"2h"`, `"2h"`, `5` (optional) |
| `ProjectedBelow:Threshold`, `:Within`, `:Window`, `:MinimumSamples` | `WhenProjectedBelow` | as above |
| `SeasonallyHigh:ZScore`, `:Seasonality`, `:Bucket`, `:TimeZone`, `:MinimumPeriods` | `WhenSeasonallyHigh` | `4`, `"Daily"` or `"Weekly"`, `"15m"` (optional), `"Europe/Rome"` (optional, an IANA or Windows id), `3` (optional) |
| `SeasonallyLow:ZScore`, `:Seasonality`, `:Bucket`, `:TimeZone`, `:MinimumPeriods` | `WhenSeasonallyLow` | as above |
| `Occurrences:Count`, `Occurrences:Within` | `Occurrences` | `3`, `"5m"` |
| `AllActive`, `AnyActive` | `WhenAllActive`, `WhenAnyActive` | `[ "db-slow", "cache-down" ]` (no `Source`) |
| `Plan` | `Heal` | see below |

## Plan keys

| Key | Same as |
|---|---|
| `Steps:N:Action` | A mapped action name, `switch:<name>` (`TurnOnSwitch(name)`, with optional `TurnOffWhenCleared` and `HoldAtMost`, a duration), or `reload-configuration` (`ReloadConfiguration()`) |
| `Steps:N:RequireApproval`, `Steps:N:ApprovalTimeout` | `RequireApproval(timeout)`; `ApprovalTimeout` needs `"RequireApproval": true` |
| `Steps:N:UndoAction` | `UndoWith<T>()` with a mapped action |
| `Steps:N:ActionTimeout`, `Steps:N:VerificationTimeout` | `ActionTimeout(t)`, `VerificationTimeout(t)` after the step |
| `Cooldown` | `Cooldown(t)` |
| `CooldownBackoff:Multiplier`, `CooldownBackoff:Max` | `CooldownBackoff(multiplier, max)` |
| `Scope` | `"Instance"` (default) or `"Cluster"` (`OncePerCluster()`) |
| `Adaptive` | `true` (`Adaptive()`), or `{ "MinimumAttempts": 10 }` (`Adaptive(10)`) |

`ActionTimeout` and `VerificationTimeout` belong to each step, as in code: on the plan object they are unknown keys.
`TurnOffWhenCleared` is `true` unless set, as in code (`TurnOnSwitch(name)`): the switch is turned off when the
condition clears. Write `"TurnOffWhenCleared": false` to leave it on until someone turns it off. `TurnOffWhenCleared`
and `HoldAtMost` are accepted only on a `switch:<name>` step, and `HoldAtMost` only when the switch is turned off when
the condition clears.

Durations accept `hh:mm:ss` or a number with a unit: `500ms`, `30s`, `5m`, `2h`, `1d` (a bare number is rejected,
so `"30"` cannot silently mean 30 days). Numbers use the invariant culture (`0.9`, not `0,9`). Keys are not
case-sensitive, and **unknown keys are errors**, so a typo such as `SustainFor` does not go unnoticed. A composite
(`AllActive` or `AnyActive`, not both) takes no `Source`, trigger, `ClearWhenHealthy` or `Occurrences`.

## Changes at runtime

When the configuration changes (a file is saved, a ConfigMap is updated, App Configuration is refreshed),
Homeostat rebuilds the conditions defined there:

- conditions whose **configuration did not change are not touched**, including the history of their
  `Occurrences`, trend and anomaly rules;
- conditions whose **configuration changed keep their status** (an active condition stays active with its new
  rules; a changed `Group` moves it to the new group, whose guardrails apply from its next run), but the history of
  those rules starts again, except the baseline of a seasonal rule whose cycle (period,
  slot and time zone) did not change, which is kept;
- new conditions start inactive; removed conditions stop being tracked: their pending approvals are withdrawn,
  the switches they turned on are turned off (on this instance, and in the store for application switches), and a run
  already in progress stops before its next step;
- an **invalid change is logged as an error and ignored**: Homeostat keeps the previous conditions.

At startup, an invalid configuration is logged as **critical**, with the path of the offending key, and its
conditions are left out: the conditions defined in code still apply, and the application starts. This way a typo pushed
to a ConfigMap or to App Configuration does not stop every new instance (a scale-out, a restart, Homeostat's own
`RestartPod`) from starting. Pass `failOnInvalid: true` to fail the startup instead, in development and in tests:
`AddConditionsFromConfiguration(section, failOnInvalid: builder.Environment.IsDevelopment())`. A typical message:

```text
Configuration 'Homeostat:Conditions:weather-feed-down:Plan:Steps:0': unknown action 'renew-feed'.
Known actions: switch:<name>, reload-configuration, renew-feed-client, switch-to-backup, switch-to-primary. Map action classes with AddAction<T>(name).
```

## Trend and anomaly rules

A trend rule and an anomaly rule in configuration. `MinimumSamples` defaults to 5 and `MinimumHistory` to 30, as in
code:

```json
"memory-leak-suspected": {
  "Source": "memory",
  "RisingFasterThan": { "PerMinute": 0.005, "Window": "15m" }
},
"latency-anomaly": {
  "Source": "http-server:latency",
  "AnomalouslyHigh": { "ZScore": 4, "Window": "1h", "MinimumHistory": 60 },
  "SustainedFor": "2m"
}
```

## Global options

`HomeostatOptions` is a plain options class, so it binds like any other. Keep the options in `Homeostat:Options`,
next to `Homeostat:Conditions`: it is also where `homeostat replay` looks for them by default (`--options-section`).
`HomeostatReplay` takes them as a callback instead (`o => section.Bind(o)`).

```csharp
builder.Services.Configure<HomeostatOptions>(builder.Configuration.GetSection("Homeostat:Options"));
```

```json
{
  "Homeostat": {
    "Options": { "DryRun": false, "MaxActionsPerWindow": 5, "DefaultCooldown": "00:10:00" },
    "Conditions": { }
  }
}
```

(`Configure` with a section comes from `Microsoft.Extensions.Configuration.Binder`, included in ASP.NET Core
applications. Option values use the `hh:mm:ss` format.)

The guardrails of a [healing group](guardrails.md#healing-groups) bind the same way, through `ConfigureGroup` (which
also registers the group):

```csharp
h.ConfigureGroup("reports", g => builder.Configuration.GetSection("Homeostat:Groups:reports").Bind(g));
```

The `dotnet new` templates read a single key instead, `Homeostat:DryRun`, with
`.Configure(o => o.DryRun = builder.Configuration.GetValue("Homeostat:DryRun", true))`. When you start binding the
whole `Homeostat:Options` section, move the setting to `Homeostat:Options:DryRun` and remove that line, so there is
one place that decides dry-run. Both kinds of configuration run in registration order: the one registered last
wins.

See also: [Conditions](conditions.md), [Healing plans](healing-plans.md), [Command-line tool](tools.md#command-line-tool).
