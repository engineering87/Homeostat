using System.Text.Json.Nodes;
using Homeostat.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Http;

/// <summary>Posts events to Slack with Block Kit, with optional approval buttons.</summary>
internal sealed class SlackSink : HttpEventSink
{
    internal const string HttpClientName = SlackOptions.HttpClientName;
    internal const string ApproveAction = "homeostat_approve";
    internal const string RejectAction = "homeostat_reject";
    private readonly SlackOptions _options;

    public SlackSink(
        IHttpClientFactory clients,
        IOptions<SlackOptions> slack,
        TimeProvider timeProvider,
        ILogger<SlackSink> logger,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
        : base(clients, HttpClientName, slack.Value.Events, timeProvider, logger, lifetime)
        => _options = slack.Value;

    protected override TimeSpan Timeout => _options.Timeout;

    protected override int MaxAttempts => _options.MaxAttempts;

    public override Task WriteAsync(HomeostatEvent e, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, _options.Url!, Payload(e, _options), null, cancellationToken);

    internal static JsonObject Payload(HomeostatEvent e, SlackOptions options)
    {
        var text = Limit($"{Icon(e)} *{Escape(e.Application)}* · {Escape(e.Summary)}", 3000);
        var blocks = ArrayOf(
            ObjectOf(("type", "section"), ("text", Text("mrkdwn", text))),
            ObjectOf(
                ("type", "context"),
                ("elements", ArrayOf(Text("mrkdwn", Limit($"Condition `{Escape(e.Condition)}`{GroupText(e)} · {e.Kind}", 3000))))));

        var buttons = new JsonArray();
        if (e.Kind == HomeostatEventKind.ApprovalRequested && options.ApprovalButtons && e.ApprovalId is { } id)
        {
            buttons.Add((JsonNode)ObjectOf(
                ("type", "button"),
                ("action_id", ApproveAction),
                ("style", "primary"),
                ("text", Text("plain_text", "Approve")),
                ("value", id),
                ("confirm", ObjectOf(
                    ("title", Text("plain_text", "Approve this step?")),
                    ("text", Text("plain_text", Truncate(e.Group is { } group ? $"{e.Condition} (group {group}): {e.Step}" : $"{e.Condition}: {e.Step}", 300))),
                    ("confirm", Text("plain_text", "Approve")),
                    ("deny", Text("plain_text", "Cancel"))))));
            buttons.Add((JsonNode)ObjectOf(
                ("type", "button"),
                ("action_id", RejectAction),
                ("style", "danger"),
                ("text", Text("plain_text", "Reject")),
                ("value", id)));
        }

        if (options.DashboardUrl is { } dashboard)
        {
            buttons.Add((JsonNode)ObjectOf(
                ("type", "button"),
                ("action_id", "homeostat_open"),
                ("text", Text("plain_text", "Open dashboard")),
                ("url", dashboard.AbsoluteUri)));
        }

        if (buttons.Count > 0)
        {
            blocks.Add((JsonNode)ObjectOf(("type", "actions"), ("block_id", "homeostat"), ("elements", buttons)));
        }

        // "text" is the fallback for notifications and screen readers.
        return ObjectOf(("text", Limit(Escape($"[{e.Application}] {e.Summary}"), 3000)), ("blocks", blocks));
    }

    private static JsonObject Text(string type, string text) => ObjectOf(("type", type), ("text", text));

    // The healing group, only for a condition in one: messages without groups stay as they were.
    private static string GroupText(HomeostatEvent e) => e.Group is { } group ? $" in group `{Escape(group)}`" : string.Empty;

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
    internal const string HttpClientName = TeamsOptions.HttpClientName;
    private readonly TeamsOptions _options;

    public TeamsSink(
        IHttpClientFactory clients,
        IOptions<TeamsOptions> teams,
        TimeProvider timeProvider,
        ILogger<TeamsSink> logger,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
        : base(clients, HttpClientName, teams.Value.Events, timeProvider, logger, lifetime)
        => _options = teams.Value;

    protected override TimeSpan Timeout => _options.Timeout;

    protected override int MaxAttempts => _options.MaxAttempts;

    public override Task WriteAsync(HomeostatEvent e, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, _options.Url!, Payload(e, _options), null, cancellationToken);

    internal static JsonObject Payload(HomeostatEvent e, TeamsOptions options)
    {
        var facts = ArrayOf(
            Fact("Condition", e.Condition),
            Fact("Group", e.Group),
            Fact("Step", e.Step),
            Fact("Outcome", e.Outcome),
            Fact("Expires", e.ExpiresAt?.ToString("u", System.Globalization.CultureInfo.InvariantCulture)));

        var actions = options.DashboardUrl is { } dashboard
            ? ArrayOf(ObjectOf(
                ("type", "Action.OpenUrl"),
                ("title", e.Kind == HomeostatEventKind.ApprovalRequested ? "Review in dashboard" : "Open dashboard"),
                ("url", dashboard.AbsoluteUri)))
            : new JsonArray();

        var body = ArrayOf(
            ObjectOf(("type", "TextBlock"), ("text", $"{Plain(e.Application)}: {e.Kind}"), ("weight", "Bolder"), ("size", "Medium"), ("wrap", true)),
            ObjectOf(("type", "TextBlock"), ("text", Plain(e.Summary)), ("wrap", true)),
            ObjectOf(("type", "FactSet"), ("facts", facts)));
        if (e.Kind == HomeostatEventKind.ApprovalRequested && options.ApprovalCommand is { Length: > 0 } command && e.ApprovalId is { } approvalId)
        {
            body.Add((JsonNode)ObjectOf(
                ("type", "TextBlock"),
                ("text", $"Reply in the channel: @{command} approve {approvalId} (or reject {approvalId})"),
                ("wrap", true),
                ("isSubtle", true)));
        }

        return ObjectOf(
            ("type", "message"),
            ("attachments", ArrayOf(ObjectOf(
                ("contentType", "application/vnd.microsoft.card.adaptive"),
                ("content", ObjectOf(
                    ("$schema", "http://adaptivecards.io/schemas/adaptive-card.json"),
                    ("type", "AdaptiveCard"),
                    ("version", "1.4"),
                    ("body", body),
                    ("actions", actions)))))));
    }

    private static JsonObject? Fact(string title, string? value)
        => value is null ? null : ObjectOf(("title", title), ("value", Plain(value)));

    /// <summary>
    /// Adaptive Cards render Markdown in text: texts that can come from outside (alert summaries) are escaped, so they
    /// cannot add links or formatting to the card.
    /// </summary>
    internal static string Plain(string value)
    {
        var text = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '[' or ']' or '(' or ')' or '*' or '_' or '`' or '#' or '~' or '>' or '<' or '!' or '|')
            {
                text.Append('\\');
            }

            text.Append(c);
        }

        return text.ToString();
    }
}
