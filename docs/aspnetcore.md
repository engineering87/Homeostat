# ASP.NET Core endpoints

`Homeostat.AspNetCore` exposes Homeostat over HTTP: a status endpoint for people and dashboards, and control
endpoints to pause, resume, answer approval requests and flip switches.

```bash
dotnet add package Homeostat.AspNetCore
```

```csharp
var app = builder.Build();

app.MapHomeostatStatus("operations");     // GET  /homeostat/status
app.MapHomeostatControl("operations");    // POST /homeostat/pause, /resume, ...
```

`"operations"` is an [authorization policy](https://learn.microsoft.com/aspnet/core/security/authorization/policies)
of your application, for example "members of the on-call group". **Both methods require one**: called without a
policy, they throw at startup. Only for local demos and tests, `allowAnonymous: true` maps them without
authorization.

Both take an optional route prefix (`"/homeostat"` by default) and return a `RouteGroupBuilder`, so the usual
endpoint conventions apply (rate limiting, OpenAPI tags, CORS).

## Status

`GET /homeostat/status` returns everything at once:

```json
{
  "application": "weather-api",
  "instance": "weather-api-7d9f8:1",
  "paused": false,
  "pauseReason": null,
  "dryRun": false,
  "budget": { "used": 2, "limit": 10, "remaining": 8, "windowSeconds": 3600 },
  "conditions": [
    {
      "name": "weather-feed-down",
      "status": "Active",
      "pendingSince": "2030-01-01T10:00:00+00:00",
      "activeSince": "2030-01-01T10:00:30+00:00",
      "clearingSince": null,
      "suppressedBy": null,
      "lastSignalAt": "2030-01-01T10:00:40+00:00",
      "lastSeverity": "Unhealthy",
      "lastMessage": "Weather feed is not responding."
    }
  ],
  "switches": [ "serve-cached-forecast" ],
  "pendingApprovals": [
    {
      "id": "5f0c3e0c2a8b4d0e9d7a1c2b3e4f5a6b",
      "conditionName": "weather-feed-down",
      "step": 1,
      "stepName": "SwitchToBackupProviderAction",
      "requestedAt": "2030-01-01T10:00:52+00:00",
      "expiresAt": "2030-01-01T10:15:52+00:00",
      "triggeringSignal": { "source": "weather-feed", "severity": 2, "timestamp": "2030-01-01T10:00:30+00:00", "message": "Weather feed is not responding.", "tags": null }
    }
  ],
  "recentRuns": [
    {
      "condition": "weather-feed-down",
      "outcome": "Healed",
      "startedAt": "2030-01-01T09:12:00+00:00",
      "completedAt": "2030-01-01T09:12:09+00:00",
      "steps": [ { "name": "RenewFeedClientAction", "succeeded": true, "verification": "Healed", "error": null, "undoSucceeded": null } ],
      "instance": "weather-api-7d9f8:1"
    }
  ]
}
```

If the store cannot be read, the endpoint still answers: `budget` is `null`, `recentRuns` is empty and
`storeError` says why. Times are those of the application's `TimeProvider`. Conditions and switches are those of the instance that
answers; budget and runs come from the [store](persistence-and-clusters.md), so with a shared store they cover
every instance. The endpoint is read-only but reveals condition names,
error messages and approval ids, which is why it needs a policy too.

## Control

| Endpoint | Effect | Response |
|---|---|---|
| `POST /homeostat/pause` | Kill switch on: no new step starts | `204` |
| `POST /homeostat/pause?for=00:30:00` | Paused for a duration, then resumes by itself (for deployment scripts) | `204`, or `400` for an invalid duration |
| `POST /homeostat/resume` | Kill switch off | `204` |
| `POST /homeostat/approvals/{id}/approve` | Approves a pending step | `204`, or `404` if the request does not exist (any more) |
| `POST /homeostat/approvals/{id}/reject` | Rejects it: the run stops | `204` or `404` |
| `POST /homeostat/switches/{name}/on` | Turns a switch on | `204`, or `400` for names over 200 characters |
| `POST /homeostat/switches/{name}/off` | Turns a switch off | `204` |

The name of the authenticated user (`User.Identity.Name`) is recorded as the approver, in logs, events and the
audit log.

Security notes:

- The policy decides who can pause Homeostat or approve a failover: make it specific (an operations role or
  group), not "any authenticated user", especially in an application with end-user accounts.
- The application must use authentication and authorization (`app.UseAuthorization()`).
- Against cross-site request forgery, every control request must carry an `Authorization: Bearer` token or the
  `X-Requested-With` header (any value), and requests that the browser marks as cross-site (`Sec-Fetch-Site`) are
  refused with `403`. A page on another site cannot add those headers, so users signed in with cookies, Windows
  authentication, Basic or client certificates are protected. The dashboard and the `homeostat` CLI send the header;
  with curl, add `-H "X-Requested-With: curl"`.

## Dashboard

```csharp
app.MapHomeostatStatus("operations");
app.MapHomeostatControl("operations");
app.MapHomeostatDashboard("operations");   // GET /homeostat/dashboard
```

A page for operators: active conditions first, steps waiting for approval with **Approve** and **Reject** buttons,
switches (turn them off, or on by name), the action budget and the recent runs with their steps. It refreshes every 5
seconds (not while the tab is hidden) and follows the system light or dark theme.

- It reads `{prefix}/status` and calls the control endpoints: map all three with the same prefix and policy. Without
  `MapHomeostatControl` it is read-only.
- It is self-contained (no CDN, fonts or images) and served with a strict Content-Security-Policy
  (`default-src 'none'`, scripts and styles only from the application, no framing). Condition names and messages can
  come from external alerts, so every value is rendered as text, never as HTML.
- Its requests carry `X-Requested-With`, which the control endpoints require from callers without a bearer token.

## Slack approvals

With `AddSlackNotifications(o => o.ApprovalButtons = true)` (package `Homeostat.Http`, see
[Notifications](notifications.md#slack)), approval requests appear in Slack with **Approve** and **Reject** buttons.
Map the endpoint that receives the clicks, and set it as the interactivity request URL of the Slack app:

```csharp
app.MapHomeostatSlackInteractions(o =>
{
    o.SigningSecret = builder.Configuration["Slack:SigningSecret"];
    o.AllowedUserIds.Add("U024BE7LH");        // who may approve; or o.AllowAnyChannelMember = true
});                                           // POST /homeostat/slack/interactions
```

| Check | Behavior |
|---|---|
| Slack signature (`X-Slack-Signature`, HMAC-SHA256 of the body with the signing secret) | Required, constant-time comparison; otherwise `401` |
| Request age (`X-Slack-Request-Timestamp`) | At most 5 minutes (`MaxRequestAge`), against replays |
| User | Must be in `AllowedUserIds` unless `AllowAnyChannelMember`; others are told privately that they cannot approve |
| Decision | Recorded with the approver `slack:{user name}`; the buttons in the message are replaced with the decision |

The endpoint authenticates Slack by its signature, so it is mapped without an authorization policy.

## Alerts

`MapHomeostatAlerts` receives Prometheus Alertmanager, Grafana and Azure Monitor notifications as signals: see
[Integrations](integrations.md#alerts-from-your-monitoring).

## Homeostat as a health check

The `Homeostat.HealthChecks` package adds a health check that describes Homeostat itself:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<WeatherFeedHealthCheck>("weather-feed")
    .AddHomeostatCheck();   // name "homeostat"
```

| State | Status | Description |
|---|---|---|
| Nothing to report | Healthy | `No active condition.` |
| Paused, budget exhausted, a step waiting for approval, or an active condition | Degraded (configurable) | The reasons, for example `paused; active: weather-feed-down` |

The result data contains `paused`, `dry_run`, `active_conditions`, `budget_remaining` and `pending_approvals`.
Use `attentionStatus: HealthStatus.Unhealthy` to make it stricter, and `tags` to select it in a dedicated
endpoint. This check is never turned into a Homeostat signal, so it cannot trigger conditions about itself.
