using Homeostat.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Http;

/// <summary>Posts events to Slack with Block Kit, with optional approval buttons.</summary>
internal sealed class SlackSink : HttpEventSink
{
    internal const string HttpClientName = "Homeostat.Slack";
    internal const string ApproveAction = "homeostat_approve";
    internal const string RejectAction = "homeostat_reject";
    private readonly SlackOptions _options;

    public SlackSink(
        IHttpClientFactory clients,
        IOptions<SlackOptions> slack,
        IOptions<HomeostatOptions> options,
        TimeProvider timeProvider,
        ILogger<SlackSink> logger)
        : base(clients, HttpClientName, options.Value.ApplicationName, slack.Value.Events, timeProvider, logger)
        => _options = slack.Value;

    protected override TimeSpan Timeout => _options.Timeout;

    protected override int MaxAttempts => _options.MaxAttempts;

    protected override Task WriteAsync(HomeostatEvent e, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, _options.WebhookUrl!, Payload(e, _options), null, cancellationToken);

    internal static object Payload(HomeostatEvent e, SlackOptions options)
    {
        var text = Limit($"{Icon(e)} *{Escape(e.Application)}* · {Escape(e.Summary)}", 3000);
        var blocks = new List<object>
        {
            new { type = "section", text = new { type = "mrkdwn", text } },
            new
            {
                type = "context",
                elements = new object[] { new { type = "mrkdwn", text = Limit($"Condition `{Escape(e.Condition)}` · {e.Kind}", 3000) } },
            },
        };

        var buttons = new List<object>();
        if (e.Kind == HomeostatEventKind.ApprovalRequested && options.ApprovalButtons && e.ApprovalId is { } id)
        {
            buttons.Add(new
            {
                type = "button",
                action_id = ApproveAction,
                style = "primary",
                text = new { type = "plain_text", text = "Approve" },
                value = id,
                confirm = new
                {
                    title = new { type = "plain_text", text = "Approve this step?" },
                    text = new { type = "plain_text", text = Truncate($"{e.Condition}: {e.Step}", 300) },
                    confirm = new { type = "plain_text", text = "Approve" },
                    deny = new { type = "plain_text", text = "Cancel" },
                },
            });
            buttons.Add(new
            {
                type = "button",
                action_id = RejectAction,
                style = "danger",
                text = new { type = "plain_text", text = "Reject" },
                value = id,
            });
        }

        if (options.DashboardUrl is { } dashboard)
        {
            buttons.Add(new { type = "button", action_id = "homeostat_open", text = new { type = "plain_text", text = "Open dashboard" }, url = dashboard.AbsoluteUri });
        }

        if (buttons.Count > 0)
        {
            blocks.Add(new { type = "actions", block_id = "homeostat", elements = buttons });
        }

        // "text" is the fallback for notifications and screen readers.
        return new { text = Limit(Escape($"[{e.Application}] {e.Summary}"), 3000), blocks };
    }

    /// <summary>Slack control characters: without escaping, text from an alert could mention @channel or forge links.</summary>
    internal static string Escape(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>Cuts escaped text to Slack's limit without splitting an entity such as <c>&amp;amp;</c>.</summary>
    internal static string Limit(string escaped, int max)
    {
        if (escaped.Length <= max)
        {
            return escaped;
        }

        var cut = escaped[..(max - 1)];
        var amp = cut.LastIndexOf('&');
        if (amp >= 0 && cut.IndexOf(';', amp) < 0)
        {
            cut = cut[..amp];
        }

        return cut + "…";
    }

    private static string Icon(HomeostatEvent e) => e.Kind switch
    {
        HomeostatEventKind.ConditionActivated => ":red_circle:",
        HomeostatEventKind.ConditionCleared => ":large_green_circle:",
        HomeostatEventKind.ApprovalRequested => ":raised_hand:",
        HomeostatEventKind.RunCompleted when e.Outcome is "Healed" or "ConditionCleared" => ":white_check_mark:",
        HomeostatEventKind.RunCompleted => ":warning:",
        _ => ":information_source:",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Posts events to Microsoft Teams as Adaptive Cards, through a Teams workflow webhook.</summary>
internal sealed class TeamsSink : HttpEventSink
{
    internal const string HttpClientName = "Homeostat.Teams";
    private readonly TeamsOptions _options;

    public TeamsSink(
        IHttpClientFactory clients,
        IOptions<TeamsOptions> teams,
        IOptions<HomeostatOptions> options,
        TimeProvider timeProvider,
        ILogger<TeamsSink> logger)
        : base(clients, HttpClientName, options.Value.ApplicationName, teams.Value.Events, timeProvider, logger)
        => _options = teams.Value;

    protected override TimeSpan Timeout => _options.Timeout;

    protected override int MaxAttempts => _options.MaxAttempts;

    protected override Task WriteAsync(HomeostatEvent e, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, _options.WebhookUrl!, Payload(e, _options), null, cancellationToken);

    internal static object Payload(HomeostatEvent e, TeamsOptions options)
    {
        var facts = new List<object> { new { title = "Condition", value = e.Condition } };
        if (e.Step is { } step)
        {
            facts.Add(new { title = "Step", value = step });
        }

        if (e.Outcome is { } outcome)
        {
            facts.Add(new { title = "Outcome", value = outcome });
        }

        if (e.ExpiresAt is { } expires)
        {
            facts.Add(new { title = "Expires", value = expires.ToString("u", System.Globalization.CultureInfo.InvariantCulture) });
        }

        var actions = options.DashboardUrl is { } dashboard
            ? new object[] { new { type = "Action.OpenUrl", title = e.Kind == HomeostatEventKind.ApprovalRequested ? "Review in dashboard" : "Open dashboard", url = dashboard.AbsoluteUri } }
            : [];

        return new
        {
            type = "message",
            attachments = new object[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new Dictionary<string, object>
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = new object[]
                        {
                            new { type = "TextBlock", text = $"{e.Application}: {e.Kind}", weight = "Bolder", size = "Medium", wrap = true },
                            new { type = "TextBlock", text = e.Summary, wrap = true },
                            new { type = "FactSet", facts },
                        },
                        ["actions"] = actions,
                    },
                },
            },
        };
    }
}
