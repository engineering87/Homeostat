using System.Globalization;
using System.Net.Http.Headers;
using Aspire.Hosting.ApplicationModel;
using Homeostat.Aspire.Hosting;

namespace Aspire.Hosting;

/// <summary>Homeostat in the Aspire app host.</summary>
public static class HomeostatResourceBuilderExtensions
{
    /// <summary>
    /// Shows Homeostat on a resource of the Aspire dashboard: a <b>Homeostat</b> link to the application's Homeostat
    /// dashboard, and the commands <b>Pause healing</b> and <b>Resume healing</b>, for example while debugging a failure
    /// that Homeostat would otherwise heal. The commands are enabled when the resource is running and healthy.
    /// </summary>
    /// <typeparam name="T">Resource type.</typeparam>
    /// <param name="builder">The resource builder, for example of <c>AddProject</c>.</param>
    /// <param name="configure">Prefix, link, commands, pause duration, endpoint and token.</param>
    /// <example>
    /// <code>
    /// builder.AddProject&lt;Projects.WeatherApi&gt;("weather-api")
    ///        .WithHomeostat();
    /// </code>
    /// </example>
    public static IResourceBuilder<T> WithHomeostat<T>(this IResourceBuilder<T> builder, Action<HomeostatResourceOptions>? configure = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new HomeostatResourceOptions();
        configure?.Invoke(options);
        var prefix = NormalizePrefix(options.Prefix);
        if (options.PauseFor is { } pause && (pause <= TimeSpan.Zero || pause > TimeSpan.FromDays(30)))
        {
            throw new ArgumentOutOfRangeException(nameof(configure), pause, "PauseFor must be positive and at most 30 days.");
        }

        if (options.DashboardLink)
        {
            builder.WithUrls(context =>
            {
                var endpoint = PickEndpoint(context.Urls, options.EndpointName);
                if (endpoint?.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
                {
                    // No Endpoint on the annotation: Aspire rewrites the path of endpoint URLs to the launch profile's
                    // launchUrl, which would turn this link into, say, /swagger.
                    context.Urls.Add(new ResourceUrlAnnotation
                    {
                        Url = DashboardUrl(baseUri, prefix).ToString(),
                        DisplayText = "Homeostat",
                    });
                }
            });
        }

        if (options.Commands)
        {
            // Aspire builds the request URI from the path alone (a query in it would be escaped): the duration is added
            // when the request is prepared.
            var pauseQuery = PauseQuery(options.PauseFor);
            builder.WithHttpCommand($"{prefix}/pause", "Pause healing", options.EndpointName, "homeostat-pause", new HttpCommandOptions
            {
                Description = options.PauseFor is { } span
                    ? $"Stops Homeostat from starting healing steps for {Describe(span)}; conditions are still tracked."
                    : "Stops Homeostat from starting healing steps until resumed; conditions are still tracked.",
                ConfirmationMessage = "Pause self-healing for this application?",
                IconName = "Pause",
                IconVariant = IconVariant.Regular,
                PrepareRequest = context => PrepareAsync(context, options, pauseQuery),
            });
            builder.WithHttpCommand($"{prefix}/resume", "Resume healing", options.EndpointName, "homeostat-resume", new HttpCommandOptions
            {
                Description = "Lets Homeostat heal again.",
                IconName = "Play",
                IconVariant = IconVariant.Regular,
                PrepareRequest = context => PrepareAsync(context, options, query: null),
            });
        }

        return builder;
    }

    internal static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var trimmed = "/" + prefix.Trim().Trim('/');
        if (trimmed.Contains("//", StringComparison.Ordinal) || trimmed.Contains('?', StringComparison.Ordinal) || trimmed.Contains('#', StringComparison.Ordinal))
        {
            throw new ArgumentException("The prefix must be a path, for example /homeostat.", nameof(prefix));
        }

        return trimmed;
    }

    /// <summary>The dashboard under the root of the endpoint, whatever path the endpoint URL carries (a launchUrl).</summary>
    internal static Uri DashboardUrl(Uri endpoint, string prefix) => new(endpoint, $"{prefix}/dashboard");

    internal static string? PauseQuery(TimeSpan? pauseFor)
        => pauseFor is { } span ? $"for={Uri.EscapeDataString(span.ToString("c", CultureInfo.InvariantCulture))}" : null;

    internal static Uri WithQuery(Uri uri, string? query) => query is null ? uri : new UriBuilder(uri) { Query = query }.Uri;

    private static string Describe(TimeSpan span)
        => span.TotalHours >= 1 && span.TotalHours == Math.Floor(span.TotalHours)
            ? $"{span.TotalHours:0} hour{(span.TotalHours == 1 ? string.Empty : "s")}"
            : $"{span.TotalMinutes:0} minutes";

    private static ResourceUrlAnnotation? PickEndpoint(List<ResourceUrlAnnotation> urls, string? endpointName)
        => endpointName is not null
            ? urls.FirstOrDefault(u => u.Endpoint?.EndpointName == endpointName)
            : urls.FirstOrDefault(u => u.Endpoint is { IsHttps: true }) ?? urls.FirstOrDefault(u => u.Endpoint is { IsHttp: true });

    /// <summary>The control endpoints refuse requests without a bearer token or <c>X-Requested-With</c> (cross-site request forgery).</summary>
    private static async Task PrepareAsync(HttpCommandRequestContext context, HomeostatResourceOptions options, string? query)
    {
        if (context.Request.RequestUri is { } uri)
        {
            context.Request.RequestUri = WithQuery(uri, query);
        }

        context.Request.Headers.TryAddWithoutValidation("X-Requested-With", "aspire-dashboard");
        if (options.BearerToken is { } token && await token(context.Services, context.CancellationToken).ConfigureAwait(false) is { Length: > 0 } value)
        {
            context.Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
        }
    }
}
