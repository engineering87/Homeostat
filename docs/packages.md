# Packages

Every package has the same version number and is released together. Start with `Homeostat` (and usually
`Homeostat.HealthChecks`), then add the packages of the technologies you use. While the current version is a release
candidate, install with `--prerelease` (see [Versioning](versioning.md)).

| Package | Adds | Install | Main entry points | Native AOT |
|---|---|---|---|---|
| `Homeostat` | Engine, conditions, plans, guardrails, runtime detectors, starter presets, call signals, probes, metric signals, built-in actions, switches, approvals, file store, audit log, recording; includes the [analyzers](analyzers.md) | `dotnet add package Homeostat --prerelease` | `AddHomeostat`, `AddCondition`, `Heal`, `AddWebApiDefaults`, `AddWorkerDefaults`, `AddCallSignals`, `AddProbe`, `AddMetricSignal`, `UseFileStore`, `AddAuditLog`, `AddSignalRecording`, `AddPauseGate`, `IHomeostatControl`, `IHomeostatSwitches` | Yes |
| `Homeostat.HealthChecks` | Health check results as signals; Homeostat's own health check | `dotnet add package Homeostat.HealthChecks --prerelease` | `AddHealthCheckSignals`, `AddHomeostatCheck` | Yes |
| `Homeostat.AspNetCore` | Status, history, coverage and control endpoints, dashboard, Slack and Teams approvals, alert ingestion | `dotnet add package Homeostat.AspNetCore --prerelease` | `MapHomeostatStatus`, `MapHomeostatControl`, `MapHomeostatDashboard`, `MapHomeostatAlerts`, `MapHomeostatSlackInteractions`, `MapHomeostatTeamsCommands` | Yes |
| `Homeostat.Http` | HTTP client signals (also HTTP-level signals for gRPC clients); webhook, PagerDuty, Opsgenie, Slack and Teams notifiers | `dotnet add package Homeostat.Http --prerelease` | `AddHomeostatSignals` (on `IHttpClientBuilder`), `AddWebhookNotifications`, `AddPagerDutyIncidents`, `AddOpsgenieIncidents`, `AddSlackNotifications`, `AddTeamsNotifications` | Yes |
| `Homeostat.Grpc` | gRPC client signals judged on the gRPC status, streaming calls included | `dotnet add package Homeostat.Grpc --prerelease` | `AddHomeostatGrpcSignals` (on the builder of `AddGrpcClient`), `HomeostatGrpcInterceptor` | Yes |
| `Homeostat.Polly` | Circuit breaker signals, circuit isolation, chaos enabled by switches | `dotnet add package Homeostat.Polly --prerelease` | `AddCircuitBreakerSignals`, `TrackCircuitBreaker`, `CircuitControl`, `IsolateCircuit`, `ChaosEnabledBySwitch` | Yes |
| `Homeostat.Redis` | Redis store and coordinator; Redis client signals | `dotnet add package Homeostat.Redis --prerelease` | `UseRedis`, `AddRedisSignals` | Yes |
| `Homeostat.Data` | Database probes for any ADO.NET provider; SqlClient and Npgsql pool signals; pool reset | `dotnet add package Homeostat.Data --prerelease` | `AddDatabaseProbe`, `AddSqlClientPoolSignal`, `AddNpgsqlPoolSignal`, `ClearConnectionPools<T>` | Yes |
| `Homeostat.EntityFrameworkCore` | Command failures and latency of a `DbContext`; `DbContext` connectivity probe | `dotnet add package Homeostat.EntityFrameworkCore --prerelease` | `AddDbContextSignals<TContext>`, `AddDbContextProbe<TContext>` | No |
| `Homeostat.Caching` | Memory cache hit ratio; HybridCache, distributed and memory cache purge actions | `dotnet add package Homeostat.Caching --prerelease` | `AddMemoryCacheSignals`, `PurgeHybridCacheTags`, `RemoveHybridCacheKeys`, `RemoveDistributedCacheKeys`, `CompactMemoryCache` | Yes |
| `Homeostat.MongoDB` | Ping, primary availability, pool exhaustion, replication lag | `dotnet add package Homeostat.MongoDB --prerelease` | `AddMongoSignals`, `AddHomeostatSignals` (on `MongoClientSettings`) | Yes |
| `Homeostat.ServiceBus` | Queue and subscription backlog and dead letters; pausable processors | `dotnet add package Homeostat.ServiceBus --prerelease` | `AddServiceBusQueueSignals`, `AddServiceBusSubscriptionSignals`, `AddPausableServiceBusProcessor` | Yes |
| `Homeostat.RabbitMQ` | Queue backlog, consumers and dead letters; pausable consumer | `dotnet add package Homeostat.RabbitMQ --prerelease` | `AddRabbitMqQueueSignals`, `PausableRabbitMqConsumer` | Yes |
| `Homeostat.Kafka` | Consumer lag, broker connections and producer queue from client statistics; pause gate | `dotnet add package Homeostat.Kafka --prerelease` | `SetHomeostatSignals`, `AddKafkaPauseGate`, `KafkaPauseGate` | Yes |
| `Homeostat.Artemis` | ActiveMQ Artemis queue depth, consumers and dead letters through Jolokia | `dotnet add package Homeostat.Artemis --prerelease` | `UseArtemis`, `AddArtemisQueueSignals` | Yes |
| `Homeostat.FeatureManagement` | Feature filter driven by switches | `dotnet add package Homeostat.FeatureManagement --prerelease` | `AddHomeostatSwitchFilter` | Yes |
| `Homeostat.OpenFeature` | OpenFeature provider that exposes switches as flags | `dotnet add package Homeostat.OpenFeature --prerelease` | `HomeostatSwitchProvider` | Yes |
| `Homeostat.Kubernetes` | Restart this pod, rollout restart, scale a deployment, cordon the node | `dotnet add package Homeostat.Kubernetes --prerelease` | `UseKubernetes`, `RestartPod`, `RolloutRestart`, `ScaleDeployment`, `CordonNode` | Yes |
| `Homeostat.Azure` | Restart App Service and VMs, Front Door origin failover, any Resource Manager operation | `dotnet add package Homeostat.Azure --prerelease` | `UseAzure`, `RestartAppService`, `RestartVirtualMachine`, `DisableFrontDoorOrigin`, `AzureResourceManager` | Yes |
| `Homeostat.Aspire.Hosting` | Homeostat link and Pause/Resume commands in the Aspire dashboard (app host) | `dotnet add package Homeostat.Aspire.Hosting --prerelease` | `WithHomeostat` | No |
| `Homeostat.Testing` | Test host with simulated time, fault injection, replay | `dotnet add package Homeostat.Testing --prerelease` | `HomeostatTestHost`, `HomeostatReplay`, `HealingContextFactory`, `FaultInjectingHandler` | Yes |
| `Homeostat.Cli` | The `homeostat` command-line tool | `dotnet tool install --global Homeostat.Cli --prerelease` | `homeostat status`, `pause`, `approve`, `switch`, `coverage`, `history`, `replay` | No |
| `Homeostat.Templates` | `dotnet new` templates: `homeostat-webapi`, `homeostat-worker` | `dotnet new install Homeostat.Templates::1.0.0-rc.2` | `dotnet new homeostat-webapi`, `dotnet new homeostat-worker` | No |
| Analyzers | Build-time checks | Bundled in `Homeostat`; not a separate package | See [Analyzers](analyzers.md) | No |

"Native AOT: Yes" means the library is marked `IsAotCompatible` and builds without trimming or AOT warnings. For
packages that wrap a client library, whether the application can be published with Native AOT also depends on that
library: see [Platforms](platforms.md#trimming-and-native-aot).

Most extension methods are in the `Homeostat` namespace, so `using Homeostat;` is enough; option types are in
sub-namespaces named after the package (`Homeostat.Http`, `Homeostat.Kafka`, ...). `WithHomeostat` is in the
`Aspire.Hosting` namespace, like the other app host extensions.
