using Homeostat.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Http;

internal sealed class WebhookSink : HttpEventSink
{
    private readonly WebhookOptions _options;

    public WebhookSink(
        IHttpClientFactory clients,
        IOptions<WebhookOptions> webhook,
        IOptions<HomeostatOptions> options,
        TimeProvider timeProvider,
        ILogger<WebhookSink> logger)
        : base(clients, WebhookOptions.HttpClientName, options.Value.ApplicationName, webhook.Value.Events, timeProvider, logger)
        => _options = webhook.Value;

    protected override TimeSpan Timeout => _options.Timeout;

    protected override int MaxAttempts => _options.MaxAttempts;

    protected override Task WriteAsync(HomeostatEvent homeostatEvent, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, _options.Url!, _options.CreatePayload(homeostatEvent), _options.Headers, cancellationToken);
}
