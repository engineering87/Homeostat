# Configuration

Conditions and plans can live in configuration (`appsettings.json`, environment variables, Azure App
Configuration, Kubernetes ConfigMaps...) instead of code. Operators can then tune thresholds, durations and
steps without a new build, and the changes are applied **while the application runs**.

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
            { "Action": "renew-feed-client" },
            { "Action": "switch-to-backup", "RequireApproval": true, "ApprovalTimeout": "10m", "UndoAction": "switch-to-primary" },
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
    .MapAction<RenewFeedClientAction>("renew-feed-client")
    .MapAction<SwitchToBackupProviderAction>("switch-to-backup")
    .MapAction<SwitchToPrimaryProviderAction>("switch-to-primary")
    .AddConditionsFromConfiguration(builder.Configuration.GetSection("Homeostat:Conditions")));
```

Action classes are code; configuration refers to them by the name given in `MapAction<T>(name)`. Conditions
defined in code and in configuration can be mixed, and can refer to each other (in `SuppressedBy` or in
composites).

## Condition keys

| Key | Same as | Example |
|---|---|---|
| `Source` | `ForSource` | `"weather-feed"` |
| `Severity` | `WhenSeverityAtLeast` | `"Degraded"`, `"Unhealthy"` |
| `ValueAbove`, `ValueBelow` | `WhenValueAbove`, `WhenValueBelow` | `0.9` |

A condition has at most one trigger: `Severity`, `ValueAbove`, `ValueBelow`, `RisingFasterThan`, `FallingFasterThan`,
`AnomalouslyHigh` or `AnomalouslyLow`.

| Key | Same as | Example |
|---|---|---|
| `ClearWhenHealthy` | `ClearWhen(s => s.Severity == Healthy)` | `true` |
| `SustainedFor`, `ClearAfter` | `SustainedFor`, `ClearAfter` | `"30s"`, `"00:02:00"` |
| `SuppressedBy` | `SuppressedBy` | `[ "network-down" ]` or `"a,b"` |
| `RisingFasterThan:PerMinute`, `:Window`, `:MinimumSamples` | `WhenRisingFasterThan` | `0.005`, `"15m"`, `5` (optional) |
| `FallingFasterThan:PerMinute`, `:Window`, `:MinimumSamples` | `WhenFallingFasterThan` | `-`, as above |
| `AnomalouslyHigh:ZScore`, `:Window`, `:MinimumHistory` | `WhenAnomalouslyHigh` | `3`, `"1h"`, `30` (optional) |
| `AnomalouslyLow:ZScore`, `:Window`, `:MinimumHistory` | `WhenAnomalouslyLow` | as above |
| `Occurrences:Count`, `Occurrences:Within` | `Occurrences` | `3`, `"5m"` |
| `AllActive`, `AnyActive` | `WhenAllActive`, `WhenAnyActive` | `[ "db-slow", "cache-down" ]` (no `Source`) |
| `Plan` | `Heal` | see below |

## Plan keys

| Key | Same as |
|---|---|
| `Steps:N:Action` | A mapped action name, `switch:<name>` (with optional `TurnOffWhenCleared`), or `reload-configuration` |
| `Steps:N:RequireApproval`, `Steps:N:ApprovalTimeout` | `RequireApproval(timeout)` |
| `Steps:N:UndoAction` | `UndoWith<T>()` with a mapped action |
| `Cooldown`, `ActionTimeout`, `VerificationTimeout` | Same-name methods |
| `CooldownBackoff:Multiplier`, `CooldownBackoff:Max` | `CooldownBackoff(multiplier, max)` |
| `Scope` | `"Instance"` (default) or `"Cluster"` (`OncePerCluster()`) |

Durations accept `hh:mm:ss` or a number with a unit: `500ms`, `30s`, `5m`, `2h`, `1d` (a bare number is rejected,
so `"30"` cannot silently mean 30 days). Numbers use the invariant culture (`0.9`, not `0,9`). Keys are not
case-sensitive, and **unknown keys are errors**, so a typo such as `SustainFor` does not go unnoticed. Use only one
of `Severity`, `ValueAbove` and `ValueBelow`; composites (`AllActive`, `AnyActive`) take no source or trigger rule.

## Changes at runtime

When the configuration changes (a file is saved, a ConfigMap is updated, App Configuration is refreshed),
Homeostat rebuilds the conditions defined there:

- conditions whose **configuration did not change are not touched**, including the history of their
  `Occurrences`, trend and anomaly rules;
- conditions whose **configuration changed keep their status** (an active condition stays active with its new
  rules), but the history of those rules starts again;
- new conditions start inactive; removed conditions stop being tracked: their pending approvals are withdrawn and
  the switches they turned on are turned off (a run already in progress finishes);
- an **invalid change is logged as an error and ignored**: Homeostat keeps the previous conditions.

At startup, by contrast, an invalid configuration fails fast with the path of the offending key, for example:

```text
Configuration 'Homeostat:Conditions:weather-feed-down:Plan:Steps:0': unknown action 'renew-feed'.
Known actions: switch:<name>, reload-configuration, renew-feed-client, switch-to-backup. Map action classes with MapAction<T>(name).
```

```json
"memory-leak-suspected": {
  "Source": "memory",
  "RisingFasterThan": { "PerMinute": 0.005, "Window": "15m" }
},
"latency-anomaly": {
  "Source": "http-server-latency",
  "AnomalouslyHigh": { "ZScore": 4, "Window": "1h", "MinimumHistory": 60 },
  "SustainedFor": "2m"
}
```

## Global options

`HomeostatOptions` is a plain options class, so it binds like any other:

```csharp
builder.Services.Configure<HomeostatOptions>(builder.Configuration.GetSection("Homeostat:Options"));
```

```json
"Options": { "DryRun": false, "MaxActionsPerWindow": 5, "DefaultCooldown": "00:10:00" }
```

(`Configure` with a section comes from `Microsoft.Extensions.Configuration.Binder`, included in ASP.NET Core
applications. Option values use the `hh:mm:ss` format.)
