# Architecture examples

The code of [From architecture to configuration](../../docs/architecture-examples.md): two systems whose teams write
down what they know about them as Homeostat conditions and plans.

| Folder | System | Entry point |
|---|---|---|
| `ForecastApi` | An API with a primary and a backup forecast provider, a shared cache and a geocoder, three instances | `AddForecastApiHealing()` |
| `TelemetryIngestion` | A service that reads device readings from a broker and writes them to a time-series store, two replicas | `AddTelemetryIngestionHealing()` |

The interfaces in the `*Services.cs` files stand for your own code: the clients of the providers, the cache, the store
and the broker. This project is a library: it is not run on its own. `ArchitectureExamplesTests` in
`tests/Homeostat.Tests` starts each configuration on simulated time with fake dependencies and checks every behaviour
the page describes:

```bash
dotnet test tests/Homeostat.Tests --filter "FullyQualifiedName~ArchitectureExamplesTests"
```
