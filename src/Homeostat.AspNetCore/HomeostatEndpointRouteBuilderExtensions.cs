using System.Security.Claims;
using Homeostat.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Homeostat;

/// <summary>Maps the Homeostat endpoints.</summary>
public static class HomeostatEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET {prefix}/status</c>: conditions, switches, pending approvals, recent runs and budget, as JSON.
    /// The endpoint reveals internal details (condition names, error messages, approval ids), so it requires an
    /// authorization policy unless <paramref name="allowAnonymous"/> is <c>true</c>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="policy">Authorization policy required to call the endpoint, for example <c>"operations"</c>.</param>
    /// <param name="prefix">Route prefix.</param>
    /// <param name="allowAnonymous"><c>true</c> maps the endpoint without authorization; only for local demos and tests.</param>
    /// <exception cref="ArgumentException">Neither <paramref name="policy"/> nor <paramref name="allowAnonymous"/> was given.</exception>
    /// <example><code>app.MapHomeostatStatus("operations");</code></example>
    public static RouteGroupBuilder MapHomeostatStatus(
        this IEndpointRouteBuilder endpoints,
        string? policy = null,
        string prefix = "/homeostat",
        bool allowAnonymous = false)
    {
        var group = CreateGroup(endpoints, policy, prefix, allowAnonymous);
        group.MapGet("/status", GetStatus).WithName("HomeostatStatus");
        return group;
    }

    /// <summary>
    /// Maps the control endpoints:
    /// <list type="bullet">
    /// <item><description><c>POST {prefix}/pause</c> and <c>POST {prefix}/resume</c>: kill switch.</description></item>
    /// <item><description><c>POST {prefix}/approvals/{id}/approve</c> and <c>.../reject</c>: answer an approval request; the user name is recorded.</description></item>
    /// <item><description><c>POST {prefix}/switches/{name}/on</c> and <c>.../off</c>: flip a switch by hand.</description></item>
    /// </list>
    /// They change what the application does to itself, so they require an authorization policy (and therefore
    /// <c>app.UseAuthorization()</c>) unless <paramref name="allowAnonymous"/> is <c>true</c>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="policy">Authorization policy required to call the endpoints, for example <c>"operations"</c>.</param>
    /// <param name="prefix">Route prefix.</param>
    /// <param name="allowAnonymous">
    /// <c>true</c> maps the endpoints without any authorization requirement. Only for local demos and tests:
    /// anyone who can reach them can pause Homeostat or approve steps.
    /// </param>
    /// <exception cref="ArgumentException">Neither <paramref name="policy"/> nor <paramref name="allowAnonymous"/> was given.</exception>
    /// <example><code>app.MapHomeostatControl("operations");</code></example>
    public static RouteGroupBuilder MapHomeostatControl(
        this IEndpointRouteBuilder endpoints,
        string? policy = null,
        string prefix = "/homeostat",
        bool allowAnonymous = false)
    {
        var group = CreateGroup(endpoints, policy, prefix, allowAnonymous);

        group.MapPost("/pause", (IHomeostatControl control) =>
        {
            control.Pause();
            return Results.NoContent();
        });

        group.MapPost("/resume", (IHomeostatControl control) =>
        {
            control.Resume();
            return Results.NoContent();
        });

        group.MapPost("/approvals/{id}/approve", (string id, IHomeostatApprovals approvals, ClaimsPrincipal user)
            => approvals.Approve(id, UserName(user)) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/approvals/{id}/reject", (string id, IHomeostatApprovals approvals, ClaimsPrincipal user)
            => approvals.Reject(id, UserName(user)) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/switches/{name}/on", (string name, IHomeostatSwitches switches) =>
        {
            if (name.Length > MaxSwitchNameLength)
            {
                return Results.BadRequest();
            }

            switches.TurnOn(name);
            return Results.NoContent();
        });

        group.MapPost("/switches/{name}/off", (string name, IHomeostatSwitches switches) =>
        {
            switches.TurnOff(name);
            return Results.NoContent();
        });

        return group;
    }

    private const int MaxSwitchNameLength = 200;

    private static RouteGroupBuilder CreateGroup(IEndpointRouteBuilder endpoints, string? policy, string prefix, bool allowAnonymous)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        if (string.IsNullOrWhiteSpace(policy) && !allowAnonymous)
        {
            throw new ArgumentException(
                "Homeostat endpoints need an authorization policy, for example MapHomeostatControl(\"operations\"). " +
                "For a local demo only, pass allowAnonymous: true.",
                nameof(policy));
        }

        var group = endpoints.MapGroup(prefix).WithTags("Homeostat");
        if (!string.IsNullOrWhiteSpace(policy))
        {
            group.RequireAuthorization(policy);
        }

        return group;
    }

    private static IResult GetStatus(
        IHomeostatControl control,
        IHomeostatSwitches switches,
        IHomeostatApprovals approvals,
        IOptions<HomeostatOptions> options)
    {
        var budget = control.GetBudget();
        var status = new HomeostatStatus(
            options.Value.ApplicationName,
            control.IsPaused,
            control.IsDryRun,
            new BudgetStatus(budget.Used, budget.Limit, budget.Remaining, budget.Window.TotalSeconds),
            control.GetConditions().Select(ConditionStatusInfo.From).ToArray(),
            switches.GetActive(),
            approvals.GetPending(),
            control.GetRecentRuns().Select(RunInfo.From).ToArray());
        return Results.Ok(status);
    }

    private static string? UserName(ClaimsPrincipal user)
        => user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;
}
