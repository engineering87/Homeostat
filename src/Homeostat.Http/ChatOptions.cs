using Homeostat.Notifications;

namespace Homeostat.Http;

/// <summary>Settings shared by the Slack and Microsoft Teams notifiers.</summary>
public abstract class ChatOptions
{
    /// <summary>Only the built-in options derive from this class: a derived class could not be registered.</summary>
    private protected ChatOptions()
    {
    }

    /// <summary>Incoming webhook URL. Required; keep it in a secret store.</summary>
    public Uri? Url { get; set; }

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
        if (Url is not { IsAbsoluteUri: true } || (DashboardUrl is not null && !DashboardUrl.IsAbsoluteUri))
        {
            throw new ArgumentException("Url is required, and Url and DashboardUrl must be absolute.");
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
    /// <summary>Name of the <see cref="HttpClient"/> used to post; configure it (proxy, handlers) with <c>AddHttpClient(SlackOptions.HttpClientName)</c>.</summary>
    public const string HttpClientName = "Homeostat.Slack";

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
    /// <summary>Name of the <see cref="HttpClient"/> used to post; configure it (proxy, handlers) with <c>AddHttpClient(TeamsOptions.HttpClientName)</c>.</summary>
    public const string HttpClientName = "Homeostat.Teams";

    /// <summary>
    /// Name of the Teams outgoing webhook mapped with <c>MapHomeostatTeamsCommands</c>, for example <c>Homeostat</c>.
    /// When set, approval request cards tell people how to answer from the channel
    /// (<c>@Homeostat approve &lt;id&gt;</c>). The webhook must belong to the team of the channel the workflow posts to.
    /// </summary>
    public string? ApprovalCommand { get; set; }
}
