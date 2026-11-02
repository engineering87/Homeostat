using System.Text.Json.Nodes;
using Homeostat.Azure;
using Homeostat.Healing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Homeostat;

/// <summary>Azure actions. Each one requires approval by default: pass <c>requireApproval: false</c> to opt out.</summary>
public static class HomeostatAzureExtensions
{
    private const string WebApiVersion = "2024-04-01";
    private const string ComputeApiVersion = "2024-07-01";
    private const string FrontDoorApiVersion = "2024-02-01";

    /// <summary>Lets healing plans call Azure Resource Manager with the token from <see cref="AzureOptions.AccessToken"/>.</summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Sets the token provider and, optionally, the endpoint and timeout.</param>
    /// <example>
    /// <code>
    /// var credential = new DefaultAzureCredential();
    /// h.UseAzure(o => o.AccessToken = async ct =>
    ///     (await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct)).Token);
    /// </code>
    /// </example>
    public static HomeostatBuilder UseAzure(this HomeostatBuilder builder, Action<AzureOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddOptions<AzureOptions>()
            .Configure(configure)
            .Validate(o => o.AccessToken is not null, "AzureOptions.AccessToken is required.")
            .Validate(o => o.ResourceManager is { IsAbsoluteUri: true, Scheme: "https" }, "AzureOptions.ResourceManager must be an absolute HTTPS address.")
            .Validate(o => o.Timeout > TimeSpan.Zero, "AzureOptions.Timeout must be positive.")
            .ValidateOnStart();
        builder.Services.AddHttpClient(ResourceManagerApi.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)   // AzureOptions.Timeout applies
            .RemoveAllLoggers();
        builder.Services.TryAddSingleton<ResourceManagerApi>();
        return builder;
    }

    /// <summary>
    /// Appends a step that restarts an App Service app or deployment slot. Needs <c>Microsoft.Web/sites/restart/action</c>
    /// (or <c>sites/slots/restart/action</c>), included in the Website Contributor role.
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="siteResourceId">Resource id of the app or slot: <c>/subscriptions/.../providers/Microsoft.Web/sites/{name}</c>.</param>
    /// <param name="softRestart">Azure's soft restart: applies the configuration settings and restarts the app only if necessary; defaults to <c>false</c> (always restart).</param>
    /// <param name="requireApproval">Whether a person approves first; defaults to <c>true</c>.</param>
    public static HealingPlanBuilder RestartAppService(this HealingPlanBuilder plan, string siteResourceId, bool softRestart = false, bool requireApproval = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var id = ResourceManagerApi.ValidateResourceId(siteResourceId, ResourceManagerApi.SitePattern(), "an App Service app or slot (.../providers/Microsoft.Web/sites/{name}[/slots/{slot}])");
        return Step(plan, $"Restart App Service {Name(id)}", HttpMethod.Post, id,
            $"/restart?softRestart={(softRestart ? "true" : "false")}&synchronous=false", WebApiVersion, body: null, requireApproval);
    }

    /// <summary>
    /// Appends a step that restarts a virtual machine (the operation is asynchronous: the step completes when Azure
    /// accepts it). Needs <c>Microsoft.Compute/virtualMachines/restart/action</c> (Virtual Machine Contributor).
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="vmResourceId">Resource id of the virtual machine.</param>
    /// <param name="requireApproval">Whether a person approves first; defaults to <c>true</c>.</param>
    public static HealingPlanBuilder RestartVirtualMachine(this HealingPlanBuilder plan, string vmResourceId, bool requireApproval = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var id = ResourceManagerApi.ValidateResourceId(vmResourceId, ResourceManagerApi.VirtualMachinePattern(), "a virtual machine (.../providers/Microsoft.Compute/virtualMachines/{name})");
        return Step(plan, $"Restart virtual machine {Name(id)}", HttpMethod.Post, id, "/restart", ComputeApiVersion, body: null, requireApproval);
    }

    /// <summary>
    /// Appends a step that takes an Azure Front Door origin out of rotation (<c>enabledState: Disabled</c>), so traffic
    /// goes to the other origins of its group: a regional failover when this region is the problem. The step refuses an
    /// origin that is already disabled, and the last enabled origin of its group. When the step does not help, the
    /// origin is enabled again before escalating; enable it by hand after the incident otherwise.
    /// Needs write access to the Front Door profile (CDN Profile Contributor).
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="originResourceId">Resource id of the origin: <c>.../providers/Microsoft.Cdn/profiles/{profile}/originGroups/{group}/origins/{origin}</c>.</param>
    /// <param name="requireApproval">Whether a person approves first; defaults to <c>true</c>.</param>
    public static HealingPlanBuilder DisableFrontDoorOrigin(this HealingPlanBuilder plan, string originResourceId, bool requireApproval = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var id = ResourceManagerApi.ValidateResourceId(originResourceId, ResourceManagerApi.FrontDoorOriginPattern(),
            "a Front Door origin (.../providers/Microsoft.Cdn/profiles/{profile}/originGroups/{group}/origins/{origin})");

        plan.With($"Disable Front Door origin {Name(id)}", async (context, ct) =>
        {
            var api = context.Services.GetRequiredService<ResourceManagerApi>();

            // An origin someone already disabled is left alone: disabling it again would register an undo that enables it.
            var origin = await api.SendAsync(HttpMethod.Get, id, null, FrontDoorApiVersion, null, ct).ConfigureAwait(false);
            if (string.Equals(origin?["properties"]?["enabledState"]?.GetValue<string>(), "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Front Door origin {Name(id)} is already disabled: Homeostat leaves it as it is.");
            }

            // Never take the last enabled origin of the group out of rotation: that is a full outage, not a failover.
            var group = id[..id.LastIndexOf("/origins/", StringComparison.OrdinalIgnoreCase)];
            var origins = await api.SendAsync(HttpMethod.Get, group, "/origins", FrontDoorApiVersion, null, ct).ConfigureAwait(false);
            var othersEnabled = (origins?["value"] as System.Text.Json.Nodes.JsonArray ?? [])
                .Count(o => !string.Equals(o?["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(o?["properties"]?["enabledState"]?.GetValue<string>(), "Enabled", StringComparison.OrdinalIgnoreCase));
            if (othersEnabled == 0)
            {
                throw new InvalidOperationException($"Front Door origin {Name(id)} is the only enabled origin of its group: disabling it would stop all traffic.");
            }

            context.Services.GetService<ILoggerFactory>()?.CreateLogger("Homeostat.Azure")
                .LogWarning("Disabling Front Door origin {Origin} for condition {Condition}.", Name(id), context.ConditionName);
            await api.SendAsync(HttpMethod.Patch, id, null, FrontDoorApiVersion, new JsonObject { ["properties"] = new JsonObject { ["enabledState"] = "Disabled" } }, ct).ConfigureAwait(false);
        });
        plan.UndoWith($"Enable Front Door origin {Name(id)}", (context, ct) => context.Services.GetRequiredService<ResourceManagerApi>()
            .SendAsync(HttpMethod.Patch, id, null, FrontDoorApiVersion, new JsonObject { ["properties"] = new JsonObject { ["enabledState"] = "Enabled" } }, ct));
        return requireApproval ? plan.RequireApproval() : plan;
    }

    /// <summary>
    /// Appends a step that sends any Azure Resource Manager request, for operations without a dedicated action (for
    /// example restarting a Container Apps revision or a Redis cache). The step succeeds when ARM accepts the request.
    /// </summary>
    /// <param name="plan">The plan builder.</param>
    /// <param name="name">Step name, shown in logs and approvals.</param>
    /// <param name="method">HTTP method, for example POST.</param>
    /// <param name="resourceId">Resource id.</param>
    /// <param name="action">Path after the resource id, for example <c>/restart</c>; <c>null</c> for the resource itself.</param>
    /// <param name="apiVersion">API version of the resource provider.</param>
    /// <param name="body">JSON body, or <c>null</c>.</param>
    /// <param name="requireApproval">Whether a person approves first; defaults to <c>true</c>.</param>
    /// <example>
    /// <code>
    /// .AzureResourceManager("Restart revision", HttpMethod.Post,
    ///     "/subscriptions/.../providers/Microsoft.App/containerApps/forecast/revisions/forecast--v7", "/restart", "2024-03-01")
    /// </code>
    /// </example>
    public static HealingPlanBuilder AzureResourceManager(
        this HealingPlanBuilder plan, string name, HttpMethod method, string resourceId, string? action, string apiVersion,
        JsonNode? body = null, bool requireApproval = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);
        var id = ResourceManagerApi.ValidateResourceId(resourceId);
        if (action is not null && !ResourceManagerApi.IsValidAction(action))
        {
            throw new ArgumentException("The action must be a path such as /restart, without a query string.", nameof(action));
        }

        return Step(plan, name, method, id, action, apiVersion, body, requireApproval);
    }

    private static HealingPlanBuilder Step(
        HealingPlanBuilder plan, string name, HttpMethod method, string resourceId, string? action, string apiVersion,
        JsonNode? body, bool requireApproval)
    {
        plan.With(name, async (context, ct) =>
        {
            context.Services.GetService<ILoggerFactory>()?.CreateLogger("Homeostat.Azure")
                .LogWarning("{Step} for condition {Condition}.", name, context.ConditionName);
            await context.Services.GetRequiredService<ResourceManagerApi>()
                .SendAsync(method, resourceId, action, apiVersion, body, ct).ConfigureAwait(false);
        });

        return requireApproval ? plan.RequireApproval() : plan;
    }

    private static string Name(string resourceId) => resourceId[(resourceId.LastIndexOf('/') + 1)..];
}
