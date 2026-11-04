using System.Text.Json.Nodes;
using Homeostat.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Http;

/// <summary>Whether each condition is active or cleared, as seen by an incident sink (events arrive in order).</summary>
internal sealed class IncidentTracker
{
    private readonly Dictionary<string, bool> _cleared = new(StringComparer.Ordinal);

    public IncidentDecision Decide(IncidentOptions options, HomeostatEvent e)
    {
        var key = IncidentOptions.Key(e);
        lock (_cleared)
        {
            if (e.Kind == HomeostatEventKind.ConditionActivated)
            {
                _cleared[key] = false;
            }
            else if (e.Kind == HomeostatEventKind.ConditionCleared)
            {
                _cleared[key] = true;
            }

            return options.Decide(e, _cleared.GetValueOrDefault(key));
        }
    }
}

/// <summary>Opens and resolves PagerDuty incidents through the Events API v2.</summary>
internal sealed class PagerDutySink : HttpEventSink
{
    internal const string HttpClientName = PagerDutyOptions.HttpClientName;
    private readonly PagerDutyOptions _options;
    private readonly IncidentTracker _tracker = new();

    public PagerDutySink(
        IHttpClientFactory clients,
        IOptions<PagerDutyOptions> pagerDuty,
        TimeProvider timeProvider,
        ILogger<PagerDutySink> logger,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
        : base(clients, HttpClientName, IncidentOptions.Kinds, timeProvider, logger, lifetime)
        => _options = pagerDuty.Value;

    protected override TimeSpan Timeout => _options.Timeout;

    protected override int MaxAttempts => _options.MaxAttempts;

    public override Task WriteAsync(HomeostatEvent e, CancellationToken cancellationToken)
    {
        var decision = _tracker.Decide(_options, e);
        if (decision.IsResolve)
        {
            return SendAsync(HttpMethod.Post, _options.Url, ObjectOf(
                ("routing_key", _options.RoutingKey),
                ("event_action", "resolve"),
                ("dedup_key", IncidentOptions.Key(e))), null, cancellationToken);
        }

        if (!decision.IsTrigger)
        {
            return Task.CompletedTask;
        }

        return SendAsync(HttpMethod.Post, _options.Url, ObjectOf(
            ("routing_key", _options.RoutingKey),
            ("event_action", "trigger"),
            ("dedup_key", IncidentOptions.Key(e)),
            ("payload", ObjectOf(
                ("summary", Truncate($"[{e.Application}] {e.Summary}", 1024)),
                ("source", e.Application),
                ("severity", decision.Reason == IncidentReason.GaveUp ? "error" : "warning"),
                ("timestamp", e.Timestamp),
                ("component", _options.Component),
                ("group", _options.Group),
                ("class", e.Kind.ToString()),
                ("custom_details", ObjectOf(
                    ("condition", e.Condition),
                    ("step", e.Step),
                    ("outcome", e.Outcome),
                    ("error", e.Error),
                    ("approval_id", e.ApprovalId),
                    ("approval_expires_at", e.ExpiresAt)))))), null, cancellationToken);
    }

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Creates and closes alerts through the Opsgenie alert API (Jira Service Management Operations or Opsgenie).</summary>
internal sealed class OpsgenieSink : HttpEventSink
{
    internal const string HttpClientName = OpsgenieOptions.HttpClientName;
    private readonly OpsgenieOptions _options;
    private readonly IncidentTracker _tracker = new();
    private readonly KeyValuePair<string, string>[] _headers;

    public OpsgenieSink(
        IHttpClientFactory clients,
        IOptions<OpsgenieOptions> opsgenie,
        TimeProvider timeProvider,
        ILogger<OpsgenieSink> logger,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
        : base(clients, HttpClientName, IncidentOptions.Kinds, timeProvider, logger, lifetime)
    {
        _options = opsgenie.Value;
        _headers = [new("Authorization", $"{_options.AuthorizationScheme} {_options.ApiKey}")];
    }

    protected override TimeSpan Timeout => _options.Timeout;

    protected override int MaxAttempts => _options.MaxAttempts;

    public override Task WriteAsync(HomeostatEvent e, CancellationToken cancellationToken)
    {
        var decision = _tracker.Decide(_options, e);
        var alias = IncidentOptions.Key(e);
        if (decision.IsResolve)
        {
            var close = new Uri($"{_options.Url.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(alias)}/close?identifierType=alias");
            return SendAsync(HttpMethod.Post, close, ObjectOf(("source", "Homeostat"), ("note", e.Summary)), _headers, cancellationToken);
        }

        if (!decision.IsTrigger)
        {
            return Task.CompletedTask;
        }

        return SendAsync(HttpMethod.Post, _options.Url, ObjectOf(
            ("message", PagerDutySink.Truncate($"[{e.Application}] {e.Condition}: {Reason(decision.Reason)}", 130)),
            ("alias", alias),
            ("description", PagerDutySink.Truncate(e.Summary, 15000)),
            ("priority", decision.Reason == IncidentReason.GaveUp ? "P2" : "P3"),
            ("source", "Homeostat"),
            ("entity", e.Application),
            ("tags", ArrayOf(_options.Tags.Select(t => (JsonNode?)t).ToArray())),
            ("details", ObjectOf(
                ("condition", e.Condition),
                ("step", e.Step),
                ("outcome", e.Outcome),
                ("error", e.Error),
                ("approvalId", e.ApprovalId)))), _headers, cancellationToken);
    }

    private static string Reason(IncidentReason reason) => reason switch
    {
        IncidentReason.GaveUp => "self-healing gave up",
        IncidentReason.ApprovalNeeded => "approval needed",
        _ => "condition active",
    };
}
