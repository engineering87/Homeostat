using System.Security.Claims;
using Homeostat.AspNetCore;
using Homeostat.Healing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat;

/// <summary>Maps the Homeostat endpoints.</summary>
public static class HomeostatEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET {prefix}/status</c>: conditions, switches, pending approvals, recent runs, budget and healing groups, as JSON; and
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
    /// <exception cref="ArgumentException">
    /// Neither <paramref name="policy"/> nor <paramref name="allowAnonymous"/> was given, or <paramref name="policy"/>
    /// starts with <c>/</c> (a route prefix passed by position).
    /// </exception>
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
    /// <item><description><c>POST {prefix}/groups/{group}/pause</c> (the same <c>?for=</c>) and <c>.../resume</c>: pause or resume one healing group.</description></item>
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
    /// <exception cref="ArgumentException">
    /// Neither <paramref name="policy"/> nor <paramref name="allowAnonymous"/> was given, or <paramref name="policy"/>
    /// starts with <c>/</c> (a route prefix passed by position).
    /// </exception>
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

        group.MapPost("/pause", (IHomeostatControl control, TimeProvider time, ClaimsPrincipal user, string? @for, CancellationToken ct) =>
        {
            if (!TryGetPauseEnd(@for, time, out var until))
            {
                return Task.FromResult(InvalidPauseDuration());
            }

            return StoreCallAsync(() => control.PauseAsync(until, UserName(user), CancellationToken.None), ct);
        });

        group.MapPost("/resume", (IHomeostatControl control, ClaimsPrincipal user, CancellationToken ct)
            => StoreCallAsync(() => control.ResumeAsync(UserName(user), CancellationToken.None), ct, letsAct: true));

        // The pause of one healing group: the same parameters, validation, store timeout and answers as the global pause
        // (decision G4: a group pause takes the global pause's path in the core).
        group.MapPost("/groups/{group}/pause", (string group, IHomeostatControl control, TimeProvider time, ClaimsPrincipal user, string? @for, CancellationToken ct) =>
        {
            group = RouteName(group);
            if (!IsValidGroupName(group))
            {
                return Task.FromResult(Results.BadRequest());
            }

            if (!TryGetPauseEnd(@for, time, out var until))
            {
                return Task.FromResult(InvalidPauseDuration());
            }

            return StoreCallAsync(async () =>
            {
                try
                {
                    await control.PauseGroupAsync(group, until, UserName(user), CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // Refused by the control (too many groups paused at once), at once or once the store refused it for
                    // the same reason (the control confirms it with a read; an outage defers the pause instead of
                    // throwing): not a store failure, so not the "nothing was changed, the store is unavailable" answer.
                    throw new RefusedChangeException(ex.Message);
                }
            }, ct);
        });

        group.MapPost("/groups/{group}/resume", (string group, IHomeostatControl control, ClaimsPrincipal user, CancellationToken ct) =>
        {
            group = RouteName(group);
            if (!IsValidGroupName(group))
            {
                return Task.FromResult(Results.BadRequest());
            }

            return StoreCallAsync(() => control.ResumeGroupAsync(group, UserName(user), CancellationToken.None), ct, letsAct: true);
        });

        group.MapPost("/conditions/{name}/release", (string name, IHomeostatControl control, CancellationToken ct) =>
        {
            name = RouteName(name);
            if (!IsValidConditionName(name))
            {
                return Task.FromResult(Results.BadRequest());
            }

            return AnswerAsync(() => control.ReleaseHoldAsync(name, CancellationToken.None), ct);
        });

        group.MapPost("/approvals/{id}/approve", (string id, IHomeostatApprovals approvals, ClaimsPrincipal user, CancellationToken ct)
            => AnswerAsync(() => approvals.ApproveAsync(RouteName(id), UserName(user), CancellationToken.None), ct));

        group.MapPost("/approvals/{id}/reject", (string id, IHomeostatApprovals approvals, ClaimsPrincipal user, CancellationToken ct)
            => AnswerAsync(() => approvals.RejectAsync(RouteName(id), UserName(user), CancellationToken.None), ct));

        group.MapPost("/switches/{name}/on", (string name, IHomeostatSwitches switches, ClaimsPrincipal user, string? scope, CancellationToken ct) =>
        {
            name = RouteName(name);
            if (!IsValidSwitchName(name))
            {
                return Task.FromResult(Results.BadRequest());
            }

            SwitchScope target;
            switch (scope)
            {
                case null or "application":
                    target = SwitchScope.Application;
                    break;
                case "instance":
                    target = SwitchScope.Instance;
                    break;
                default:
                    return Task.FromResult(HomeostatResults.BadRequest("'scope' must be 'application' (the default) or 'instance'."));
            }

            return StoreCallAsync(() => switches.TurnOnAsync(name, target, UserName(user), CancellationToken.None), ct, letsAct: true);
        });

        group.MapPost("/switches/{name}/off", (string name, IHomeostatSwitches switches, ClaimsPrincipal user, CancellationToken ct) =>
        {
            name = RouteName(name);
            if (!IsValidSwitchName(name))
            {
                return Task.FromResult(Results.BadRequest());
            }

            return StoreCallAsync(() => switches.TurnOffAsync(name, UserName(user), CancellationToken.None), ct);
        });

        return group;
    }

    private const int MaxSwitchNameLength = 200;

    // Like the core's healing group names (HealingGroupNames): the same limit as switch names.
    private const int MaxGroupNameLength = 200;

    private static readonly TimeSpan MaxPause = TimeSpan.FromDays(30);

    // A store that does not answer: the request gets a 503 instead of waiting, so requests do not pile up.
    // Longer than SharedControl.WriteTimeout, so a pause or a switch turned off that the store does not take in time
    // (applied here, written later) answers 204 rather than 503.
    internal static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The end of a pause from the <c>for</c> query parameter: <c>null</c> without one (until resumed).</summary>
    private static bool TryGetPauseEnd(string? value, TimeProvider time, out DateTimeOffset? until)
    {
        until = null;
        if (value is null)
        {
            return true;
        }

        if (!TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var duration)
            || duration <= TimeSpan.Zero || duration > MaxPause)
        {
            return false;
        }

        until = time.GetUtcNow() + duration;
        return true;
    }

    private static IResult InvalidPauseDuration()
        => HomeostatResults.BadRequest("'for' must be a positive duration of at most 30 days, in the d.hh:mm:ss format.");

    /// <summary>A change the control refused before applying anything (not a store failure): answered <c>409</c> with its reason.</summary>
    private sealed class RefusedChangeException(string message) : Exception(message);

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

    /// <summary>
    /// Same as <see cref="MapHomeostatStatus(IEndpointRouteBuilder, string?, string, bool)"/>, with the authorization policy built
    /// inline instead of registered by name.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configurePolicy">Builds the policy, for example <c>p => p.RequireRole("operations")</c>.</param>
    /// <param name="prefix">Route prefix.</param>
    /// <example><code>app.MapHomeostatStatus(p => p.RequireRole("operations"));</code></example>
    public static RouteGroupBuilder MapHomeostatStatus(
        this IEndpointRouteBuilder endpoints,
        Action<Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder> configurePolicy,
        string prefix = "/homeostat")
    {
        ArgumentNullException.ThrowIfNull(configurePolicy);
        var group = MapHomeostatStatus(endpoints, policy: null, prefix, allowAnonymous: true);
        group.RequireAuthorization(configurePolicy);
        return group;
    }

    /// <summary>
    /// Same as <see cref="MapHomeostatControl(IEndpointRouteBuilder, string?, string, bool)"/>, with the authorization policy built
    /// inline instead of registered by name.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configurePolicy">Builds the policy, for example <c>p => p.RequireRole("operations")</c>.</param>
    /// <param name="prefix">Route prefix.</param>
    /// <example><code>app.MapHomeostatControl(p => p.RequireRole("operations"));</code></example>
    public static RouteGroupBuilder MapHomeostatControl(
        this IEndpointRouteBuilder endpoints,
        Action<Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder> configurePolicy,
        string prefix = "/homeostat")
    {
        ArgumentNullException.ThrowIfNull(configurePolicy);
        var group = MapHomeostatControl(endpoints, policy: null, prefix, allowAnonymous: true);
        group.RequireAuthorization(configurePolicy);
        return group;
    }

    private static RouteGroupBuilder CreateGroup(IEndpointRouteBuilder endpoints, string? policy, string prefix, bool allowAnonymous, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        RefuseRouteAsPolicy(policy, method);

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

    /// <summary>
    /// Refuses a policy name that starts with <c>/</c>: ASP.NET Core's <c>Map*(pattern)</c> habit makes
    /// <c>MapHomeostatControl("/ops")</c> likely, which would otherwise map under the default prefix and require a policy
    /// named "/ops" (decided at map time, decision I2).
    /// </summary>
    internal static void RefuseRouteAsPolicy(string? policy, string method)
    {
        if (policy is not null && policy.StartsWith('/'))
        {
            throw new ArgumentException(
                $"The policy \"{policy}\" looks like a route prefix: pass it as prefix: {method}(\"<policy>\", prefix: \"{policy}\").",
                nameof(policy));
        }
    }

    /// <summary>A change kept in the store: <c>204</c>, or <c>503</c> when the store cannot be reached.</summary>
    /// <remarks>
    /// The change itself runs with no cancellation token (it is bounded by <see cref="StoreTimeout"/> and the store's
    /// own timeouts): a client that disconnects must not cut a change short halfway. Only the wait ends with the
    /// request, and the endpoint then returns quietly.
    /// </remarks>
    /// <param name="call">The change.</param>
    /// <param name="cancellationToken">The request's token: only the wait ends with it.</param>
    /// <param name="letsAct">
    /// A change that lets Homeostat act (resume, turn a switch on): when its write times out, Homeostat decides again in
    /// the safe direction (pauses again, turns the switch off), so the answer says it was not made.
    /// </param>
    private static Task<IResult> StoreCallAsync(Func<ValueTask> call, CancellationToken cancellationToken, bool letsAct = false)
        => AnswerAsync(async () =>
        {
            await call().ConfigureAwait(false);
            return true;
        }, cancellationToken, letsAct);

    /// <inheritdoc cref="StoreCallAsync"/>
    private static async Task<IResult> AnswerAsync(Func<ValueTask<bool>> answer, CancellationToken cancellationToken, bool letsAct = false)
    {
        try
        {
            return await StoreCall.Run(answer, StoreTimeout, cancellationToken).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
        }
        catch (ArgumentException)
        {
            return Results.BadRequest();
        }
        catch (RefusedChangeException ex)
        {
            return HomeostatResults.Problem(ex.Message, StatusCodes.Status409Conflict);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client is gone: nobody reads the answer, and the change goes on by itself.
            return Results.Empty;
        }
        catch (Exception ex) when (StoreCall.IsOutcomeUnknown(ex))
        {
            return letsAct ? StoreTimedOutSafely() : StoreOutcomeUnknown();
        }
        catch (Exception)
        {
            return StoreUnavailable();
        }
    }

    // No exception details: a store's messages can name servers. GET /status reports that the store failed.
    private static IResult StoreUnavailable()
        => HomeostatResults.Problem("The Homeostat store is unavailable; nothing was changed (GET /status shows the error).", StatusCodes.Status503ServiceUnavailable);

    // A timeout says nothing about the outcome: the store may still apply the change after the answer.
    // A resume or a turn-on that times out is decided again in the safe direction (decision S4): it does not stand.
    private static IResult StoreTimedOutSafely()
        => HomeostatResults.Problem(
            "The Homeostat store did not answer in time; the change was not made: Homeostat (or the healing group) stays paused, or the switch stays off. Try again later.",
            StatusCodes.Status503ServiceUnavailable);

    private static IResult StoreOutcomeUnknown()
        => HomeostatResults.Problem(
            "The Homeostat store did not answer in time; the change may still be applied. Check GET /status before trying again.",
            StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> GetStatus(
        IHomeostatControl control,
        IHomeostatSwitches switches,
        IHomeostatApprovals approvals,
        IOptions<HomeostatOptions> options,
        ILoggerFactory loggers,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // The status stays available when the store is not: that is when operators need it most.
        BudgetStatus? budgetStatus = null;
        IReadOnlyList<RunInfo> runInfos = [];
        IReadOnlyList<PendingApproval> pending = [];
        string? storeError = null;
        // The reads together (the dashboard polls this), each kept if it succeeds: the budgets of the groups that have
        // one are read alongside the global budget, each bounded alike. The groups are fixed by the application's
        // configuration (only configured groups have a budget), so their number is bounded.
        var groups = control.GetGroups();
        var pendingRead = StoreCall.Run(() => approvals.GetPendingAsync(cancellationToken), StoreTimeout, cancellationToken);
        var budgetRead = StoreCall.Run(() => control.GetBudgetAsync(cancellationToken), StoreTimeout, cancellationToken);
        var runsRead = StoreCall.Run(() => control.GetRecentRunsAsync(cancellationToken), StoreTimeout, cancellationToken);
        var groupBudgetReads = groups
            .Where(g => g.MaxActionsPerWindow is not null)
            .ToDictionary(g => g.Name, g => StoreCall.Run(() => control.GetGroupBudgetAsync(g.Name, cancellationToken), StoreTimeout, cancellationToken), StringComparer.Ordinal);
        Task[] reads = [pendingRead, budgetRead, runsRead, .. groupBudgetReads.Values];
        try
        {
            await Task.WhenAll(reads).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.Empty;   // the client is gone
        }
        catch (Exception ex)
        {
            // No exception message: it can carry connection details (server names, user names). The type tells a
            // timeout from a refused connection; the details go to the log only.
            var error = reads.FirstOrDefault(t => t.IsFaulted)?.Exception?.InnerException ?? ex;
            storeError = $"The Homeostat store could not be read ({error.GetType().Name}); the application log has the details.";
            loggers.CreateLogger("Homeostat.AspNetCore").LogWarning(error, "GET /status: the Homeostat store could not be read.");
        }

        if (pendingRead.IsCompletedSuccessfully)
        {
            pending = pendingRead.Result;
        }

        if (budgetRead.IsCompletedSuccessfully)
        {
            budgetStatus = BudgetStatus.From(budgetRead.Result);
        }

        if (runsRead.IsCompletedSuccessfully)
        {
            runInfos = runsRead.Result.Select(RunInfo.From).ToArray();
        }

        var now = time.GetUtcNow();
        var groupInfos = groups.Count == 0 ? null : groups.Select(g => GroupStatusInfo.From(
            g,
            groupBudgetReads.TryGetValue(g.Name, out var read) && read.IsCompletedSuccessfully ? read.Result : null,
            now)).ToArray();

        var status = new HomeostatStatus(
            options.Value.ApplicationName,
            options.Value.InstanceId,
            control.IsPaused,
            control.PauseReason,
            control.IsDryRun,
            budgetStatus,
            control.GetConditions().Select(ConditionStatusInfo.From).ToArray(),
            switches.GetActive(),
            pending,
            runInfos,
            storeError,
            groupInfos);
        return Results.Json(status, HomeostatAspNetCoreJsonContext.Default.HomeostatStatus);
    }

    private static async Task<IResult> GetHistoryAsync(IHomeostatControl control, string? condition, CancellationToken cancellationToken)
    {
        try
        {
            var events = await StoreCall.Run(() => control.GetConditionHistoryAsync(cancellationToken), StoreTimeout, cancellationToken);
            return Results.Json(events
                .Where(e => condition is null || string.Equals(e.ConditionName, condition, StringComparison.Ordinal))
                .Select(ConditionEventInfo.From)
                .ToArray(), HomeostatAspNetCoreJsonContext.Default.ConditionEventInfoArray);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.Empty;   // the client is gone
        }
        catch (Exception)
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
            if (!DateTimeOffset.TryParse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out from)
                || from > now || now - from > TimeSpan.FromDays(365))
            {
                return HomeostatResults.BadRequest("'since' must be a date and time within the last 365 days, for example 2026-11-02T08:00:00Z.");
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
            var report = await StoreCall.Run(() => control.GetCoverageAsync(from, cancellationToken), StoreTimeout, cancellationToken);
            return Results.Json(report, HomeostatAspNetCoreJsonContext.Default.CoverageReport);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.Empty;   // the client is gone
        }
        catch (Exception)
        {
            // No exception details: they can carry store connection details.
            return HomeostatResults.Problem("The run history is unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string? UserName(ClaimsPrincipal user)
        => user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;

    /// <summary>
    /// A name from a route value. ASP.NET Core decodes every escape in the path except <c>%2F</c>, so that it cannot
    /// split segments: a name with a slash (<c>feed/eu</c>, sent as <c>feed%2Feu</c> by the dashboard and the CLI)
    /// arrives as <c>feed%2Feu</c>. Only that escape is decoded here: the rest is already decoded, and decoding it again
    /// would change names that contain <c>%</c>.
    /// </summary>
    internal static string RouteName(string value)
        => value.Contains('%', StringComparison.Ordinal) ? value.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase) : value;

    /// <summary>A switch or condition name from a URL: not empty, not too long, and without control characters (no line breaks in logs).</summary>
    internal static bool IsValidSwitchName(string name)
        => name.Length is > 0 and <= MaxSwitchNameLength && !name.Any(char.IsControl);

    /// <summary>
    /// A healing group name from a URL, validated as the core validates group names: not blank, at most 200 characters,
    /// no control characters.
    /// </summary>
    internal static bool IsValidGroupName(string name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= MaxGroupNameLength && !name.Any(char.IsControl);

    /// <summary>A condition name from a URL: not empty and without control characters (conditions have no length limit).</summary>
    internal static bool IsValidConditionName(string name)
        => name.Length > 0 && !name.Any(char.IsControl);
}
