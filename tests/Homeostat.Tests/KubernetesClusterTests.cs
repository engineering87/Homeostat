using Homeostat.Healing;
using Homeostat.Kubernetes;
using Homeostat.Signals;
using Homeostat.Testing;

namespace Homeostat.Tests;

/// <summary>
/// The Kubernetes actions against a real cluster, with the service account and the Role of docs/cloud.md. Skipped
/// unless HOMEOSTAT_TEST_KUBERNETES_SERVER is set: the CI "kubernetes" job sets it up on a kind cluster (see
/// .github/workflows/ci.yml), with a deployment "weather-api" whose pod may be evicted, and a deployment
/// "weather-protected" whose only pod a PodDisruptionBudget protects.
/// </summary>
public class KubernetesClusterTests
{
    private static string Variable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(value), $"No Kubernetes cluster: set {name} (see the kubernetes job of the CI workflow).");
        return value!.Trim();
    }

    private static Task<HomeostatTestHost> StartAsync(string pod, Action<HealingPlanBuilder> plan)
        => HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.DefaultActionTimeout = TimeSpan.FromDays(1))   // real calls, simulated time
            .UseKubernetes(o =>
            {
                o.ApiServer = new Uri(Variable("HOMEOSTAT_TEST_KUBERNETES_SERVER"));
                o.CertificateAuthorityPath = Variable("HOMEOSTAT_TEST_KUBERNETES_CA");
                o.TokenPath = Variable("HOMEOSTAT_TEST_KUBERNETES_TOKEN");
                o.Namespace = Variable("HOMEOSTAT_TEST_KUBERNETES_NAMESPACE");
                o.PodName = pod;
                o.NodeName = "unused";
            })
            .AddCondition("weather-api-sick", c => c.ForSource("weather-api").Heal(p => plan(p.VerifyWith<CachingTests.NeverHealed>()))));

    private static readonly RestartLimits Limits = new(TimeSpan.Zero, 3, TimeSpan.FromHours(1));

    [Fact]
    public async Task A_pod_is_evicted_and_the_deployment_restarted_and_scaled_with_the_documented_role()
    {
        // The eviction first: the rollout restart replaces the pod.
        await using var host = await StartAsync(Variable("HOMEOSTAT_TEST_KUBERNETES_POD"), p => p
            .RestartPod(gracePeriod: TimeSpan.FromSeconds(1), requireApproval: false, limits: Limits)
            .RolloutRestart("weather-api", requireApproval: false)
            .ScaleDeployment("weather-api", by: 1, maximum: 2, requireApproval: false));

        host.Publish("weather-api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("weather-api-sick", TimeSpan.FromMinutes(1));

        Assert.All(run.Steps, s => Assert.True(s.Succeeded, s.Error?.ToString()));
    }

    [Fact]
    public async Task A_pod_protected_by_a_disruption_budget_is_not_evicted()
    {
        await using var host = await StartAsync(Variable("HOMEOSTAT_TEST_KUBERNETES_PROTECTED_POD"), p => p
            .RestartPod(requireApproval: false, limits: Limits));

        host.Publish("weather-api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("weather-api-sick", TimeSpan.FromMinutes(1));

        var error = Assert.IsType<InvalidOperationException>(Assert.Single(run.Steps).Error);
        Assert.Contains("PodDisruptionBudget", error.Message);
    }
}
