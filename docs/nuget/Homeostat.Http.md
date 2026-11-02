# Homeostat.Http

`IHttpClientFactory` integration for Homeostat. One line per client turns its failure ratio and p95 latency into a signal (`http:{client name}`); gRPC clients built with `Grpc.Net.ClientFactory` are covered too, with failed gRPC statuses counted as failures. It also provides the notifiers: a generic webhook, PagerDuty and Opsgenie incidents, and Slack and Microsoft Teams messages, with approval buttons in Slack.

## Install

```bash
dotnet add package Homeostat.Http --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;

builder.Services.AddHttpClient("forecast", c => c.BaseAddress = new Uri("https://forecast.example"))
    .AddHomeostatSignals(o => o.UnhealthyLatency = TimeSpan.FromSeconds(2));   // source "http:forecast"

builder.Services.AddHomeostat(h => h
    .AddCondition("forecast-api-failing", c => c
        .ForSource("http:forecast")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.TurnOnSwitch("serve-cached-forecast", turnOffWhenCleared: true)))
    .AddSlackNotifications(o => o.WebhookUrl = new Uri(builder.Configuration["Slack:WebhookUrl"]!)));
```

Notifiers: [Notifications and audit](https://github.com/engineering87/Homeostat/blob/main/docs/notifications.md).

## Documentation

[HTTP client signals](https://github.com/engineering87/Homeostat/blob/main/docs/detectors.md#http-clients)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
