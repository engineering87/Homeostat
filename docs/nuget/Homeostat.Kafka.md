# Homeostat.Kafka

Apache Kafka for Homeostat, through Confluent.Kafka: consumer lag, broker connections and the producer queue as signals, read from the client statistics without any extra connection, and a pause gate that lets healing plans pause and resume a consumer safely from its own consume loop.

## Install

```bash
dotnet add package Homeostat.Kafka --prerelease
```

The current version is a release candidate (`1.0.0-rc.2`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Confluent.Kafka;
using Homeostat;

builder.Services.AddSingleton(sp => new ConsumerBuilder<string, byte[]>(new ConsumerConfig
    {
        BootstrapServers = "kafka:9092",
        GroupId = "telemetry",
        StatisticsIntervalMs = 30_000,                   // required: the signals come from the statistics
    })
    .SetHomeostatSignals(sp, "telemetry", o => o.Lag.UnhealthyAbove = 100_000)   // kafka:telemetry:lag, :brokers
    .Build());

builder.Services.AddHomeostat(h => h
    .AddKafkaPauseGate("telemetry")                      // the consume loop calls gate.Apply(consumer)
    .AddCondition("archive-db-down", c => c
        .ForSource("archive-db")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.PauseConsumer("telemetry"))));
```

In the consume loop: `var gate = services.GetRequiredKeyedService<KafkaPauseGate>("telemetry");`, then `gate.Apply(consumer)` before each `Consume`.

## Documentation

[Apache Kafka](https://github.com/engineering87/Homeostat/blob/main/docs/messaging.md#apache-kafka)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
