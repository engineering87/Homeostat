using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Homeostat.AspNetCore;
using Homeostat.Signals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Homeostat;

/// <summary>Maps endpoints that turn alerts from monitoring systems into Homeostat signals.</summary>
public static class HomeostatAlertEndpointExtensions
{
    /// <summary>
    /// Maps endpoints that receive alert notifications and publish each alert as a signal, so a condition can heal
    /// what your monitoring already detects:
    /// <list type="bullet">
    /// <item><description><c>POST {prefix}/alertmanager</c>: Prometheus Alertmanager webhook receiver.</description></item>
    /// <item><description><c>POST {prefix}/grafana</c>: Grafana alerting webhook contact point (one numeric query value becomes the signal value).</description></item>
    /// <item><description><c>POST {prefix}/azure-monitor</c>: Azure Monitor action group webhook with the common alert schema.</description></item>
    /// <item><description><c>POST {prefix}/signals</c>: generic signals, <c>{"source":"...","severity":"Unhealthy","value":1.5,"message":"..."}</c> or an array of them.</description></item>
    /// </list>
    /// The source is <c>alert:</c> followed by the <c>alertname</c> label (the alert rule name in Azure Monitor). A
    /// firing alert is Unhealthy when its severity is critical (or Sev0/Sev1 in Azure Monitor) and
    /// <see cref="AlertIngestionOptions.DefaultSeverity"/> otherwise. The alerts of each source are kept as state: the
    /// source is as bad as its worst firing series (one alert name often covers several instances), Healthy only when
    /// none fires, and its signal is published again at every detection interval, so <c>SustainedFor</c> and
    /// <c>ClearAfter</c> work even though senders notify each change once. The state lives in the memory of the
    /// instance that receives the notifications. The endpoints answer <c>202 Accepted</c> with the number of accepted
    /// and skipped alerts.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="policy">Authorization policy required to call the endpoints.</param>
    /// <param name="prefix">Route prefix.</param>
    /// <param name="allowAnonymous"><c>true</c> maps the endpoints without a policy or token; only for local demos and tests.</param>
    /// <param name="configure">Changes the token, source and severity mapping, and limits.</param>
    /// <exception cref="ArgumentException">None of <paramref name="policy"/>, a token or <paramref name="allowAnonymous"/> was given.</exception>
    /// <example>
    /// <code>
    /// app.MapHomeostatAlerts(configure: o => o.Token = builder.Configuration["Homeostat:AlertToken"]);
    /// // Alertmanager receiver: url: https://app/homeostat/alerts/alertmanager, authorization credentials: the token.
    /// builder.Services.AddHomeostat(h => h.AddCondition("disk-almost-full", c => c
    ///     .ForSource("alert:DiskAlmostFull").WhenSeverityAtLeast(SignalSeverity.Degraded) // warnings are Degraded
    ///     .Heal(p => p.With&lt;PruneTempFilesAction&gt;())));
    /// </code>
    /// </example>
    public static RouteGroupBuilder MapHomeostatAlerts(
        this IEndpointRouteBuilder endpoints,
        string? policy = null,
        string prefix = "/homeostat/alerts",
        bool allowAnonymous = false,
        Action<AlertIngestionOptions>? configure = null)
        => MapAlerts(endpoints, prefix, configure, string.IsNullOrWhiteSpace(policy) ? null : g => g.RequireAuthorization(policy), allowAnonymous);

    /// <summary>
    /// Same as <see cref="MapHomeostatAlerts(IEndpointRouteBuilder, string?, string, bool, Action{AlertIngestionOptions}?)"/>,
    /// with the authorization policy built inline instead of registered by name.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configurePolicy">Builds the policy, for example <c>p => p.RequireRole("monitoring")</c>.</param>
    /// <param name="prefix">Route prefix.</param>
    /// <param name="configure">Changes the source and severity mapping, and limits.</param>
    /// <example><code>app.MapHomeostatAlerts(p => p.RequireAuthenticatedUser().AddAuthenticationSchemes("Certificate"));</code></example>
    public static RouteGroupBuilder MapHomeostatAlerts(
        this IEndpointRouteBuilder endpoints,
        Action<Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder> configurePolicy,
        string prefix = "/homeostat/alerts",
        Action<AlertIngestionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configurePolicy);
        return MapAlerts(endpoints, prefix, configure, g => g.RequireAuthorization(configurePolicy), allowAnonymous: false);
    }

    private static RouteGroupBuilder MapAlerts(
        IEndpointRouteBuilder endpoints,
        string prefix,
        Action<AlertIngestionOptions>? configure,
        Action<RouteGroupBuilder>? authorize,
        bool allowAnonymous)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var options = new AlertIngestionOptions();
        configure?.Invoke(options);
        options.Validate();

        if (authorize is null && options.Token is null && !allowAnonymous)
        {
            throw new ArgumentException(
                "Alert endpoints need an authorization policy or a token, for example " +
                "MapHomeostatAlerts(configure: o => o.Token = secret). For a local demo only, pass allowAnonymous: true.",
                "policy");
        }

        var group = endpoints.MapGroup(prefix).WithTags("Homeostat");
        if (authorize is not null)
        {
            authorize(group);
        }
        else if (options.Token is not null)
        {
            // The token is the protection: a global fallback authorization policy must not reject the senders.
            group.AllowAnonymous();
        }

        var services = endpoints.ServiceProvider;
        var state = new AlertState(options, services.GetRequiredService<ISignalPublisher>(),
            services.GetService<TimeProvider>() ?? TimeProvider.System);
        state.Start(services.GetService<IOptions<HomeostatOptions>>()?.Value.DetectionInterval ?? TimeSpan.FromSeconds(10));
        services.GetService<IHostApplicationLifetime>()?.ApplicationStopping.Register(state.Dispose);

        var token = options.Token is null ? null : Encoding.UTF8.GetBytes(options.Token);

        group.MapPost("/alertmanager", (HttpContext http, ISignalPublisher signals)
            => ReceiveAsync(http, signals, state, options, token, allowQueryToken: false, root => AlertParsers.ParseAlertmanager(root, options, "alertmanager")));
        group.MapPost("/grafana", (HttpContext http, ISignalPublisher signals)
            => ReceiveAsync(http, signals, state, options, token, allowQueryToken: false, root => AlertParsers.ParseAlertmanager(root, options, "grafana")));
        group.MapPost("/azure-monitor", (HttpContext http, ISignalPublisher signals)
            => ReceiveAsync(http, signals, state, options, token, allowQueryToken: true, root => AlertParsers.ParseAzureMonitor(root, options)));
        group.MapPost("/signals", (HttpContext http, ISignalPublisher signals, TimeProvider time)
            => ReceiveAsync(http, signals, state, options, token, allowQueryToken: false, root => AlertParsers.ParseSignals(root, options, time.GetUtcNow())));

        return group;
    }

    private static async Task<IResult> ReceiveAsync(
        HttpContext http,
        ISignalPublisher signals,
        AlertState state,
        AlertIngestionOptions options,
        byte[]? token,
        bool allowQueryToken,
        Func<JsonElement, AlertBatch> parse)
    {
        if (token is not null && !HasToken(http.Request, token, allowQueryToken))
        {
            return Results.Unauthorized();
        }

        // Alert senders are servers. A browser can be made to post a form to these routes with the operator's
        // cookies, but not with a JSON content type (that needs a CORS preflight) nor without Sec-Fetch-Site.
        if (http.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
        {
            return HomeostatResults.Problem("Alerts cannot be posted from another site.", StatusCodes.Status403Forbidden);
        }

        if (!http.Request.HasJsonContentType())
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (http.Request.ContentLength > options.MaxRequestBodySize)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = options.MaxRequestBodySize;
        }

        JsonDocument document;
        try
        {
            using var body = new LimitedStream(http.Request.Body, options.MaxRequestBodySize);
            document = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 32 }, http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return HomeostatResults.BadRequest("The body is not valid JSON.");
        }
        catch (PayloadTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using (document)
        {
            var batch = parse(document.RootElement);
            if (batch.Invalid is { } reason)
            {
                return HomeostatResults.BadRequest(reason);
            }

            var accepted = state.Apply(batch.Alerts);
            foreach (var signal in batch.Signals)
            {
                if (signals.TryPublish(signal))
                {
                    accepted++;
                }
            }

            return Results.Json(
                new AlertIngestionResult(accepted, batch.Skipped + (batch.Count - accepted)),
                HomeostatAspNetCoreJsonContext.Default.AlertIngestionResult,
                statusCode: StatusCodes.Status202Accepted);
        }
    }

    private static bool HasToken(HttpRequest request, byte[] expected, bool allowQuery)
    {
        string? presented = null;
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            presented = header["Bearer ".Length..].Trim();
        }
        else if (allowQuery && request.Query.TryGetValue("token", out var query) && query.Count == 1)
        {
            presented = query[0];
        }

        return presented is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), expected);
    }

    private sealed class PayloadTooLargeException : IOException;

    /// <summary>Read-only stream that fails once more than the allowed number of bytes has been read.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new PayloadTooLargeException() : read;
        }
    }
}
