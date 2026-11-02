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
  "paused": false,
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
      "steps": [ { "name": "RenewFeedClientAction", "succeeded": true, "verification": "Healed", "error": null } ]
    }
  ]
}
```

Times are those of the application's `TimeProvider`. The endpoint is read-only but reveals condition names,
error messages and approval ids, which is why it needs a policy too.

## Control

| Endpoint | Effect | Response |
|---|---|---|
| `POST /homeostat/pause` | Kill switch on: no new step starts | `204` |
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
- The endpoints are plain `POST` requests without a body or antiforgery token. Call them with bearer tokens
  (tooling, scripts, chat bots). If your users sign in with cookies, do not rely on cookies for these
  endpoints: a page on a sibling site could send the request on their behalf.

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
