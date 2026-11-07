# Homeostat.MongoDB

MongoDB for Homeostat, through MongoDB.Driver 3: ping latency, primary availability, connection pool timeouts and, optionally, replication lag as signals (`mongodb:{name}:ping`, `:primary`, `:pool`, `:replication-lag`). Primary and pool signals come from the driver's own events, so a lost primary is reported at once.

## Install

```bash
dotnet add package Homeostat.MongoDB --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;
using MongoDB.Driver;

builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var settings = MongoClientSettings.FromConnectionString(builder.Configuration.GetConnectionString("archive"));
    settings.AddHomeostatSignals(sp, "archive");   // driver events: primary and pool
    return new MongoClient(settings);
});

builder.Services.AddHomeostat(h => h
    .AddMongoSignals("archive", o => o.Ping.DegradedLatency = TimeSpan.FromMilliseconds(100))   // mongodb:archive:ping
    .AddPauseGate("ingest")                                                                     // awaited by the ingest loop
    .AddCondition("archive-no-primary", c => c
        .ForSource("mongodb:archive:primary")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .Heal(p => p.PauseConsumer("ingest"))));
```

## Documentation

[MongoDB](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#mongodb)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
