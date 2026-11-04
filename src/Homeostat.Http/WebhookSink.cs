using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        TimeProvider timeProvider,
        ILogger<WebhookSink> logger,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
        : base(clients, WebhookOptions.HttpClientName, webhook.Value.Events, timeProvider, logger, lifetime)
        => _options = webhook.Value;

    protected override TimeSpan Timeout => _options.Timeout;

    protected override int MaxAttempts => _options.MaxAttempts;

    public override Task WriteAsync(HomeostatEvent homeostatEvent, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, _options.Url!, ToNode(_options.CreatePayload(homeostatEvent)), _options.Headers, cancellationToken);

    /// <summary>
    /// The payload as JSON. A <see cref="JsonNode"/> and the event itself need no reflection; any other object (for
    /// example an anonymous one) is serialized with reflection, which trimmed and Native AOT applications do not have.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only for payload types the application chose; documented on WebhookOptions.CreatePayload, and guarded below.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only for payload types the application chose; documented on WebhookOptions.CreatePayload, and guarded below.")]
    internal static JsonNode ToNode(object? payload) => payload switch
    {
        null => throw new InvalidOperationException("WebhookOptions.CreatePayload returned null."),
        JsonNode node => node,
        HomeostatEvent e => JsonSerializer.SerializeToNode(e, HttpJsonContext.Default.HomeostatEvent)!,
        _ when !JsonSerializer.IsReflectionEnabledByDefault => throw new InvalidOperationException(
            $"WebhookOptions.CreatePayload returned a {payload.GetType().Name}, which needs reflection to serialize: with trimming or Native AOT, return a JsonNode (for example a JsonObject)."),
        _ => JsonSerializer.SerializeToNode(payload, payload.GetType(), ReflectionJson)!,
    };

    private static readonly JsonSerializerOptions ReflectionJson = CreateReflectionJson();

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Used only when reflection-based serialization is enabled, which Native AOT applications do not have.")]
    private static JsonSerializerOptions CreateReflectionJson() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
