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
    /// Maps <c>GET {prefix}/status</c>: conditions, switches, pending approvals, recent runs and budget, as JSON; and
    /// <c>GET {prefix}/coverage</c> (<c>?last=04:00:00</c> or <c>?since=2026-11-02T08:00:00Z</c>, default the last 24
    /// hours): which conditions activated, which plans ran and which steps never ran (see <see cref="Diagnostics.CoverageReport"/>);
    /// and <c>GET {prefix}/history</c>: the latest condition activations and clears, newest first (optionally
    /// <c>?condition=name</c>).
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
        group.MapGet("/coverage", GetCoverageAsync).WithName("HomeostatCoverage");
        group.MapGet("/history", GetHistoryAsync).WithName("HomeostatConditionHistory");
        return group;
    }

    /// <summary>
    /// Maps the control endpoints:
    /// <list type="bullet">
    /// <item><description><c>POST {prefix}/pause</c> (optionally <c>?for=00:30:00</c>, resuming by itself) and <c>POST {prefix}/resume</c>: kill switch.</description></item>
    /// <item><description><c>POST {prefix}/approvals/{id}/approve</c> and <c>.../reject</c>: answer an approval request; the user name is recorded.</description></item>
    /// <item><description><c>POST {prefix}/switches/{name}/on</c> and <c>.../off</c>: flip a switch by hand.</description></item>
    /// </list>
    /// They change what the application does to itself, so they require an authorization policy (and therefore
    /// <c>app.UseAuthorization()</c>) unless <paramref name="allowAnonymous"/> is <c>true</c>. Against cross-site request
    /// forgery, every request must carry an <c>Authorization: Bearer</c> token or the <c>X-Requested-With</c> header
    /// (any value), and requests that browsers mark as cross-site are refused.
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

        // Cross-site request forgery: a page on another site can make the browser POST here with ambient credentials
        // (cookies, Windows authentication, Basic, client certificates), but cannot add a custom header or an
        // Authorization: Bearer header without a CORS preflight. So every request must carry a bearer token or the
        // X-Requested-With header (the dashboard and the CLI send it), and browsers' cross-site requests are refused.
        group.AddEndpointFilter(static async (context, next) => IsForgeable(context.HttpContext.Request)
            ? HomeostatResults.Problem(
                "Control requests must carry an Authorization: Bearer token or the X-Requested-With header, and come from the same site.",
                StatusCodes.Status403Forbidden)
            : await next(context));

        group.MapPost("/pause", (IHomeostatControl control, TimeProvider time, string? @for) =>
        {
            if (@for is null)
            {
                control.Pause();
                return Results.NoContent();
            }

            if (!TimeSpan.TryParse(@for, System.Globalization.CultureInfo.InvariantCulture, out var duration)
                || duration <= TimeSpan.Zero || duration > MaxPause)
            {
                return HomeostatResults.BadRequest("'for' must be a positive duration of at most 30 days, in the d.hh:mm:ss format.");
            }

            control.PauseUntil(time.GetUtcNow() + duration);
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
            if (!IsValidSwitchName(name))
            {
                return Results.BadRequest();
            }

            switches.TurnOn(name);
            return Results.NoContent();
        });

        group.MapPost("/switches/{name}/off", (string name, IHomeostatSwitches switches) =>
        {
            if (!IsValidSwitchName(name))
            {
                return Results.BadRequest();
            }

            switches.TurnOff(name);
            return Results.NoContent();
        });

        return group;
    }

    private const int MaxSwitchNameLength = 200;

    private static readonly TimeSpan MaxPause = TimeSpan.FromDays(30);

    internal static bool IsForgeable(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite is "cross-site" or "same-site")
        {
            return true;
        }

        var hasBearer = request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        return !hasBearer && !request.Headers.ContainsKey("X-Requested-With");
    }

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

    private static async Task<IResult> GetStatus(
        IHomeostatControl control,
        IHomeostatSwitches switches,
        IHomeostatApprovals approvals,
        IOptions<HomeostatOptions> options,
        CancellationToken cancellationToken)
    {
        // The status stays available when the store is not: that is when operators need it most.
        BudgetStatus? budgetStatus = null;
        IReadOnlyList<RunInfo> runInfos = [];
        string? storeError = null;
        try
        {
            var budget = await control.GetBudgetAsync(cancellationToken);
            budgetStatus = new BudgetStatus(budget.Used, budget.Limit, budget.Remaining, budget.Window.TotalSeconds);
            runInfos = (await control.GetRecentRunsAsync(cancellationToken)).Select(RunInfo.From).ToArray();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            storeError = ex.Message;
        }

        var status = new HomeostatStatus(
            options.Value.ApplicationName,
            options.Value.InstanceId,
            control.IsPaused,
            control.PauseReason,
            control.IsDryRun,
            budgetStatus,
            control.GetConditions().Select(ConditionStatusInfo.From).ToArray(),
            switches.GetActive(),
            approvals.GetPending(),
            runInfos,
            storeError);
        return Results.Json(status, HomeostatAspNetCoreJsonContext.Default.HomeostatStatus);
    }

    private static async Task<IResult> GetHistoryAsync(IHomeostatControl control, string? condition, CancellationToken cancellationToken)
    {
        try
        {
            var events = await control.GetConditionHistoryAsync(cancellationToken);
            return Results.Json(events
                .Where(e => condition is null || string.Equals(e.ConditionName, condition, StringComparison.Ordinal))
                .Select(ConditionEventInfo.From)
                .ToArray(), HomeostatAspNetCoreJsonContext.Default.ConditionEventInfoArray);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeostatResults.Problem("The condition history is unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> GetCoverageAsync(
        IHomeostatControl control, TimeProvider time, string? since, string? last, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        DateTimeOffset from;
        if (since is not null)
        {
            if (!DateTimeOffset.TryParse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out from))
            {
                return HomeostatResults.BadRequest("'since' must be a date and time, for example 2026-11-02T08:00:00Z.");
            }
        }
        else if (last is not null)
        {
            if (!TimeSpan.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, out var span) || span <= TimeSpan.Zero || span > TimeSpan.FromDays(365))
            {
                return HomeostatResults.BadRequest("'last' must be a positive duration in the d.hh:mm:ss format.");
            }

            from = now - span;
        }
        else
        {
            from = now - TimeSpan.FromDays(1);
        }

        try
        {
            var report = await control.GetCoverageAsync(from, cancellationToken);
            return Results.Json(report, HomeostatAspNetCoreJsonContext.Default.CoverageReport);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No exception details: they can carry store connection details.
            return HomeostatResults.Problem("The run history is unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string? UserName(ClaimsPrincipal user)
        => user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;

    /// <summary>A switch name from a URL: not empty, not too long, and without control characters (no line breaks in logs).</summary>
    internal static bool IsValidSwitchName(string name)
        => name.Length is > 0 and <= MaxSwitchNameLength && !name.Any(char.IsControl);
}
