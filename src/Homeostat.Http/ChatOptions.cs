using Homeostat.Notifications;

namespace Homeostat.Http;

/// <summary>Settings shared by the Slack and Microsoft Teams notifiers.</summary>
public abstract class ChatOptions
{
    /// <summary>Incoming webhook URL. Required; keep it in a secret store.</summary>
    public Uri? WebhookUrl { get; set; }

    /// <summary>
    /// Events to post. By default: condition activated and cleared, run completed, approval requested and resolved.
    /// </summary>
    public ISet<HomeostatEventKind> Events { get; } = new HashSet<HomeostatEventKind>
    {
        HomeostatEventKind.ConditionActivated,
        HomeostatEventKind.ConditionCleared,
        HomeostatEventKind.RunCompleted,
        HomeostatEventKind.ApprovalRequested,
        HomeostatEventKind.ApprovalResolved,
    };

    /// <summary>Address of the Homeostat dashboard (<c>MapHomeostatDashboard</c>); messages link to it when set.</summary>
    public Uri? DashboardUrl { get; set; }

    /// <summary>Timeout of one delivery attempt. Defaults to 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delivery attempts, including the first. Defaults to 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    internal void Validate()
    {
        if (WebhookUrl is not { IsAbsoluteUri: true } || (DashboardUrl is not null && !DashboardUrl.IsAbsoluteUri))
        {
            throw new ArgumentException("WebhookUrl is required, and WebhookUrl and DashboardUrl must be absolute.");
        }

        if (Timeout <= TimeSpan.Zero || MaxAttempts < 1)
        {
            throw new ArgumentException("Timeout must be positive and MaxAttempts at least 1.");
        }
    }
}

/// <summary>Settings of the Slack notifier.</summary>
public sealed class SlackOptions : ChatOptions
{
    /// <summary>
    /// Adds <b>Approve</b> and <b>Reject</b> buttons to approval requests. Requires the webhook to belong to a Slack app
    /// with interactivity enabled, whose request URL is the endpoint mapped by <c>MapHomeostatSlackInteractions</c>.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool ApprovalButtons { get; set; }
}

/// <summary>Settings of the Microsoft Teams notifier (a Teams workflow that posts an Adaptive Card to a channel).</summary>
public sealed class TeamsOptions : ChatOptions
{
}
