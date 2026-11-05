# Homeostat.Artemis

Apache ActiveMQ Artemis for Homeostat: queue depth, consumer count and dead letters as signals, read through the broker's Jolokia management API, so no AMQP or JMS client is needed. Receive loops are paused with the `PauseGate` of the core package.

## Install

```bash
dotnet add package Homeostat.Artemis --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;

builder.Services.AddHomeostat(h => h
    .UseArtemis(o =>
    {
        o.ManagementUrl = new Uri("http://artemis:8161");
        o.BrokerName = "0.0.0.0";                         // <name> in broker.xml
        o.UserName = "monitor";                           // a read-only management user
        o.Password = builder.Configuration["Artemis:MonitorPassword"];
    })
    .AddArtemisQueueSignals("telemetry-ingest", q => q.Messages.UnhealthyAbove = 20_000)   // artemis:telemetry-ingest:messages
    .AddPauseGate("telemetry-ingest")                                                     // awaited by the receive loop
    .AddCondition("archive-db-down", c => c
        .ForSource("archive-db")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.PauseConsumer("telemetry-ingest"))));
```

## Documentation

[Apache ActiveMQ Artemis](https://github.com/engineering87/Homeostat/blob/main/docs/messaging.md#apache-activemq-artemis)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
