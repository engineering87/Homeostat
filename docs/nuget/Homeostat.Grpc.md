# Homeostat.Grpc

gRPC client integration for Homeostat. One line per client of the gRPC client factory (`Grpc.Net.ClientFactory`) turns the failure ratio of its calls, and the p95 duration of its unary calls, into a signal (`grpc:{client name}`). Calls are judged on their gRPC status, including the status a streaming call reports in its trailers, so failures that gRPC carries inside HTTP 200 responses count. Errors in the request (`NotFound`, `InvalidArgument`, ...) and calls canceled by the caller do not. For channels built without the client factory, `HomeostatGrpcInterceptor` does the same through `channel.Intercept(...)`.

## Install

```bash
dotnet add package Homeostat.Grpc --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;

builder.Services.AddGrpcClient<Forecasts.ForecastsClient>(o => o.Address = new Uri("https://forecasts:5001"))
    .AddHomeostatGrpcSignals(o => o.UnhealthyLatency = TimeSpan.FromSeconds(2));   // source "grpc:ForecastsClient"

builder.Services.AddHomeostat(h => h
    .AddCondition("forecasts-failing", c => c
        .ForSource("grpc:ForecastsClient")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p.TurnOnSwitch("serve-cached-forecast", turnOffWhenCleared: true))));
```

Use it instead of the HTTP-level `AddHomeostatSignals` of Homeostat.Http on the same client: that one sees only the response headers.

## Documentation

[gRPC clients](https://github.com/engineering87/Homeostat/blob/main/docs/integrations.md#grpc-clients)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
