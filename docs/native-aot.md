# Trimming and Native AOT

Homeostat's libraries (all but `Homeostat.EntityFrameworkCore` and the tools listed below) are marked
`IsAotCompatible`: they build with the trimming and AOT analyzers on, without
warnings, and serialize JSON with source generation instead of reflection. An application can be published with
`PublishTrimmed` or `PublishAot` and keep self-healing, the endpoints and the dashboard.

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

```csharp
var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddHomeostat(h => h
    .UseFileStore("data/homeostat.json")
    .AddCondition("feed-down", c => c
        .ForSource("feed")
        .Heal(p => p.With<RenewFeedClientAction>())));

var app = builder.Build();
app.MapHomeostatStatus("operations");
app.MapHomeostatControl("operations");
```

Nothing has to be added to the application's `JsonSerializerContext`: the endpoints write their answers with
Homeostat's own source-generated metadata, and problem details go through the application's `IProblemDetailsService`
when it has one (`AddProblemDetails`).

## What is checked

The CI publishes `tests/Homeostat.AotSmoke` with Native AOT and runs it. The publish fails on any trimming or AOT
warning, and the run checks, without reflection or runtime code generation:

- a signal received by the alert endpoint, a condition, a healing plan and its verification;
- the file store, the audit log and signal recording;
- the status, coverage, history, control and dashboard endpoints, including the problem details answers;
- the webhook (with a `JsonNode` payload), Slack, Teams, PagerDuty and Opsgenie notifiers;
- signed requests to the Slack interaction and Teams command endpoints;
- the cache, Kubernetes and Azure actions (compiled and analyzed; they do not run in the smoke test).

## What to know

| Topic | Under trimming or Native AOT |
|---|---|
| Actions, detectors, stores, sinks registered by type (`With<TAction>()`, `AddDetector<T>()`, `UseStore<T>()`) | Work: the type parameters are annotated, so their constructors are kept. |
| `WebhookOptions.CreatePayload` | Return a `JsonNode` (`new JsonObject { ["text"] = e.Summary }`) or the event itself. Anonymous objects need reflection: they work in ordinary applications and fail with a clear error when reflection-based serialization is off. |
| `AzureResourceManager(..., body)` | The body is a `JsonNode`. |
| `RestartHostedService` | Works for services derived from `RestartableBackgroundService` and for plain hosted services. For a plain `BackgroundService` it reads `HostOptions.BackgroundServiceExceptionBehavior` with reflection; if trimming removed that metadata, it assumes the default (`StopHost`) and refuses the restart, which is the safe answer. |
| `Homeostat.EntityFrameworkCore` | Not marked `IsAotCompatible`: it follows EF Core, whose Native AOT support is still experimental. It works in ordinary (JIT) applications. |
| `Homeostat.Cli`, `Homeostat.Templates` | Tools: not meant for trimmed applications. |
| `Homeostat.Aspire.Hosting` | Runs in the Aspire app host, not in the application: not marked AOT compatible. |
| Integrations with client libraries (Redis, Kafka, RabbitMQ, Service Bus, Artemis, MongoDB, gRPC, FeatureManagement, OpenFeature, Polly) | Homeostat's own code is analyzed and annotated; whether the application can be published with Native AOT also depends on the client library. Check its documentation before turning on `PublishAot`. |

The analyzers ship inside the `Homeostat` package and run in the compiler only.
