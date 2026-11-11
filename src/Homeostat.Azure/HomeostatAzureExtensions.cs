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
    /// h.UseAzure(o => o.AccessToken = async (services, ct) =>
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
    /// <remarks>
    /// <para>
    /// The step acts on a shared resource, not on this instance: mark the plan <c>OncePerCluster()</c>. With the default
    /// scope every instance that sees the condition runs the plan, so a condition active on N replicas sends the request
    /// N times.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// The step acts on a shared resource, not on this instance: mark the plan <c>OncePerCluster()</c>. With the default
    /// scope every instance that sees the condition runs the plan, so a condition active on N replicas sends the request
    /// N times.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// The step acts on a shared resource, not on this instance: mark the plan <c>OncePerCluster()</c>. With the default
    /// scope every instance that sees the condition runs the plan, so a condition active on N replicas sends the request
    /// N times.
    /// </para>
    /// </remarks>
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
            if (OthersEnabled(origins, id) == 0)
            {
                throw new InvalidOperationException($"Front Door origin {Name(id)} is the only enabled origin of its group: disabling it would stop all traffic.");
            }

            var logger = context.Services.GetService<ILoggerFactory>()?.CreateLogger("Homeostat.Azure");
            logger?.LogWarning("Disabling Front Door origin {Origin} for condition {Condition}.", Name(id), context.ConditionName);

            // The check above and the change are not atomic: another plan (another condition, or another instance)
            // may have disabled the other origins meanwhile. Read the group again; if no other origin is enabled any
            // more, this one goes back into rotation and the step fails. When two steps race, both may restore their
            // origin, which keeps traffic flowing: the safe outcome. The restore does not take the step's token, so a
            // step timeout cannot cut it short (the request has its own timeout).
            //
            // A step that throws is not undone, so any failure from the change on (a change that timed out after ARM
            // applied it, a re-read refused with 429 or 5xx, the step's timeout) also puts the origin back before the
            // error is passed on: otherwise it could stay out of rotation with nothing to restore it, and without the
            // last-origin check. The origin was enabled before the change, so enabling it again is safe.
            JsonNode? recheck;
            try
            {
                await api.SendAsync(HttpMethod.Patch, id, null, FrontDoorApiVersion, Enabled(false), ct).ConfigureAwait(false);
                recheck = await api.SendAsync(HttpMethod.Get, group, "/origins", FrontDoorApiVersion, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Disabling Front Door origin {Origin} failed or could not be checked: enabling it again.", Name(id));
                await EnableAgainAsync(api, id, logger).ConfigureAwait(false);
                throw;
            }

            if (OthersEnabled(recheck, id) == 0)
            {
                logger?.LogError("Front Door origin {Origin} was the last enabled origin of its group after it was disabled: enabling it again.", Name(id));
                await api.SendAsync(HttpMethod.Patch, id, null, FrontDoorApiVersion, Enabled(true), CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Front Door origin {Name(id)} was enabled again: the other origins of its group were disabled meanwhile (by another plan or a person), and disabling it would stop all traffic.");
            }
        });
        plan.UndoWith($"Enable Front Door origin {Name(id)}", (context, ct) => context.Services.GetRequiredService<ResourceManagerApi>()
            .SendAsync(HttpMethod.Patch, id, null, FrontDoorApiVersion, Enabled(true), ct));
        return requireApproval ? plan.RequireApproval() : plan;
    }

    /// <summary>
    /// Appends a step that sends any Azure Resource Manager request, for operations without a dedicated action (for
    /// example restarting a Container Apps revision or a Redis cache). The step succeeds when ARM accepts the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The step acts on a shared resource, not on this instance: mark the plan <c>OncePerCluster()</c>. With the default
    /// scope every instance that sees the condition runs the plan, so a condition active on N replicas sends the request
    /// N times.
    /// </para>
    /// </remarks>
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

    private static JsonObject Enabled(bool enabled)
        => new() { ["properties"] = new JsonObject { ["enabledState"] = enabled ? "Enabled" : "Disabled" } };

    /// <summary>Best-effort restore after a failed disable; the caller passes the original error on.</summary>
    private static async Task EnableAgainAsync(ResourceManagerApi api, string id, ILogger? logger)
    {
        try
        {
            await api.SendAsync(HttpMethod.Patch, id, null, FrontDoorApiVersion, Enabled(true), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogCritical(ex, "Front Door origin {Origin} could not be enabled again: check it, and enable it by hand if it is disabled.", Name(id));
        }
    }

    private static string Name(string resourceId) => resourceId[(resourceId.LastIndexOf('/') + 1)..];

    /// <summary>Enabled origins of a group listing, other than <paramref name="originId"/>.</summary>
    private static int OthersEnabled(JsonNode? origins, string originId)
        => (origins?["value"] as JsonArray ?? [])
            .Count(o => !string.Equals(o?["id"]?.GetValue<string>(), originId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(o?["properties"]?["enabledState"]?.GetValue<string>(), "Enabled", StringComparison.OrdinalIgnoreCase));
}
