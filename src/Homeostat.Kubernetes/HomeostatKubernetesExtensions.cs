using Homeostat.Healing;
using Homeostat.Kubernetes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Homeostat;

/// <summary>Kubernetes actions. Each one requires approval by default: pass <c>requireApproval: false</c> to opt out.</summary>
public static class HomeostatKubernetesExtensions
{
    /// <summary>
    /// Lets healing plans act on Kubernetes with the pod's service account. Inside a pod the defaults need no
    /// configuration; grant the service account only the permissions of the actions you use (see docs/cloud.md).
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Changes the API server, token, CA, namespace, pod or node.</param>
    public static HomeostatBuilder UseKubernetes(this HomeostatBuilder builder, Action<KubernetesOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<KubernetesOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.PostConfigure(o => o.Resolve())
            .Validate(o => o.ApiServer is null || o.ApiServer.IsAbsoluteUri, "KubernetesOptions.ApiServer must be absolute.")
            .Validate(o => o.ApiServer is null || o.ApiServer.Scheme == Uri.UriSchemeHttps || o.ApiServer.IsLoopback,
                "KubernetesOptions.ApiServer must use HTTPS: the token must not travel in clear text.")
            .Validate(o => o.Timeout > TimeSpan.Zero, "KubernetesOptions.Timeout must be positive.")
            .ValidateOnStart();

        builder.Services.AddHttpClient(KubernetesApi.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)   // KubernetesOptions.Timeout applies
            .ConfigurePrimaryHttpMessageHandler(sp => KubernetesApi.CreateHandler(sp.GetRequiredService<IOptions<KubernetesOptions>>().Value))
            .RemoveAllLoggers();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<KubernetesApi>();
        return builder;
    }

    /// <summary>
    /// Appends a step that deletes this application's own pod, so its controller (Deployment, StatefulSet) starts a
    /// fresh one: a restart that can also land on another node. The pod gets <paramref name="gracePeriod"/> to
    /// stop gracefully. The step refuses a pod without a controller, and applies <see cref="RestartGuard"/> (minimum
    /// uptime, restarts per window counted in the store): the new pod has a new name, so without those limits a plan
    /// could delete pod after pod. The step ends the run: put it last. Needs <c>get</c> and <c>delete</c> on <c>pods</c>.
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="gracePeriod">Graceful termination period, in whole seconds, at least 1 second; defaults to 30 seconds.</param>
    /// <param name="requireApproval">Whether a person approves first; defaults to <c>true</c>.</param>
    /// <param name="limits">Minimum uptime and restarts per window; defaults to <see cref="RestartLimits.Default"/>.</param>
    public static HealingPlanBuilder RestartPod(this HealingPlanBuilder plan, TimeSpan? gracePeriod = null, bool requireApproval = true, RestartLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var grace = gracePeriod ?? TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.FromSeconds(1), nameof(gracePeriod));
        var gracePeriodSeconds = (int)Math.Ceiling(grace.TotalSeconds);
        var effective = limits ?? RestartLimits.Default;
        return Approval(plan.With("Restart this pod", (c, ct) => KubernetesActions.RestartPodAsync(c, gracePeriodSeconds, effective, ct)), requireApproval);
    }

    /// <summary>
    /// Appends a step that replaces every pod of <paramref name="deployment"/> with a rolling update, like
    /// <c>kubectl rollout restart</c>. Needs <c>patch</c> on <c>deployments</c>.
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="deployment">Deployment name, in the application's namespace.</param>
    /// <param name="requireApproval">Whether a person approves first; defaults to <c>true</c>.</param>
    public static HealingPlanBuilder RolloutRestart(this HealingPlanBuilder plan, string deployment, bool requireApproval = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        KubernetesNames.Validate(deployment, "deployment name");
        return Approval(plan.With($"Rollout restart {deployment}", (c, ct) => KubernetesActions.RolloutRestartAsync(c, deployment, ct)), requireApproval);
    }

    /// <summary>
    /// Appends a step that changes the replicas of <paramref name="deployment"/> by <paramref name="by"/>, up to
    /// <paramref name="maximum"/> (or down to <paramref name="minimum"/>), only in that direction. The step fails when the
    /// deployment is already at or beyond the limit, so the plan escalates. With a HorizontalPodAutoscaler on the deployment, change the autoscaler instead:
    /// it would undo this. Needs <c>get</c> and <c>patch</c> on <c>deployments/scale</c>.
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="deployment">Deployment name, in the application's namespace.</param>
    /// <param name="by">Replicas to add (negative to remove).</param>
    /// <param name="maximum">Most replicas this step may reach.</param>
    /// <param name="minimum">Fewest replicas this step may leave; defaults to 1.</param>
    /// <param name="requireApproval">Whether a person approves first; defaults to <c>true</c>.</param>
    public static HealingPlanBuilder ScaleDeployment(
        this HealingPlanBuilder plan, string deployment, int by, int maximum, int minimum = 1, bool requireApproval = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        KubernetesNames.Validate(deployment, "deployment name");
        if (by == 0 || minimum < 0 || maximum < minimum)
        {
            throw new ArgumentException("'by' must not be zero, and 0 <= minimum <= maximum.");
        }

        return Approval(
            plan.With($"Scale {deployment} by {by:+#;-#}", (c, ct) => KubernetesActions.ScaleAsync(c, deployment, by, minimum, maximum, ct)),
            requireApproval);
    }

    /// <summary>
    /// Appends a step that marks the node running this pod as unschedulable, so new pods go elsewhere (it does not
    /// evict running pods). Use it when the node itself is the problem: disk, network, a noisy neighbor. Uncordon it by
    /// hand when fixed (<c>kubectl uncordon</c>), or with <see cref="UndoWithUncordon"/>. A node that is already cordoned
    /// is left alone (the step fails). Needs <c>get</c> and <c>patch</c> on <c>nodes</c> (a ClusterRole).
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="requireApproval">Whether a person approves first; defaults to <c>true</c>.</param>
    public static HealingPlanBuilder CordonNode(this HealingPlanBuilder plan, bool requireApproval = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Approval(plan.With("Cordon this node", (c, ct) => KubernetesActions.SetUnschedulableAsync(c, unschedulable: true, ct)), requireApproval);
    }

    /// <summary>Compensation for <see cref="CordonNode"/>: makes the node schedulable again when cordoning did not help.</summary>
    /// <param name="plan">The plan builder.</param>
    public static HealingPlanBuilder UndoWithUncordon(this HealingPlanBuilder plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.UndoWith("Uncordon this node", (c, ct) => KubernetesActions.SetUnschedulableAsync(c, unschedulable: false, ct));
    }

    private static HealingPlanBuilder Approval(HealingPlanBuilder plan, bool requireApproval)
        => requireApproval ? plan.RequireApproval() : plan;
}
