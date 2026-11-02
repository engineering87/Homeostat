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
| `ApprovalResolved` | The request was approved, rejected, expired or withdrawn (`outcome` = `Approved`, `Rejected`, `Expired`, `Cancelled`) |

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

Part of `Homeostat.Http`:

```csharp
.AddWebhookNotifications(o =>
{
    o.Url = new Uri(builder.Configuration["Homeostat:WebhookUrl"]!);
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
o.CreatePayload = e => new { text = $"[{e.Application}] {e.Summary}" };
```

```csharp
// Include the approval link for the person on call
o.CreatePayload = e => new
{
    text = e.Kind == HomeostatEventKind.ApprovalRequested
        ? $"{e.Summary} Approve: POST https://weather.example/homeostat/approvals/{e.ApprovalId}/approve"
        : e.Summary,
};
```

For receivers that need a specific schema (for example adaptive cards in Microsoft Teams), build the object
in `CreatePayload`; any serializable object works.

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
several destinations, point it to a small relay or write your own observer.

## Delivery guarantees

- Events are queued in memory (up to 1000; the oldest are dropped if the sink falls behind).
- When the application stops, sinks keep accepting events until every other hosted service has stopped, so
  runs cancelled by the shutdown are recorded too; then they deliver what is queued within the host's shutdown
  timeout. During shutdown the webhook makes a single short attempt per event, to leave time to the others.
- Nothing is persisted across restarts: for a durable trail, ship the audit file with your log pipeline.

## Your own sink

Derive from `HomeostatEventSink` to deliver events anywhere (a message queue, a database, an incident tool)
with the same background queue and shutdown behavior:

```csharp
public sealed class IncidentSink(IncidentClient client, IOptions<HomeostatOptions> options, TimeProvider time, ILogger<IncidentSink> logger)
    : HomeostatEventSink(options.Value.ApplicationName, [HomeostatEventKind.RunCompleted], time, logger)
{
    protected override Task WriteAsync(HomeostatEvent e, CancellationToken cancellationToken)
        => e.Outcome is "Healed" ? Task.CompletedTask : client.OpenAsync(e.Summary, cancellationToken);   // your code
}
```

```csharp
.AddHomeostat(h => h.AddEventSink<IncidentSink>())
```

`IsStopping` tells the sink that the application is shutting down, so it can skip retries.
