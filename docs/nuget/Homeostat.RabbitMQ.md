# Homeostat.RabbitMQ

RabbitMQ for Homeostat, through RabbitMQ.Client 7: queue depth, consumer count and dead letters as signals, and a consumer wrapper that healing plans can pause and resume.

## Install

```bash
dotnet add package Homeostat.RabbitMQ --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;
using RabbitMQ.Client;

builder.Services.AddSingleton<IConnection>(_ =>
    new ConnectionFactory { HostName = "rabbitmq" }.CreateConnectionAsync().GetAwaiter().GetResult());

builder.Services.AddHomeostat(h => h
    .AddRabbitMqQueueSignals("thumbnails", o =>     // rabbitmq:thumbnails:messages, :consumers, :dead-letter
    {
        o.Messages.UnhealthyAbove = 50_000;
        o.DeadLetterQueue = "thumbnails.dead-letter";
    })
    .AddCondition("thumbnail-consumers-gone", c => c
        .ForSource("rabbitmq:thumbnails:consumers")
        .SustainedFor(TimeSpan.FromMinutes(1))));   // observe only: no plan
```

To make a consumer pausable, consume through `PausableRabbitMqConsumer` and register it with `IHomeostatConsumers.Register`; see the documentation.

## Documentation

[RabbitMQ](https://github.com/engineering87/Homeostat/blob/main/docs/messaging.md#rabbitmq)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
