# Cloud and orchestrators

This page is for SREs and developers whose application runs on Kubernetes, Azure or a service manager. Some problems
are cleared only by the platform: a fresh process, a new pod, another region. The actions below ask the platform to do
it. They change infrastructure, so **every one of them requires approval by default**; pass `requireApproval: false`
only for plans you have rehearsed (dry-run, [chaos](integrations.md#validate-plans-with-chaos)).

| Need | Package | Stability | Action |
|---|---|---|---|
| Restart this process | `Homeostat` | Stable | `RestartApplication()` |
| Restart this pod, rollout restart, scale, cordon | `Homeostat.Kubernetes` | Stable | `RestartPod()`, `RolloutRestart()`, `ScaleDeployment()`, `CordonNode()` |
| Restart App Service or a VM, Front Door failover, any ARM operation | `Homeostat.Azure` | Preview | `RestartAppService()`, `RestartVirtualMachine()`, `DisableFrontDoorOrigin()`, `AzureResourceManager()` |

Both platform packages call the platform's REST API directly, with no client SDK: no extra dependencies, and the
requests are the documented ones. Their HTTP clients do not log request URLs.

## Restart the application

`RestartApplication()` is in the core package. The step sets the exit code (1 by default) and stops the host
gracefully; the orchestrator or service manager starts a new process (Kubernetes, App Service, systemd
`Restart=on-failure`, Docker `--restart`).

```csharp
.AddCondition("memory-leak", c => c
    .ForSource("memory")
    .WhenRisingFasterThan(0.01, TimeSpan.FromMinutes(30))
    .Heal(p => p
        .CompactMemoryCache(0.5)
        .Then("collect", _ => GC.Collect())
        .RestartApplication()))                // approval, then exit code 1
```

> [!WARNING]
> Without an orchestrator or service manager that restarts the process, the application stops and stays down. Use
> `RestartApplication()` only where something starts it again.

| Parameter of `RestartApplication` | Default | Meaning |
|---|---|---|
| `exitCode` | 1 | Process exit code, so "restart on failure" policies apply too |
| `requireApproval` | `true` | Whether a person approves the restart first |
| `limits` | `RestartLimits.Default` | Minimum uptime and restarts per window |

The limits (`RestartLimits`, shared with `RestartPod`) guard against restart loops:

| `RestartLimits` | Default | Meaning |
|---|---|---|
| `MinimumUptime` | 10 minutes | An instance that has started more recently is not restarted, so a process that fails right after starting does not loop |
| `MaxRestarts` | 3 | Restarts of the whole application allowed per window, across instances and processes; after that the step fails and a person has to look |
| `Window` | 1 hour | Length of the window of `MaxRestarts` |

Change them with a new `RestartLimits`:

```csharp
.RestartApplication(limits: new RestartLimits(TimeSpan.FromMinutes(30), MaxRestarts: 2, Window: TimeSpan.FromHours(6)))
```

Notes:

- Restarts are counted in the store for the whole application, in fixed windows of the configured length (aligned on
  UTC time). A restart is allowed only while the current and the previous window together hold fewer than the limit,
  so no period of one window length ever holds more, even when every replica restarts at the same moment. A refused
  attempt counts only within its own window, so a condition that keeps trying never blocks restarts for good.
- Keep the store persistent (`UseFileStore` on a volume that survives the restart, or `UseRedis`): the in-memory store
  starts empty in the new process, and so would the count, the cooldown and the history. With the in-memory store the
  step logs a warning.
- The step **ends the run**: put it last, steps after it never run. The run is recorded as canceled by the shutdown it
  caused; the restart is logged as a critical message before the application stops.
- The plan state is per instance (`InstanceId`, the machine name by default). A container restarted in place keeps its
  name and its cooldown; a new pod has a new name, which is why the restart count is per application.

### Your own restart action

An action that restarts the application in another way (a platform API, a supervisor) should apply the same guards:
call `RestartGuard.EnsureAllowedAsync` first. It throws `InvalidOperationException` (the step fails, and the plan
escalates or stops) when the instance has been up for less than `MinimumUptime` or the application already
restarted `MaxRestarts` times within the window; otherwise it counts this restart in the store.

```csharp
public sealed class RecycleWorkerProcessAction(SupervisorClient supervisor) : IHealingAction
{
    public async Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        await RestartGuard.EnsureAllowedAsync(context, RestartLimits.Default, cancellationToken);
        await supervisor.RecycleAsync(cancellationToken);   // your code
    }
}
```

## Kubernetes

```bash
dotnet add package Homeostat.Kubernetes --prerelease
```

`Homeostat.Kubernetes` adds steps that restart this pod, restart or scale a deployment, and cordon the node, through
the Kubernetes API with the pod's service account. `UseKubernetes()` configures the connection; inside a pod the
defaults work.

```csharp
builder.Services.AddHomeostat(h => h
    .UseKubernetes()                           // in-cluster defaults
    .AddCondition("pod-degraded", c => c
        .ForSource("threadpool")
        .SustainedFor(TimeSpan.FromMinutes(2))
        .Heal(p => p
            .ScaleDeployment("weather-api", by: 2, maximum: 10)           // more replicas, with approval
            .RestartPod())));                                             // last resort: this pod, with approval; ends the run
```

| Action | Kubernetes call | Permission |
|---|---|---|
| `RestartPod(gracePeriod, requireApproval, limits)` | `GET` the pod, then `POST pods/{this pod}/eviction` with a grace period (30 s by default, in whole seconds, at least 1 s, for example `RestartPod(gracePeriod: TimeSpan.FromSeconds(15))`); the controller starts a new pod. An eviction, so PodDisruptionBudgets apply: replicas restarting together never take the workload down at once (the step fails when the budget allows no disruption). Refused for a pod without a controller, and by the restart limits | `get` on `pods`, `create` on `pods/eviction` |
| `RolloutRestart(deployment)` | `PATCH deployments/{name}` with the `kubectl.kubernetes.io/restartedAt` annotation, like `kubectl rollout restart` | `patch` on `deployments` |
| `ScaleDeployment(deployment, by, maximum, minimum)` | `GET` and `PATCH deployments/{name}/scale`, only in the direction of `by`, within `minimum` (1 by default) and `maximum`. The step fails when the deployment is already at or beyond the limit, so the plan escalates (a deployment already above `maximum` is not scaled in). The update is conditional on the read version: on a concurrent change (`409`, frequent while a rollout is in progress) it reads again and decides again, up to 4 attempts, then the step fails | `get`, `patch` on `deployments/scale` |
| `CordonNode()` | `GET`, then `PATCH nodes/{this node}` `spec.unschedulable: true` (new pods go elsewhere; running pods stay). A node already cordoned is left alone, and the step fails | `get`, `patch` on `nodes` (ClusterRole) |

Steps that take `requireApproval` (`true` by default) use `DefaultApprovalTimeout`; call
`.RequireApproval(timeout)` after the step to change it.

`UndoWithUncordon()` after `CordonNode()` makes the node schedulable again when cordoning did not help. With a
HorizontalPodAutoscaler, change the autoscaler's bounds instead of the deployment: the autoscaler would undo the
change.

`ScaleDeployment` is not reverted when the condition clears: the extra replicas cost money but do not degrade the
application, and scaling in at the wrong moment could. Scale in with a condition of its own (for example a low load
sustained for a while, with `ScaleDeployment("weather-api", by: -1, maximum: 10, minimum: 2)`), or let an autoscaler
own the replica count.

### Connection

Inside a pod nothing needs configuring: the API server comes from `KUBERNETES_SERVICE_HOST`, the token, CA and
namespace from the service account files. The token is read again every minute (projected tokens rotate), and at once
after a `401`. Give the pod its name and node through the downward API (without `POD_NAME` the pod name comes from
`HOSTNAME`, which differs with `spec.hostname` or `hostNetwork`, and `RestartPod` logs a warning):

```yaml
env:
  - name: POD_NAME
    valueFrom: { fieldRef: { fieldPath: metadata.name } }
  - name: NODE_NAME
    valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
```

Outside a cluster (for example from a development machine), set `ApiServer`, `Namespace`, `PodName` or `NodeName` as
needed, and `AccessToken` instead of the token file.

| `KubernetesOptions` | Default | Meaning |
|---|---|---|
| `ApiServer` | `https://{KUBERNETES_SERVICE_HOST}:{KUBERNETES_SERVICE_PORT}` (port 443 when not set) | API server address; must be `https`, except on loopback for local development |
| `TokenPath` | `/var/run/secrets/kubernetes.io/serviceaccount/token` | Bearer token file, read again every minute |
| `AccessToken` | none | Returns the bearer token instead of reading `TokenPath` |
| `CertificateAuthorityPath` | `/var/run/secrets/kubernetes.io/serviceaccount/ca.crt` | CA (PEM) that signs the API server certificate, besides the system trust store |
| `Namespace` | the service account `namespace` file | Namespace of the application |
| `PodName` | `POD_NAME`, then `HOSTNAME` | Name of this pod |
| `NodeName` | `NODE_NAME` | Name of the node running this pod |
| `Timeout` | 30 seconds | Timeout of one API request |

`KubernetesOptions.ServiceAccountDirectory` is the directory of the default files.

Notes:

- TLS is validated against the system trust store or the cluster CA (`ca.crt`, with the intermediates the server
  sends, for server certificates only, with the right name). It is never turned off, and an `http` API server is
  refused, except on loopback.
- Names (deployment, pod, node, namespace) must be valid Kubernetes names, so they cannot change the request target.

### Permissions

Grant only what the actions you use need:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata: { name: homeostat, namespace: weather }
rules:
  - apiGroups: [""]
    resources: [pods]
    verbs: [get]                        # RestartPod
  - apiGroups: [""]
    resources: [pods/eviction]
    verbs: [create]                     # RestartPod (PodDisruptionBudgets apply)
  - apiGroups: [apps]
    resources: [deployments]
    resourceNames: [weather-api]
    verbs: [patch]                      # RolloutRestart
  - apiGroups: [apps]
    resources: [deployments/scale]
    resourceNames: [weather-api]
    verbs: [get, patch]                 # ScaleDeployment
---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata: { name: homeostat, namespace: weather }
subjects: [{ kind: ServiceAccount, name: weather-api, namespace: weather }]
roleRef: { kind: Role, name: homeostat, apiGroup: rbac.authorization.k8s.io }
```

A refused call fails the step with a `KubernetesApiException`, which carries the `StatusCode` and the Kubernetes
message, with a hint about the missing permission.

> [!IMPORTANT]
> Know what these permissions allow if the application itself is compromised:
>
> - `patch` on a deployment allows rewriting its pod template (image, service account): scope it with
>   `resourceNames`, as above, and prefer `ScaleDeployment` (only `deployments/scale`) when it is enough.
> - `create` on `pods/eviction` covers every pod of the namespace; Kubernetes cannot restrict it to the pod itself.
>   Evictions respect PodDisruptionBudgets: define one for the workload.
> - `CordonNode` needs a ClusterRole with `get` and `patch` on `nodes`, which also allows changing labels and taints
>   of every node. Grant it only if you use it, and consider an admission policy (for example a
>   ValidatingAdmissionPolicy) that lets this service account change only `spec.unschedulable`.

## Azure

```bash
dotnet add package Homeostat.Azure --prerelease
```

`Homeostat.Azure` is a preview package. It adds steps that restart an App Service app or a virtual machine, take a
Front Door origin out of rotation, or send any Azure Resource Manager request. `UseAzure` configures how it gets a
Resource Manager token.

```csharp
var credential = new DefaultAzureCredential();       // Azure.Identity: managed identity in Azure
builder.Services.AddHomeostat(h => h
    .UseAzure(o => o.AccessToken = async (services, ct) =>
        (await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct)).Token)
    .AddCondition("region-down", c => c
        .ForSource("alert:WestEuropeApiDown")
        .Heal(p => p
            .RestartAppService("/subscriptions/.../resourceGroups/weather-rg/providers/Microsoft.Web/sites/weather-api-weu")
            .DisableFrontDoorOrigin("/subscriptions/.../providers/Microsoft.Cdn/profiles/weather-fd/originGroups/api/origins/westeurope"))));
```

| Action | Resource Manager call | Role |
|---|---|---|
| `RestartAppService(siteId, softRestart)` | `POST {site or slot}/restart`; `softRestart` (`false` by default) applies the settings and restarts only if needed | Website Contributor |
| `RestartVirtualMachine(vmId)` | `POST {vm}/restart` (asynchronous) | Virtual Machine Contributor |
| `DisableFrontDoorOrigin(originId)` | `GET` the origin and its group, then `PATCH {origin}` `enabledState: Disabled`; enabled again if it did not help. Refused when the origin is already disabled or is the last enabled one of its group | CDN Profile Contributor |
| `AzureResourceManager(name, method, resourceId, action, apiVersion, body)` | Any operation | As required |

| `AzureOptions` | Default | Meaning |
|---|---|---|
| `AccessToken` | required | Returns a Resource Manager token; called for every request, so let the credential cache it |
| `ResourceManager` | `AzureOptions.PublicCloud` (`https://management.azure.com/`) | Endpoint, absolute `https`; change it for sovereign clouds |
| `Timeout` | 60 seconds | Timeout of one request |

Notes:

- Homeostat does not depend on Azure.Identity: `AccessToken` takes the token from the credential you already use,
  which caches and refreshes it.
- Resource ids are checked against the exact resource type (an App Service site or slot, a virtual machine, a Front
  Door origin), and `AzureResourceManager` actions must be plain paths such as `/restart`, without a query string, so a
  mistyped id or action cannot reach another endpoint.
- The optional `body` is a `JsonNode`, for example
  `new JsonObject { ["properties"] = new JsonObject { ["enabledState"] = "Disabled" } }`, so it serializes without
  reflection.
- Long operations answer `202 Accepted`: the step succeeds when Azure accepts the request, and Homeostat's
  verification checks whether the condition clears. An error answer fails the step with a `ResourceManagerException`,
  which carries the `StatusCode` and the Resource Manager error.
- Azure Key Vault secrets loaded with the configuration provider are refreshed with the built-in
  `ReloadConfiguration()` action, for example after a rotation that broke a connection.
- Scaling an App Service plan is not included: the Resource Manager update operation does not change the plan's
  capacity, and the full update would rewrite the plan. Use autoscale rules, or `AzureResourceManager` with an
  operation you have verified.

## AWS

Not included yet: AWS requests need Signature Version 4 and the SDK's credential chain, which Homeostat will not
reimplement. Use an inline action with the AWS SDK in the meantime:

```csharp
.With("force new ECS deployment", async (ctx, ct) =>
    await ctx.Services.GetRequiredService<IAmazonECS>().UpdateServiceAsync(
        new UpdateServiceRequest { Cluster = "weather", Service = "weather-api", ForceNewDeployment = true }, ct))
.RequireApproval()
```

See also: [Guardrails](guardrails.md), [Security](security.md), [Persistence and clusters](persistence-and-clusters.md),
[Integrations](integrations.md).
