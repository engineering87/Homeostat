# Notifications and audit

Homeostat can tell people what it does and keep a record of it, without any code: an **audit log** in JSON
Lines and a **webhook notifier** for chats and incident tools. Both are [observers](observability.md#observers-react-to-events)
that queue events and write them in the background, so a slow disk or endpoint never slows down healing.

## Events

Both use the same flat event, `HomeostatEvent`:

| Field | Example | Present for |
|---|---|---|
| `kind` | `RunCompleted` | Always |
| `timestamp` | `2030-01-01T10:00:30+00:00` | Always |
| `application` | `weather-api` (from `HomeostatOptions.ApplicationName`) | Always |
| `condition` | `weather-feed-down` | Always |
| `summary` | `Healing of 'weather-feed-down' ended: Healed after 1 step(s).` | Always |
| `step` | `RenewFeedClientAction` | Step, run and approval events |
| `outcome` | `Healed`, `NotHealed`, `Approved`, ... | Step, run and resolved approval events |
| `error` | `Connection refused` | Failed steps |
| `approvalId`, `expiresAt` | `5f0c...`, `2030-01-01T10:15:00+00:00` | Approval events |
| `by` | `on-call` | Approved or rejected requests |

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
{"kind":"RunCompleted","timestamp":"2030-01-01T10:00:41+00:00","application":"weather-api","condition":"weather-feed-down","summary":"Healing of 'weather-feed-down' ended: Healed after 1 step(s).","step":"RenewFeedClientAction","outcome":"Healed"}
```

- `{date}` in the path is replaced with the UTC date of the event, so you get one file per day. Without it,
  everything goes to one file.
- The directory is created if needed; the file is opened for each event and shared, so log shippers and
  rotation tools can read, move or delete it.
- To record only some kinds: `.AddAuditLog(o => { o.Path = "..."; o.Events.Remove(HomeostatEventKind.StepCompleted); })`.

## Webhook notifier

Part of `Homeostat.Http` (`dotnet add package Homeostat.Http --prerelease`), like the incident and chat notifiers below:

```csharp
.AddWebhookNotifications(o =>
{
    o.Url = new Uri(builder.Configuration["Homeostat:Url"]!);
    o.Headers["Authorization"] = $"Bearer {builder.Configuration["Homeostat:WebhookToken"]}";
})
```

Each event is sent as a `POST` with a JSON body (the event itself, as above). By default, the notifier sends
condition activated and cleared, run completed, approval requested and approval resolved; step events are left
out to keep channels quiet (`o.Events` changes the selection).

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
applications. Other objects, such as anonymous ones (`e => new { text = e.Summary }`), are serialized with reflection:
fine in an ordinary application, refused with a clear error when reflection-based serialization is off.

### Delivery

| Setting | Default | Meaning |
|---|---|---|
| `Timeout` | 10 seconds | Per attempt |
| `MaxAttempts` | `3` | Attempts per event, with a short growing delay between them |
| `Headers` | none | Added to every request |

Failed deliveries are logged as warnings and dropped after the last attempt. The HTTP client is a named
`IHttpClientFactory` client, so you can add a proxy, certificates or handlers:

```csharp
builder.Services.AddHttpClient(WebhookOptions.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { Proxy = new WebProxy("http://proxy:8080") });
```

The URL is validated at startup: it must be absolute. One webhook is supported per application; for
several destinations, point it to a small relay or write your own observer. Put secrets in `Headers` (an
`Authorization` header, as above), never in the URL: URLs end up in proxy and access logs. See [Security](security.md).

## Incidents: PagerDuty and Opsgenie

Page the on-call person only when a person is needed: when healing **gives up** (the run ended as not healed,
inconclusive, out of budget or not approved) or a step **waits for approval**. The incident is resolved when the
condition clears. There is one incident per application and condition (`homeostat/{application}/{condition}`), so
repeated triggers update it instead of opening new ones.

```csharp
.AddPagerDutyIncidents(o => o.RoutingKey = builder.Configuration["PagerDuty:RoutingKey"])   // Events API v2
.AddOpsgenieIncidents(o => o.ApiKey = builder.Configuration["Jsm:ApiKey"])                     // Opsgenie alert API
```

| Setting | Default | Meaning |
|---|---|---|
| `OnHealingGaveUp` | `true` | Trigger when a run ends without healing |
| `OnApprovalRequested` | `true` | Trigger when a step waits for approval |
| `OnConditionActivated` | `false` | Trigger as soon as a condition is active, before healing |
| `ResolveWhenCleared` | `true` | Resolve (close) when the condition clears |
| `Timeout`, `MaxAttempts` | 10 s, 3 | Delivery; network errors, 408, 429 and 5xx are retried, other 4xx are not |

PagerDuty: severity `error` when healing gave up, `warning` otherwise; `component` and `group` are optional; EU
accounts set `Url` to `https://events.eu.pagerduty.com/v2/enqueue`.

Opsgenie: priority `P2` when healing gave up, `P3` otherwise, tags `homeostat` plus yours. The default endpoint is
**Jira Service Management Operations** (`OpsgenieOptions.JiraServiceManagement`), where Atlassian is moving Opsgenie;
standalone Opsgenie (`OpsgenieOptions.Opsgenie`, `OpsgenieOptions.OpsgenieEu`) is still supported, but Atlassian has
announced its end of support (April 2027): prefer Jira Service Management for new setups. The key is sent as `Authorization: GenieKey {key}`; change `AuthorizationScheme` if your integration
expects another scheme.

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

Messages use Block Kit: an icon for the kind of event, the summary, the condition, and an **Open dashboard** button.
Text coming from signals is escaped, so an alert message cannot mention `@channel` or forge links. With
`ApprovalButtons`, approval requests carry **Approve** and **Reject** buttons: the webhook must belong to a Slack app
with interactivity enabled, and the clicks are received by `MapHomeostatSlackInteractions`
([ASP.NET Core endpoints](aspnetcore.md#slack-approvals)), which checks the signature and who may approve.

### Microsoft Teams

```csharp
.AddTeamsNotifications(o =>
{
    o.Url = new Uri(builder.Configuration["Teams:WorkflowUrl"]!);
    o.DashboardUrl = new Uri("https://weather-api.internal/homeostat/dashboard");
})
```

Create a Teams workflow from the template "Post to a channel when a webhook request is received" and use its URL.
Each event is an Adaptive Card with the condition, step, outcome and expiry; approval requests link to the dashboard
("Review in dashboard"), where the step is approved. To approve from the channel instead, map a Teams outgoing webhook
with `MapHomeostatTeamsCommands` ([ASP.NET Core endpoints](aspnetcore.md#microsoft-teams-approvals)) and set
`o.ApprovalCommand` to its name: the cards then say `Reply in the channel: @Homeostat approve <id>`.

Both notifiers send the same events as the webhook by default (condition activated and cleared, run completed,
approval requested and resolved); change `Events` to send fewer.

Like the webhook, the incident and chat notifiers post through named `IHttpClientFactory` clients:
`PagerDutyOptions.HttpClientName` (`"Homeostat.PagerDuty"`), `OpsgenieOptions.HttpClientName`
(`"Homeostat.Opsgenie"`), `SlackOptions.HttpClientName` (`"Homeostat.Slack"`) and `TeamsOptions.HttpClientName`
(`"Homeostat.Teams"`). Configure a proxy or handlers with `AddHttpClient(SlackOptions.HttpClientName)`.

## Delivery guarantees

- Events are queued in memory (up to 1000; the oldest are dropped if the sink falls behind).
- When the application stops, sinks keep accepting events until every other hosted service has stopped, so
  runs canceled by the shutdown are recorded too; then they deliver what is queued within the host's shutdown
  timeout. During shutdown the webhook makes a single short attempt per event, to leave time to the others.
- Nothing is persisted across restarts: for a durable trail, ship the audit file with your log pipeline.

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
