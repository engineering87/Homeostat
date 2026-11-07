using Homeostat.Http;
using Homeostat.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Homeostat;

/// <summary>Registers the webhook notifier.</summary>
public static class HomeostatWebhookExtensions
{
    /// <summary>
    /// Sends Homeostat events to an HTTP endpoint as JSON, in the background. Works with any receiver; adapt the body
    /// with <see cref="WebhookOptions.CreatePayload"/> (for example for Slack or Microsoft Teams).
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Sets the URL and, optionally, the events, headers and payload.</param>
    /// <example>
    /// <code>
    /// .AddWebhookNotifications(o =>
    /// {
    ///     o.Url = new Uri(builder.Configuration["Homeostat:Url"]!);
    ///     o.CreatePayload = e => new JsonObject { ["text"] = $"[{e.Application}] {e.Summary}" };
    /// })
    /// </code>
    /// </example>
    public static HomeostatBuilder AddWebhookNotifications(this HomeostatBuilder builder, Action<WebhookOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddOptions<WebhookOptions>()
            .Configure(configure)
            .Validate(o => o.Url is { IsAbsoluteUri: true }, "WebhookOptions.Url must be an absolute URI.")
            .Validate(o => o.Timeout > TimeSpan.Zero, "WebhookOptions.Timeout must be positive.")
            .Validate(o => o.MaxAttempts >= 1, "WebhookOptions.MaxAttempts must be at least 1.")
            .ValidateOnStart();

        // The default HttpClient logging writes the request URL, and webhook URLs often carry a secret.
        builder.Services.AddHttpClient(WebhookOptions.HttpClientName).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(HomeostatHttpHandlers.WithoutRedirects);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.AddEventSink<WebhookSink>();
        return builder;
    }
}
