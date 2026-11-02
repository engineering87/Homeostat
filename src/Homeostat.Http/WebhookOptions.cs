using Homeostat.Notifications;

namespace Homeostat.Http;

/// <summary>Settings of the webhook notifier (see <see cref="HomeostatWebhookExtensions.AddWebhookNotifications"/>).</summary>
public sealed class WebhookOptions
{
    /// <summary>Name of the <see cref="HttpClient"/> used to send notifications; configure it with <c>AddHttpClient(WebhookOptions.HttpClientName)</c>.</summary>
    public const string HttpClientName = "Homeostat.Webhook";

    /// <summary>Endpoint that receives a <c>POST</c> with a JSON body for each event. Required.</summary>
    public Uri? Url { get; set; }

    /// <summary>
    /// Events to send. By default: condition activated and cleared, run completed, approval requested and
    /// resolved. Step events are left out to keep channels quiet.
    /// </summary>
    public ISet<HomeostatEventKind> Events { get; } = new HashSet<HomeostatEventKind>
    {
        HomeostatEventKind.ConditionActivated,
        HomeostatEventKind.ConditionCleared,
        HomeostatEventKind.RunCompleted,
        HomeostatEventKind.ApprovalRequested,
        HomeostatEventKind.ApprovalResolved,
    };

    /// <summary>Headers added to every request, for example an authorization token.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the JSON body from an event. By default the event itself is sent. Return an anonymous object to match
    /// what the receiver expects, for example <c>e =&gt; new { text = e.Summary }</c> for a Slack incoming webhook.
    /// </summary>
    public Func<HomeostatEvent, object> CreatePayload { get; set; } = static e => e;

    /// <summary>Maximum time for one delivery attempt.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Attempts per event, including the first; failed deliveries are retried after a short delay.</summary>
    public int MaxAttempts { get; set; } = 3;
}
