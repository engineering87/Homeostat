# Homeostat.ServiceBus

Azure Service Bus for Homeostat: the active and dead-letter message counts of queues and subscriptions as signals, and `ServiceBusProcessor` and `ServiceBusSessionProcessor` instances that healing plans can pause and resume, for example while the store they write to is down.

## Install

```bash
dotnet add package Homeostat.ServiceBus --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Homeostat;

var connectionString = builder.Configuration.GetConnectionString("ServiceBus");
builder.Services.AddSingleton(new ServiceBusAdministrationClient(connectionString));   // needs the Manage right
builder.Services.AddSingleton(new ServiceBusClient(connectionString));
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateProcessor("ingest"));

builder.Services.AddHomeostat(h => h
    .AddServiceBusQueueSignals("ingest", o => o.Active.UnhealthyAbove = 10_000)   // servicebus:ingest:active, :dead-letter
    .AddPausableServiceBusProcessor("ingest")
    .AddCondition("archive-db-down", c => c
        .ForSource("archive-db")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.PauseConsumer("ingest"))));                                  // resumes when the condition clears
```

## Documentation

[Azure Service Bus](https://github.com/engineering87/Homeostat/blob/main/docs/messaging.md#azure-service-bus)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
