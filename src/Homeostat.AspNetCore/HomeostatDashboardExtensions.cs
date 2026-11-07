using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Homeostat;

/// <summary>Maps the embedded Homeostat dashboard.</summary>
public static class HomeostatDashboardExtensions
{
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Lazy<(string Name, byte[] Content, string ContentType)[]> Assets = new(Load);

    /// <summary>
    /// Maps <c>GET {prefix}/dashboard</c>: a page that shows conditions, pending approvals, switches, the action budget,
    /// recent runs, the condition history and the coverage of the last 24 hours, refreshed every 5 seconds, with buttons to approve or reject steps, pause and resume, and
    /// turn switches on and off. It reads <c>{prefix}/status</c> and calls the control endpoints, so map
    /// <see cref="HomeostatEndpointRouteBuilderExtensions.MapHomeostatStatus(IEndpointRouteBuilder, string?, string, bool)"/> and
    /// <see cref="HomeostatEndpointRouteBuilderExtensions.MapHomeostatControl(IEndpointRouteBuilder, string?, string, bool)"/> with the same prefix (without the
    /// control endpoints, the dashboard is read-only). The page is self-contained (no external scripts, fonts or
    /// images) and served with a strict Content-Security-Policy.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="policy">Authorization policy required to open the dashboard; use the same as the status and control endpoints.</param>
    /// <param name="prefix">Route prefix, the same as the status and control endpoints.</param>
    /// <param name="allowAnonymous"><c>true</c> maps the dashboard without authorization; only for local demos and tests.</param>
    /// <exception cref="ArgumentException">
    /// Neither <paramref name="policy"/> nor <paramref name="allowAnonymous"/> was given, or <paramref name="policy"/>
    /// starts with <c>/</c> (a route prefix passed by position).
    /// </exception>
    /// <example>
    /// <code>
    /// app.MapHomeostatStatus("operations");
    /// app.MapHomeostatControl("operations");
    /// app.MapHomeostatDashboard("operations");   // GET /homeostat/dashboard
    /// </code>
    /// </example>
    public static RouteGroupBuilder MapHomeostatDashboard(
        this IEndpointRouteBuilder endpoints,
        string? policy = null,
        string prefix = "/homeostat",
        bool allowAnonymous = false)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        HomeostatEndpointRouteBuilderExtensions.RefuseRouteAsPolicy(policy, nameof(MapHomeostatDashboard));
        if (string.IsNullOrWhiteSpace(policy) && !allowAnonymous)
        {
            throw new ArgumentException(
                "The Homeostat dashboard needs an authorization policy, for example MapHomeostatDashboard(\"operations\"). " +
                "For a local demo only, pass allowAnonymous: true.",
                nameof(policy));
        }

        var group = endpoints.MapGroup(prefix).WithTags("Homeostat").ExcludeFromDescription();
        if (!string.IsNullOrWhiteSpace(policy))
        {
            group.RequireAuthorization(policy);
        }

        foreach (var (name, content, contentType) in Assets.Value)
        {
            var path = name == "dashboard.html" ? "/dashboard" : "/" + name;
            group.MapGet(path, (HttpContext http) =>
            {
                // Routing also matches ".../dashboard/", where the page's relative URLs would point one level too deep.
                if (http.Request.Path.Value is { } requested && requested.EndsWith('/'))
                {
                    return Results.Redirect(http.Request.PathBase + requested.TrimEnd('/'));
                }

                var headers = http.Response.Headers;
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers.CacheControl = "no-cache";
                return Results.Bytes(content, contentType);
            });
        }

        return group;
    }

    private static (string Name, byte[] Content, string ContentType)[] Load()
    {
        var assembly = typeof(HomeostatDashboardExtensions).Assembly;
        return
        [
            Read(assembly, "dashboard.html", "text/html; charset=utf-8"),
            Read(assembly, "dashboard.css", "text/css; charset=utf-8"),
            Read(assembly, "dashboard.js", "text/javascript; charset=utf-8"),
        ];
    }

    private static (string Name, byte[] Content, string ContentType) Read(Assembly assembly, string name, string contentType)
    {
        using var stream = assembly.GetManifestResourceStream($"Homeostat.AspNetCore.Dashboard.{name}")
            ?? throw new InvalidOperationException($"The dashboard asset {name} is missing from the assembly.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return (name, copy.ToArray(), contentType);
    }

    /// <summary>
    /// Same as <see cref="MapHomeostatDashboard(IEndpointRouteBuilder, string?, string, bool)"/>, with the authorization policy built
    /// inline instead of registered by name.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configurePolicy">Builds the policy, for example <c>p => p.RequireRole("operations")</c>.</param>
    /// <param name="prefix">Route prefix.</param>
    /// <example><code>app.MapHomeostatDashboard(p => p.RequireRole("operations"));</code></example>
    public static RouteGroupBuilder MapHomeostatDashboard(
        this IEndpointRouteBuilder endpoints,
        Action<Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder> configurePolicy,
        string prefix = "/homeostat")
    {
        ArgumentNullException.ThrowIfNull(configurePolicy);
        var group = MapHomeostatDashboard(endpoints, policy: null, prefix, allowAnonymous: true);
        group.RequireAuthorization(configurePolicy);
        return group;
    }
}
