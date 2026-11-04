# ASP.NET Core endpoints

`Homeostat.AspNetCore` exposes Homeostat over HTTP: a status endpoint for people and dashboards, and control
endpoints to pause, resume, answer approval requests and flip switches.

```bash
dotnet add package Homeostat.AspNetCore --prerelease
```

```csharp
var app = builder.Build();

app.MapHomeostatStatus("operations");     // GET  /homeostat/status
app.MapHomeostatControl("operations");    // POST /homeostat/pause, /resume, ...
```

`"operations"` is an [authorization policy](https://learn.microsoft.com/aspnet/core/security/authorization/policies)
of your application, for example "members of the on-call group". **Both methods require one**: called without a
policy, they throw at startup. Only for local demos and tests, `allowAnonymous: true` maps them without
authorization. To build the policy inline instead of registering it by name, pass a lambda:

```csharp
app.MapHomeostatStatus(p => p.RequireRole("operations"));
app.MapHomeostatControl(p => p.RequireRole("operations"));
app.MapHomeostatDashboard(p => p.RequireRole("operations"));
```

Both take an optional route prefix (`"/homeostat"` by default) and return a `RouteGroupBuilder`, so the usual
endpoint conventions apply (rate limiting, OpenAPI tags, CORS). The JSON they return is the contract; the .NET types
behind it are internal.

## Status

`GET /homeostat/status` returns everything at once:

```json
{
  "application": "weather-api",
  "instance": "weather-api-7d9f8",
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
      "triggeringSignal": { "source": "weather-feed", "severity": "Unhealthy", "timestamp": "2030-01-01T10:00:30+00:00", "value": null, "message": "Weather feed is not responding.", "tags": null }
    }
  ],
  "recentRuns": [
    {
      "condition": "weather-feed-down",
      "outcome": "Healed",
      "startedAt": "2030-01-01T09:12:00+00:00",
      "completedAt": "2030-01-01T09:12:09+00:00",
      "steps": [ { "name": "RenewFeedClientAction", "succeeded": true, "verification": "Healed", "error": null, "undoSucceeded": null } ],
      "instance": "weather-api-7d9f8"
    }
  ]
}
```

Enums are written as names (`"Unhealthy"`, `"Healed"`), never as numbers, and property names in camel case. The
shape of this document, and of the history and coverage documents below, is part of the public contract: fields
are added in minor versions, never renamed or removed before a major version (see [Versioning](versioning.md)).

If the store cannot be read, the endpoint still answers: `budget` is `null`, `recentRuns` and `pendingApprovals` are
empty and `storeError` says why. Times are those of the application's `TimeProvider`. Conditions are those of the
instance that answers; `switches` lists the application switches and the switches of that instance. Budget, runs and
pending approvals come from the [store](persistence-and-clusters.md), so with a shared store they cover
every instance, and so do the pause and the application switches. The endpoint is read-only but reveals condition names,
error messages and approval ids, which is why it needs a policy too.

## Coverage and history

`MapHomeostatStatus` also maps:

- `GET /homeostat/coverage` (`?last=04:00:00` or `?since=...`, default the last 24 hours): which conditions activated,
  which plans ran and which steps never ran. See [Learning and foresight](intelligence.md#coverage).
- `GET /homeostat/history` (optionally `?condition=feed-down`): the latest activations and clears, newest first, with
  the instance and the signal that caused them. With the file and Redis stores the history survives restarts and, with
  a shared store, covers every instance; with the default in-memory store it covers this process. When the store
  cannot be read, the endpoint answers with this instance's events.

```json
[ { "condition": "feed-down", "kind": "Activated", "at": "2026-11-02T09:14:05Z", "instance": "web-1",
    "source": "weather-feed", "severity": "Unhealthy", "value": null, "message": "timeout" } ]
```

## Control

| Endpoint | Effect | Response |
|---|---|---|
| `POST /homeostat/pause` | Kill switch on: no new step starts, on every instance that shares the store | `204` |
| `POST /homeostat/pause?for=00:30:00` | Paused for a duration (at most 30 days), then resumes by itself (for deployment scripts) | `204`, or `400` for an invalid duration |
| `POST /homeostat/resume` | Kill switch off | `204` |
| `POST /homeostat/approvals/{id}/approve` | Approves a pending step, on any instance | `204`, or `404` if the request does not exist (any more) |
| `POST /homeostat/approvals/{id}/reject` | Rejects it: the run stops and the plan is held | `204` or `404` |
| `POST /homeostat/conditions/{name}/release` | Lifts the hold of the condition's plan after a rejection ([details](healing-plans.md#releasing-a-hold)) | `204`, or `404` for an unknown condition or one without a plan |
| `POST /homeostat/switches/{name}/on` | Turns an application switch on, for every instance | `204`, or `400` for names over 200 characters |
| `POST /homeostat/switches/{name}/on?scope=instance` | Turns the switch on for the instance that answers only | `204`, or `400` for an invalid `scope` |
| `POST /homeostat/switches/{name}/off` | Turns a switch off, for the application and the instance that answers | `204` |

Pause, approvals and switches are kept in the [store](persistence-and-clusters.md#shared-control-state). When the
store cannot be reached, a change that lets Homeostat act (resume, turn a switch on, answer an approval) answers `503`
and is not made; a change in the safe direction (pause, turn a switch off) applies at once on the instance that
answers, answers `204`, and is written to the store as soon as it answers, so an outage never keeps chaos, an
isolated circuit or a paused consumer on. A request waits at most 15 seconds for the store; a change that is aborted by
the client still applies. Changes reach the other instances within `ControlRefreshInterval` (5 seconds by default).

The name of the authenticated user (`User.Identity.Name`) is recorded as the approver in logs, events and the audit
log, and with the pause and the switches it turns on: who paused appears in `pauseReason`.

Security notes:

- The policy decides who can pause Homeostat or approve a failover: make it specific (an operations role or
  group), not "any authenticated user", especially in an application with end-user accounts.
- The application must use authentication and authorization (`app.UseAuthorization()`).
- Against cross-site request forgery, every control request must carry an `Authorization: Bearer` token or the
  `X-Requested-With` header (any value), and requests that the browser marks as cross-site or same-site
  (`Sec-Fetch-Site`) are refused with `403`. A page on another site cannot add those headers, so users signed in with cookies, Windows
  authentication, Basic or client certificates are protected. The dashboard and the `homeostat` CLI send the header;
  with curl, add `-H "X-Requested-With: curl"`.
- The [security guide](security.md) collects these rules with the other hardening steps.

## Dashboard

```csharp
app.MapHomeostatStatus("operations");
app.MapHomeostatControl("operations");
app.MapHomeostatDashboard("operations");   // GET /homeostat/dashboard
```

A page for operators: active conditions first, steps waiting for approval with **Approve** and **Reject** buttons,
switches (turn them off, or on by name), the action budget, the recent runs with their steps, the condition history and
the coverage of the last 24 hours (steps that always failed or never ran). It refreshes every 5 seconds (history and
coverage every 30 seconds; nothing while the tab is hidden) and follows the system light or dark theme.

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

## Microsoft Teams approvals

Teams has no buttons that call an application without a registered bot, but it has **outgoing webhooks**: a team owner
creates one (team settings, *Apps*, *Create an outgoing webhook*) with a name, for example `Homeostat`, and the URL of
the endpoint below; Teams shows a security token. People then mention it in a channel of that team:

```text
@Homeostat pending            lists the steps waiting for approval
@Homeostat approve 4f1c...    approves one
@Homeostat reject 4f1c...     rejects one
```

```csharp
app.MapHomeostatTeamsCommands(o =>
{
    o.SecurityToken = builder.Configuration["Teams:OutgoingWebhookToken"];
    o.AllowedUserIds.Add("6f1c2a77-5b8e-4d55-9a8e-0c2f1e0b9d41");   // Microsoft Entra object IDs; or o.AllowAnyTeamMember = true
});                                                              // POST /homeostat/teams/messages
```

| Check | Behavior |
|---|---|
| Signature (`Authorization: HMAC ...`, HMAC-SHA256 of the body with the security token) | Required, constant-time comparison; otherwise `401` |
| Message age (`timestamp` of the message) and id | Both required; at most 5 minutes old (`MaxRequestAge`); each message id is acted on once per instance (a request replayed to another instance within 5 minutes could be acted on again, but approving or rejecting twice has no further effect) |
| User | `approve` and `reject` need the sender's Entra object ID in `AllowedUserIds`, unless `AllowAnyTeamMember`; `pending` is open to the team |
| Decision | Recorded with the approver `teams:{name} ({object id})`; the reply appears in the thread |

With `AddTeamsNotifications(o => o.ApprovalCommand = "Homeostat")`, approval request cards say what to type. Outgoing
webhooks work in standard (public) channels of the team where they are created, not in private channels or chats, so
point the notification workflow at such a channel. The endpoint authenticates Teams by its signature, so it is mapped
without an authorization policy, and it must be reachable from Teams over HTTPS.

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
endpoint. This check is never turned into a Homeostat signal, so it cannot trigger conditions about itself. It
answers within one second even when the store does not. Keep it out of liveness probes: "a condition is active"
means the application is being healed, not that it must be restarted; use it in readiness or monitoring endpoints.
