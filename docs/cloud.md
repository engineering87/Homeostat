# Cloud and orchestrators

Some problems are cleared only by the platform: a fresh process, a new pod, another region. These actions ask the
platform to do it. They change infrastructure, so **every one of them requires approval by default**; pass
`requireApproval: false` only for plans you have rehearsed (dry-run, [chaos](integrations.md#validate-plans-with-chaos)).

| Need | Package | Action |
|---|---|---|
| Restart this process | `Homeostat` | `RestartApplication()` |
| Restart this pod, rollout restart, scale, cordon | `Homeostat.Kubernetes` | `RestartPod()`, `RolloutRestart()`, `ScaleDeployment()`, `CordonNode()` |
| Restart App Service or a VM, Front Door failover, any ARM operation | `Homeostat.Azure` | `RestartAppService()`, `RestartVirtualMachine()`, `DisableFrontDoorOrigin()`, `AzureResourceManager()` |

Both packages call the platform's REST API directly, with no client SDK: no extra dependencies, and the requests are
the documented ones. Their HTTP clients do not log request URLs.

## Restart the application

```csharp
.AddCondition("memory-leak", c => c
    .ForSource("memory")
    .WhenRisingFasterThan(0.01, TimeSpan.FromMinutes(30))
    .Heal(p => p
        .CompactMemoryCache(0.5)
        .Then("collect", _ => GC.Collect())
        .RestartApplication()))                // approval, then exit code 1
```

The step sets the exit code (1 by default) and stops the host gracefully; the orchestrator or service manager starts a
new process (Kubernetes, App Service, systemd `Restart=on-failure`, Docker `--restart`). Without one, the application
just stops: use it only where something restarts it.

Guards (`RestartLimits`, shared with `RestartPod`):

- **Approval** by default.
- **Minimum uptime** (10 minutes by default): an instance that has just started is not restarted, so a process that
  fails right after starting does not loop.
- **Restarts per window** (3 per hour by default), counted in the store for the whole application, across instances and
  processes: after that the step fails and a person has to look. Keep the store persistent (`UseFileStore` on a volume
  that survives the restart, or `UseRedis`): the in-memory store starts empty in the new process, and so would the
  count, the cooldown and the history.
- The step **ends the run**: put it last, steps after it never run. The run is recorded as cancelled by the shutdown it
  caused; the restart is logged as a critical message before the application stops.
- The plan state is per instance (`InstanceId`, the machine name by default). A container restarted in place keeps its
  name and its cooldown; a new pod has a new name, which is why the restart count is per application.

## Kubernetes

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
| `RestartPod(gracePeriodSeconds, limits)` | `GET` then `DELETE pods/{this pod}`; the controller starts a new pod. Refused for a pod without a controller, and by the restart guards | `get`, `delete` on `pods` |
| `RolloutRestart(deployment)` | `PATCH deployments/{name}` with the `kubectl.kubernetes.io/restartedAt` annotation, like `kubectl rollout restart` | `patch` on `deployments` |
| `ScaleDeployment(deployment, by, maximum, minimum)` | `GET` and `PATCH deployments/{name}/scale`, only in the direction of `by` (a deployment already above `maximum` is not scaled in); conditional on the read version, so a concurrent change fails the step | `get`, `patch` on `deployments/scale` |
| `CordonNode()` | `GET`, then `PATCH nodes/{this node}` `spec.unschedulable: true` (new pods go elsewhere; running pods stay). A node already cordoned is left alone | `get`, `patch` on `nodes` (ClusterRole) |

`UndoWithUncordon()` after `CordonNode()` makes the node schedulable again when cordoning did not help. With a
HorizontalPodAutoscaler, scale the autoscaler's bounds instead of the deployment: the autoscaler would undo the change.

### Connection

Inside a pod nothing needs configuring: the API server comes from `KUBERNETES_SERVICE_HOST`, the token, CA and namespace
from the service account files. The token is read again every minute (projected tokens rotate), and at once after a
`401`. TLS is validated against the system trust store or the cluster CA (`ca.crt`, with the intermediates the server
sends, for server certificates only, with the right name); it is never turned off, and an `http` API server is refused,
except on loopback for local development. Names (deployment, pod, node, namespace) must be valid Kubernetes names, so
they cannot change the request target. Give the pod its name and node through the downward API (without `POD_NAME`
the pod name comes from `HOSTNAME`, which differs with `spec.hostname` or `hostNetwork`):

```yaml
env:
  - name: POD_NAME
    valueFrom: { fieldRef: { fieldPath: metadata.name } }
  - name: NODE_NAME
    valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
```

### Permissions

Grant only what the actions you use need:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata: { name: homeostat, namespace: weather }
rules:
  - apiGroups: [""]
    resources: [pods]
    verbs: [get, delete]                # RestartPod
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

A refused call fails the step with the Kubernetes message and a hint about the missing permission.

Know what these permissions allow if the application itself is compromised:

- `patch` on a deployment allows rewriting its pod template (image, service account): scope it with `resourceNames`,
  as above, and prefer `ScaleDeployment` (only `deployments/scale`) when it is enough.
- `delete` on `pods` covers every pod of the namespace; Kubernetes cannot restrict it to the pod itself.
- `CordonNode` needs a ClusterRole with `get` and `patch` on `nodes`, which also allows changing labels and taints of
  every node. Grant it only if you use it, and consider an admission policy (for example a
  ValidatingAdmissionPolicy) that lets this service account change only `spec.unschedulable`.

## Azure

```csharp
var credential = new DefaultAzureCredential();       // Azure.Identity: managed identity in Azure
builder.Services.AddHomeostat(h => h
    .UseAzure(o => o.AccessToken = async ct =>
        (await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct)).Token)
    .AddCondition("region-down", c => c
        .ForSource("alert:WestEuropeApiDown")
        .Heal(p => p
            .RestartAppService("/subscriptions/.../resourceGroups/weather-rg/providers/Microsoft.Web/sites/weather-api-weu")
            .DisableFrontDoorOrigin("/subscriptions/.../providers/Microsoft.Cdn/profiles/weather-fd/originGroups/api/origins/westeurope"))));
```

| Action | Resource Manager call | Role |
|---|---|---|
| `RestartAppService(siteId, softRestart)` | `POST {site or slot}/restart` (`softRestart`: apply the settings and restart only if needed) | Website Contributor |
| `RestartVirtualMachine(vmId)` | `POST {vm}/restart` (asynchronous) | Virtual Machine Contributor |
| `DisableFrontDoorOrigin(originId)` | `GET` the origin and its group, then `PATCH {origin}` `enabledState: Disabled`; enabled again if it did not help. Refused when the origin is already disabled or is the last enabled one of its group | CDN Profile Contributor |
| `AzureResourceManager(name, method, resourceId, action, apiVersion, body)` | Any operation | As required |

Homeostat does not depend on Azure.Identity: `AccessToken` takes the token from the credential you already use, which
caches and refreshes it. Resource ids are checked against the exact resource type (an App Service site or slot, a
virtual machine, a Front Door origin), and `AzureResourceManager` actions must be plain path segments such as
`/restart`, so a mistyped id or action cannot reach another endpoint. The optional `body` is a `JsonNode`, for example
`new JsonObject { ["properties"] = new JsonObject { ["enabledState"] = "Disabled" } }`, so it serializes without reflection. For sovereign clouds set `ResourceManager`. Long
operations answer `202 Accepted`: the step succeeds when Azure accepts the request, and Homeostat's verification checks
whether the condition clears.

Azure Key Vault secrets loaded with the configuration provider are refreshed with the built-in
`ReloadConfiguration()` action, for example after a rotation that broke a connection.

Plan scaling of App Service is not included: the Resource Manager update operation does not change the plan's
capacity, and the full update would rewrite the plan. Use autoscale rules, or `AzureResourceManager` with an operation
you have verified.

## AWS

Not included yet: AWS requests need Signature Version 4 and the SDK's credential chain, which Homeostat will not
reimplement. Use an inline action with the AWS SDK in the meantime:

```csharp
.With("force new ECS deployment", async (ctx, ct) =>
    await ctx.Services.GetRequiredService<IAmazonECS>().UpdateServiceAsync(
        new UpdateServiceRequest { Cluster = "weather", Service = "weather-api", ForceNewDeployment = true }, ct))
.RequireApproval()
```
