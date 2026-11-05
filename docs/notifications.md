# Notifications and audit

This page covers how Homeostat tells people what it does and keeps a record of it, without any code: an audit log
in JSON Lines, a webhook notifier, incident notifiers for PagerDuty and Opsgenie, and chat notifiers for Slack and
Microsoft Teams. All of them are [observers](observability.md#observers-react-to-events) that queue events and write
them in the background, so a slow disk or endpoint never slows down healing.

## Events

Every sink receives the same flat event, `HomeostatEvent`:

| Field | Example | Present for |
|---|---|---|
| `kind` | `RunCompleted` | Always |
| `timestamp` | `2030-01-01T10:00:30+00:00` | Always |
| `application` | `weather-api` (from `HomeostatOptions.ApplicationName`) | Always |
| `condition` | `weather-feed-down` | Always |
| `summary` | `Healing of 'weather-feed-down' ended: Healed after 1 step(s).` | Always |
| `runId` | `3f2b9c0d...` | Step and run events |
| `instance` | `weather-api-7d9f8` (the instance that ran the plan) | Run events |
| `step` | `RenewFeedClientAction` | Step and approval events; run events with at least one step (the last step) |
| `outcome` | `Healed`, `NotHealed`, `Approved`, ... | Run events, resolved approvals, and steps that were verified (the verification outcome) |
| `error` | `Connection refused` | Failed steps |
| `approvalId` | `5f0c...` | Approval events |
| `expiresAt` | `2030-01-01T10:15:00+00:00` | Approval requests |
| `by` | `on-call` | Approved or rejected requests, when the person is known |

| Kind | When |
|---|---|
| `ConditionActivated` | A condition became active |
| `ConditionCleared` | An active condition cleared |
| `StepCompleted` | A step finished (not in dry-run) |
| `RunCompleted` | A run ended, whatever the outcome |
| `ApprovalRequested` | A step waits for approval |
| `ApprovalResolved` | The request was approved, rejected, expired or withdrawn (`outcome` = `Approved`, `Rejected`, `Expired`, `Canceled`) |

## Audit log

```csharp
.AddHomeostat(h => h
    .AddCondition(...)
    .AddAuditLog("logs/homeostat-{date}.jsonl"))
```

Every event becomes one line of JSON; null fields are omitted:

```json
{"kind":"ConditionActivated","timestamp":"2030-01-01T10:00:30+00:00","application":"weather-api","condition":"weather-feed-down","summary":"Condition 'weather-feed-down' is active."}
{"kind":"RunCompleted","timestamp":"2030-01-01T10:00:41+00:00","application":"weather-api","condition":"weather-feed-down","summary":"Healing of 'weather-feed-down' ended: Healed after 1 step(s).","runId":"3f2b9c0d4e5f4a6b8c7d9e0f1a2b3c4d","instance":"weather-api-7d9f8","step":"RenewFeedClientAction","outcome":"Healed"}
```

- `{date}` in the path is replaced with the UTC date of the event (`yyyy-MM-dd`), so you get one file per day.
  Without it, everything goes to one file. The default path is `homeostat-audit-{date}.jsonl`, relative to the
  current directory.
- The directory is created if needed. The file is opened for each event and shared, so log shippers and rotation
  tools can read, move or delete it.
- Every kind is recorded by default. To record only some kinds:
  `.AddAuditLog(o => { o.Path = "..."; o.Events.Remove(HomeostatEventKind.StepCompleted); })`.

## Webhook notifier

The webhook notifier is part of `Homeostat.Http` (`dotnet add package Homeostat.Http --prerelease`), like the incident
and chat notifiers below:

```csharp
.AddWebhookNotifications(o =>
{
    o.Url = new Uri(builder.Configuration["Homeostat:Url"]!);
    o.Headers["Authorization"] = $"Bearer {builder.Configuration["Homeostat:WebhookToken"]}";
})
```

Each event is sent as a `POST` with a JSON body (the event itself, as above). By default, the notifier sends
condition activated and cleared, run completed, approval requested and approval resolved. Step events are left
out to keep channels quiet; `o.Events` changes the selection.

### Shaping the body

`CreatePayload` turns an event into whatever the receiver expects. Most chat webhooks accept a `text` field:

```csharp
// Slack incoming webhook
o.CreatePayload = e => new JsonObject { ["text"] = $"[{e.Application}] {e.Summary}" };
```

```csharp
// Include the approval link for the person on call
o.CreatePayload = e => new JsonObject
{
    ["text"] = e.Kind == HomeostatEventKind.ApprovalRequested
        ? $"{e.Summary} Approve: POST https://weather.example/homeostat/approvals/{e.ApprovalId}/approve"
        : e.Summary,
};
```

For receivers that need a specific schema (for example adaptive cards in Microsoft Teams), build the body in
`CreatePayload`. A `JsonNode` (`JsonObject`, `JsonArray`) works everywhere, including trimmed and Native AOT
applications. Other objects, such as anonymous ones (`e => new { text = e.Summary }`), are serialized with reflection.
That works in an ordinary application and fails with a clear error when reflection-based serialization is off.
Returning `null` is an error too.

### Delivery

| Option | Default | Meaning |
|---|---|---|
| `Timeout` | 10 seconds | Per attempt; must be positive |
| `MaxAttempts` | `3` | Attempts per event, including the first; at least 1 |
| `Headers` | none | Added to every request |

Network errors, timeouts, `408`, `429` and `5xx` answers are retried after 2 seconds times the attempt number, or
after the receiver's `Retry-After` (capped at one minute); other `4xx` answers are not retried, since the request
itself is wrong. Failed deliveries are logged as warnings and dropped after the last attempt. Redirects are not
followed, and the HTTP client's own request logging is removed, because webhook URLs often carry a secret. The HTTP
client is a named `IHttpClientFactory` client (`WebhookOptions.HttpClientName`, `"Homeostat.Webhook"`), so you can add
a proxy, certificates or handlers:

```csharp
builder.Services.AddHttpClient(WebhookOptions.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { Proxy = new WebProxy("http://proxy:8080") });
```

The URL is validated at startup: it must be absolute. One webhook is supported per application; for several
destinations, point it to a small relay or write your own sink. Put secrets in `Headers` (an `Authorization` header,
as above), never in the URL: URLs end up in proxy and access logs. See [Security](security.md).

## Incidents: PagerDuty and Opsgenie

Page the on-call person only when a person is needed: when healing gives up (the run ended as `NotHealed`,
`Inconclusive`, `BudgetExhausted` or `NotApproved`) or a step waits for approval. The incident is resolved when the
condition clears. There is one incident per application and condition (deduplication key or alias
`homeostat.{application}.{condition}`, with slashes replaced by `-`, at most 255 characters), so repeated triggers
update it instead of opening new ones. A run that completes after its condition already cleared opens nothing, since
nothing would resolve it.

```csharp
.AddPagerDutyIncidents(o => o.RoutingKey = builder.Configuration["PagerDuty:RoutingKey"])   // Events API v2
.AddOpsgenieIncidents(o => o.ApiKey = builder.Configuration["Jsm:ApiKey"])                     // Opsgenie alert API
```

| Option | Default | Meaning |
|---|---|---|
| `OnHealingGaveUp` | `true` | Trigger when a run ends without healing |
| `OnApprovalRequested` | `true` | Trigger when a step waits for approval |
| `OnConditionActivated` | `false` | Trigger as soon as a condition is active, before healing |
| `ResolveWhenCleared` | `true` | Resolve (close) when the condition clears |
| `Timeout`, `MaxAttempts` | 10 seconds, 3 | Delivery, retried as for the webhook |

PagerDuty: `RoutingKey` (the integration key) is required. Severity is `error` when healing gave up, `warning`
otherwise; `Component` and `Group` are optional. EU accounts set `Url` to
`https://events.eu.pagerduty.com/v2/enqueue`.

Opsgenie: `ApiKey` is required. Priority is `P2` when healing gave up, `P3` otherwise; `Tags` holds `homeostat` by
default, and you can add your own. The default endpoint is Jira Service Management Operations
(`OpsgenieOptions.JiraServiceManagement`), where Atlassian is moving Opsgenie. Standalone Opsgenie
(`OpsgenieOptions.Opsgenie`, `OpsgenieOptions.OpsgenieEu`) still works, but Atlassian supports it only until
5 April 2027: prefer Jira Service Management for new setups. The key is sent as `Authorization: GenieKey {key}`;
change `AuthorizationScheme` if your integration expects another scheme.

## Chat: Slack and Microsoft Teams

### Slack

```csharp
.AddSlackNotifications(o =>
{
    o.Url = new Uri(builder.Configuration["Slack:Url"]!);
    o.DashboardUrl = new Uri("https://weather-api.internal/homeostat/dashboard");   // optional link
    o.ApprovalButtons = true;                                                      // optional, see below
})
```

Messages use Block Kit: an icon for the kind of event, the application, the summary, the condition and the event
kind, and an **Open dashboard** button when `DashboardUrl` is set. Text coming from signals is escaped, so an alert
message cannot mention `@channel` or forge links. With `ApprovalButtons`, approval requests carry **Approve** and
**Reject** buttons. The webhook must then belong to a Slack app with interactivity enabled, and the clicks are
received by `MapHomeostatSlackInteractions` ([ASP.NET Core endpoints](aspnetcore.md#slack-approvals)), which checks
the signature and who may approve.

### Microsoft Teams

```csharp
.AddTeamsNotifications(o =>
{
    o.Url = new Uri(builder.Configuration["Teams:WorkflowUrl"]!);
    o.DashboardUrl = new Uri("https://weather-api.internal/homeostat/dashboard");
})
```

Create a Teams workflow from the template "Post to a channel when a webhook request is received" and use its URL.
Each event is an Adaptive Card with the condition, step, outcome and expiry. With `DashboardUrl` set, cards link to
the dashboard, and approval requests say "Review in dashboard", where the step is approved. To approve from the
channel instead, map a Teams outgoing webhook with `MapHomeostatTeamsCommands`
([ASP.NET Core endpoints](aspnetcore.md#microsoft-teams-approvals)) and set `o.ApprovalCommand` to its name: the cards
then say `Reply in the channel: @Homeostat approve <id> (or reject <id>)`.

Both chat notifiers send the same events as the webhook by default (condition activated and cleared, run completed,
approval requested and resolved); change `Events` to send fewer. `Url` is required and, like `DashboardUrl`, must be
absolute. `Timeout` and `MaxAttempts` work as for the webhook.

Like the webhook, the incident and chat notifiers post through named `IHttpClientFactory` clients:
`PagerDutyOptions.HttpClientName` (`"Homeostat.PagerDuty"`), `OpsgenieOptions.HttpClientName`
(`"Homeostat.Opsgenie"`), `SlackOptions.HttpClientName` (`"Homeostat.Slack"`) and `TeamsOptions.HttpClientName`
(`"Homeostat.Teams"`). Configure a proxy or handlers with `AddHttpClient(SlackOptions.HttpClientName)`.

## Delivery guarantees

- Events are queued in memory, up to 1000 per sink; the oldest are dropped if the sink falls behind.
- When the application stops, sinks keep accepting events until every other hosted service has stopped, so
  runs canceled by the shutdown are recorded too. Then they deliver what is queued within the host's shutdown
  timeout. During shutdown the HTTP notifiers make a single attempt per event, of at most 5 seconds, to leave time to
  the others.
- Nothing is persisted across restarts. For a durable trail, ship the audit file with your log pipeline.

## Your own sink

Implement `IHomeostatEventSink` to deliver events anywhere (a message queue, a database, an incident tool)
with the same background queue and shutdown behavior:

```csharp
public sealed class IncidentSink(IncidentClient client, IHostApplicationLifetime lifetime) : IHomeostatEventSink
{
    public IReadOnlyCollection<HomeostatEventKind>? Events => [HomeostatEventKind.RunCompleted];   // null (the default): every kind

    public Task WriteAsync(HomeostatEvent e, CancellationToken cancellationToken)
        => e.Outcome is "Healed"
            ? Task.CompletedTask
            : client.OpenAsync(e.Summary, retry: !lifetime.ApplicationStopping.IsCancellationRequested, cancellationToken);   // your code
}
```

```csharp
.AddHomeostat(h => h.AddEventSink<IncidentSink>())
```

`AddEventSink<TSink>()` registers the sink once, as a singleton. Homeostat builds the events (with
`ApplicationName` filled in from `HomeostatOptions`), queues them and calls `WriteAsync` one at a time, in the
background. An exception from `WriteAsync` is logged and the event is dropped. The token is canceled when the host's
shutdown timeout expires; a sink that retries should stop retrying once `IHostApplicationLifetime.ApplicationStopping`
is signaled, as the example does.

See also: [Observability](observability.md), [ASP.NET Core endpoints](aspnetcore.md), [Security](security.md).
