using System.Globalization;
using System.Text.Json.Nodes;
using Homeostat.Healing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat.Kubernetes;

internal static class KubernetesActions
{
    public static async Task RestartPodAsync(HealingContext context, int gracePeriodSeconds, RestartLimits limits, CancellationToken cancellationToken)
    {
        var api = context.Services.GetRequiredService<KubernetesApi>();
        var podPath = $"/api/v1/namespaces/{Segment(api.Namespace)}/pods/{Segment(api.PodName)}";

        // Only a pod that a controller recreates may be deleted: a bare pod would be gone for good.
        var pod = await api.GetAsync(podPath, cancellationToken).ConfigureAwait(false);
        var owners = pod?["metadata"]?["ownerReferences"] as System.Text.Json.Nodes.JsonArray;
        if (owners is null || !owners.Any(o => o?["controller"]?.GetValue<bool>() == true))
        {
            throw new InvalidOperationException(
                $"Pod {api.PodName} has no controller (Deployment, ReplicaSet, StatefulSet): deleting it would not restart it.");
        }

        if (api.Options.PodNameFromHostName)
        {
            Log(context).LogWarning("The pod name comes from HOSTNAME; set POD_NAME with the downward API to be sure it is this pod.");
        }

        await RestartGuard.EnsureAllowedAsync(context, limits, cancellationToken).ConfigureAwait(false);
        Log(context).LogCritical("Deleting pod {Pod} in {Namespace} for condition {Condition}: its controller starts a new one.",
            api.PodName, api.Namespace, context.ConditionName);
        await api.DeleteAsync(
            podPath,
            new JsonObject { ["apiVersion"] = "v1", ["kind"] = "DeleteOptions", ["gracePeriodSeconds"] = gracePeriodSeconds },
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task RolloutRestartAsync(HealingContext context, string deployment, CancellationToken cancellationToken)
    {
        var api = context.Services.GetRequiredService<KubernetesApi>();
        var now = (context.Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        Log(context).LogWarning("Rollout restart of deployment {Deployment} in {Namespace} for condition {Condition}.",
            deployment, api.Namespace, context.ConditionName);

        // What "kubectl rollout restart" does: a new pod template annotation makes the deployment replace its pods.
        await api.PatchAsync(
            $"/apis/apps/v1/namespaces/{Segment(api.Namespace)}/deployments/{Segment(deployment)}",
            new JsonObject
            {
                ["spec"] = new JsonObject
                {
                    ["template"] = new JsonObject
                    {
                        ["metadata"] = new JsonObject
                        {
                            ["annotations"] = new JsonObject
                            {
                                // Sub-second precision: two restarts in the same second still roll out twice.
                                ["kubectl.kubernetes.io/restartedAt"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffK", CultureInfo.InvariantCulture),
                            },
                        },
                    },
                },
            },
            "application/strategic-merge-patch+json",
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task ScaleAsync(HealingContext context, string deployment, int by, int minimum, int maximum, CancellationToken cancellationToken)
    {
        var api = context.Services.GetRequiredService<KubernetesApi>();
        var path = $"/apis/apps/v1/namespaces/{Segment(api.Namespace)}/deployments/{Segment(deployment)}/scale";
        var scale = await api.GetAsync(path, cancellationToken).ConfigureAwait(false);
        var current = scale?["spec"]?["replicas"]?.GetValue<int>() ?? 0;

        // Only in the asked direction: a deployment already above the maximum is not scaled in by a scale-out step.
        var target = by > 0 ? Math.Min(current + by, maximum) : Math.Max(current + by, minimum);
        if (by > 0 ? target <= current : target >= current)
        {
            throw new InvalidOperationException(
                $"Deployment {deployment} has {current} replicas: this step cannot scale it {(by > 0 ? "out" : "in")} within {minimum} to {maximum}.");
        }

        Log(context).LogWarning("Scaling deployment {Deployment} in {Namespace} from {Current} to {Target} replicas for condition {Condition}.",
            deployment, api.Namespace, current, target, context.ConditionName);

        // resourceVersion makes the change conditional: a concurrent change answers 409 instead of being overwritten.
        var resourceVersion = scale?["metadata"]?["resourceVersion"]?.GetValue<string>();
        var patch = new JsonObject { ["spec"] = new JsonObject { ["replicas"] = target } };
        if (resourceVersion is not null)
        {
            patch["metadata"] = new JsonObject { ["resourceVersion"] = resourceVersion };
        }

        await api.PatchAsync(path, patch, "application/merge-patch+json", cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetUnschedulableAsync(HealingContext context, bool unschedulable, CancellationToken cancellationToken)
    {
        var api = context.Services.GetRequiredService<KubernetesApi>();
        if (unschedulable)
        {
            // A node someone already cordoned (for example during a drain) is left alone: cordoning it again would
            // register an uncordon that undoes their work.
            var node = await api.GetAsync($"/api/v1/nodes/{Segment(api.NodeName)}", cancellationToken).ConfigureAwait(false);
            if (node?["spec"]?["unschedulable"]?.GetValue<bool>() == true)
            {
                throw new InvalidOperationException($"Node {api.NodeName} is already cordoned: Homeostat leaves it as it is.");
            }
        }

        Log(context).LogWarning("{Action} node {Node} for condition {Condition}.",
            unschedulable ? "Cordoning" : "Uncordoning", api.NodeName, context.ConditionName);
        await api.PatchAsync(
            $"/api/v1/nodes/{Segment(api.NodeName)}",
            new JsonObject { ["spec"] = new JsonObject { ["unschedulable"] = unschedulable } },
            "application/merge-patch+json",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Names go in URL paths: they are valid Kubernetes names (no '/', no dot segments), and escaped anyway.</summary>
    internal static string Segment(string value) => Uri.EscapeDataString(value);

    private static ILogger Log(HealingContext context)
        => context.Services.GetService<ILoggerFactory>()?.CreateLogger("Homeostat.Kubernetes")
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}
