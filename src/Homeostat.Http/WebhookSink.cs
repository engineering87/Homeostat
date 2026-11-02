using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Homeostat.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Http;

internal sealed class WebhookSink : HomeostatEventSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _clients;
    private readonly WebhookOptions _options;
    private readonly TimeProvider _timeProvider;

    public WebhookSink(
        IHttpClientFactory clients,
        IOptions<WebhookOptions> webhook,
        IOptions<HomeostatOptions> options,
        TimeProvider timeProvider,
        ILogger<WebhookSink> logger)
        : base(options.Value.ApplicationName, webhook.Value.Events, timeProvider, logger)
    {
        _clients = clients;
        _options = webhook.Value;
        _timeProvider = timeProvider;
    }

    protected override async Task WriteAsync(HomeostatEvent homeostatEvent, CancellationToken cancellationToken)
    {
        var payload = _options.CreatePayload(homeostatEvent);
        var client = _clients.CreateClient(WebhookOptions.HttpClientName);

        // While the application stops, the remaining time is shared with the other sinks: one short attempt only.
        var maxAttempts = IsStopping ? 1 : _options.MaxAttempts;
        var attemptTimeout = IsStopping && _options.Timeout > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : _options.Timeout;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url)
                {
                    Content = JsonContent.Create(payload, payload.GetType(), options: Json),
                };
                foreach (var (name, value) in _options.Headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                using var timeout = new CancellationTokenSource(attemptTimeout, _timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                Logger.LogDebug(ex, "Webhook delivery attempt {Attempt} failed; retrying.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
