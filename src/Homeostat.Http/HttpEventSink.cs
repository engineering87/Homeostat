using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Homeostat.Notifications;
using Microsoft.Extensions.Logging;

namespace Homeostat.Http;

/// <summary>
/// Base of the HTTP notifiers: sends JSON with a timeout per attempt and retries network errors, timeouts,
/// 408, 429 and 5xx answers with a growing delay. Other 4xx answers are not retried: the request itself is wrong.
/// </summary>
internal abstract class HttpEventSink : HomeostatEventSink
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _clients;
    private readonly string _clientName;
    private readonly TimeProvider _timeProvider;

    protected HttpEventSink(
        IHttpClientFactory clients,
        string clientName,
        string application,
        IEnumerable<HomeostatEventKind> kinds,
        TimeProvider timeProvider,
        ILogger logger)
        : base(application, kinds, timeProvider, logger)
    {
        _clients = clients;
        _clientName = clientName;
        _timeProvider = timeProvider;
    }

    protected abstract TimeSpan Timeout { get; }

    protected abstract int MaxAttempts { get; }

    /// <summary>Sends <paramref name="payload"/> as JSON, retrying transient failures.</summary>
    protected async Task SendAsync(
        HttpMethod method,
        Uri url,
        object payload,
        IEnumerable<KeyValuePair<string, string>>? headers,
        CancellationToken cancellationToken)
    {
        var client = _clients.CreateClient(_clientName);

        // While the application stops, the remaining time is shared with the other sinks: one short attempt only.
        var maxAttempts = IsStopping ? 1 : MaxAttempts;
        var attemptTimeout = IsStopping && Timeout > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : Timeout;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url)
                {
                    Content = JsonContent.Create(payload, payload.GetType(), options: Json),
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
