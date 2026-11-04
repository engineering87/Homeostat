using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Homeostat.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Http;

/// <summary>
/// Base of the HTTP notifiers: sends JSON with a timeout per attempt and retries network errors, timeouts,
/// 408, 429 and 5xx answers with a growing delay. Other 4xx answers are not retried: the request itself is wrong.
/// </summary>
internal abstract class HttpEventSink : IHomeostatEventSink
{
    /// <summary>Writes payloads: no reflection, so the sinks work under trimming and Native AOT.</summary>
    internal static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private readonly IHttpClientFactory _clients;
    private readonly string _clientName;
    private readonly TimeProvider _timeProvider;

    private readonly IHostApplicationLifetime? _lifetime;

    protected HttpEventSink(
        IHttpClientFactory clients,
        string clientName,
        IEnumerable<HomeostatEventKind> kinds,
        TimeProvider timeProvider,
        ILogger logger,
        IHostApplicationLifetime? lifetime)
    {
        _clients = clients;
        _clientName = clientName;
        _timeProvider = timeProvider;
        Logger = logger;
        Events = kinds.ToArray();
        _lifetime = lifetime;
    }

    public IReadOnlyCollection<HomeostatEventKind>? Events { get; }

    protected ILogger Logger { get; }

    /// <summary>
    /// <c>true</c> once the application is stopping: deliver quickly (one short attempt), because the remaining
    /// shutdown time is shared with every other sink.
    /// </summary>
    protected bool IsStopping => _lifetime?.ApplicationStopping.IsCancellationRequested == true;

    public abstract Task WriteAsync(HomeostatEvent homeostatEvent, CancellationToken cancellationToken);

    protected abstract TimeSpan Timeout { get; }

    protected abstract int MaxAttempts { get; }

    /// <summary>Sends <paramref name="payload"/> as JSON, retrying transient failures.</summary>
    protected async Task SendAsync(
        HttpMethod method,
        Uri url,
        JsonNode payload,
        IEnumerable<KeyValuePair<string, string>>? headers,
        CancellationToken cancellationToken)
    {
        var client = _clients.CreateClient(_clientName);
        var body = payload.ToJsonString(Json);

        // While the application stops, the remaining time is shared with the other sinks: one short attempt only.
        var maxAttempts = IsStopping ? 1 : MaxAttempts;
        var attemptTimeout = IsStopping && Timeout > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : Timeout;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                foreach (var (name, value) in headers ?? [])
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                using var timeout = new CancellationTokenSource(attemptTimeout, _timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                throw new DeliveryException(response.StatusCode, IsTransient(response.StatusCode), RetryAfter(response));
            }
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested
                && (ex is not DeliveryException delivery || delivery.Transient))
            {
                Logger.LogDebug(ex, "Delivery attempt {Attempt} to {Host} failed; retrying.", attempt, url.Host);
                var delay = (ex as DeliveryException)?.RetryAfter ?? TimeSpan.FromSeconds(attempt * 2);
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(HttpJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>A JSON object without the properties whose value is null, as receivers expect.</summary>
    internal static JsonObject ObjectOf(params (string Name, JsonNode? Value)[] properties)
    {
        var result = new JsonObject();
        foreach (var (name, value) in properties)
        {
            if (value is not null)
            {
                result[name] = value;
            }
        }

        return result;
    }

    /// <summary>A JSON array of the given items, without the null ones.</summary>
    internal static JsonArray ArrayOf(params JsonNode?[] items) => new(items.Where(i => i is not null).ToArray());

    /// <summary>The receiver's Retry-After on 429 or 503, capped at one minute.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter?.Delta;
        return delay is { } d && d > TimeSpan.Zero ? (d > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : d) : null;
    }

    private static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>The receiver answered with an error status.</summary>
    internal sealed class DeliveryException(HttpStatusCode status, bool transient, TimeSpan? retryAfter)
        : HttpRequestException($"The receiver answered {(int)status} ({status}).", null, status)
    {
        public bool Transient { get; } = transient;

        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}

/// <summary>The types the HTTP sinks serialize, with the settings receivers expect.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(HomeostatEvent))]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class HttpJsonContext : JsonSerializerContext;
