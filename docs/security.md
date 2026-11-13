# Security

Homeostat changes what a running application does: it can pause consumers, turn off features, restart processes,
scale deployments and fail over. Treat its endpoints as administrative interfaces and its permissions as those of an
operator. This page collects, for the people who deploy and operate Homeostat, the hardening steps that the other
pages describe one by one. To report a vulnerability, see the [security policy](https://github.com/engineering87/Homeostat/blob/main/SECURITY.md).

## Checklist

- [ ] Every Homeostat endpoint is mapped with a specific authorization policy (or a token, for alerts); none uses
      `allowAnonymous: true` outside development.
- [ ] Slack and Teams endpoints have their signing secret or security token, and a list of allowed approvers.
- [ ] Webhook, incident and chat secrets come from a secret store; keys travel in headers wherever the destination
      allows it (Slack and Teams incoming webhook URLs are secrets themselves).
- [ ] Kubernetes and Azure permissions are limited to the actions the plans use, on the resources they target.
- [ ] Plans ran in dry-run in a real environment before `DryRun = false`; risky steps keep `RequireApproval`.
- [ ] The action budget and cooldowns are set for the worst case, not the average one.
- [ ] Files written by Homeostat (store, audit log, recordings) live where only the application can write them.

## Endpoints

### Authorization policies

`MapHomeostatStatus`, `MapHomeostatControl`, `MapHomeostatDashboard` and `MapHomeostatAlerts` refuse to start
without a policy (or, for alerts, a token) unless you pass `allowAnonymous: true`. The [analyzer](analyzers.md#homeo004)
reports anonymous endpoints outside development.

- Use a specific policy: an operations role or group, not "any authenticated user", especially in an application
  with end-user accounts. The status endpoint reveals condition names, error messages and approval ids; the control
  endpoints pause healing (all of it, or one healing group), approve failovers and flip switches.
- Call `app.UseAuthorization()` and configure authentication (JWT bearer, cookies, Windows, certificates).
- Map status, control and dashboard with the same prefix (`/homeostat` by default): the dashboard calls the other two
  with the user's credentials. The simplest setup uses one policy for all three. When more people may read than act, give
  `MapHomeostatControl` a stricter policy: users without it still see the dashboard, and their actions are refused
  with `403`.
- The usual endpoint conventions apply to the returned `RouteGroupBuilder`: add rate limiting, restrict the hosts or
  keep the endpoints on an internal port.

```csharp
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("homeostat-read", p => p.RequireRole("operations", "on-call"));
    o.AddPolicy("homeostat-operate", p => p.RequireRole("on-call"));
});

app.MapHomeostatStatus("homeostat-read");
app.MapHomeostatDashboard("homeostat-read");      // actions refused for people without the operate role
app.MapHomeostatControl("homeostat-operate");
```

### Cross-site request forgery

The control endpoints require an `Authorization: Bearer` token or the `X-Requested-With` header on every request,
and refuse with `403` the requests that the browser marks as cross-site or same-site (`Sec-Fetch-Site`). A page on
another site cannot add those headers, so users signed in with cookies, Windows authentication, Basic or client
certificates are protected.

> [!WARNING]
> Do not put the endpoints behind a proxy that adds `X-Requested-With` to every request: it removes this protection.

The alert endpoints, which servers call, accept only `Content-Type: application/json` (`415` otherwise) and refuse
requests marked cross-site or same-site with `403`: a form on another page cannot post an alert with an operator's
cookies when the endpoints are protected by a policy rather than a token.

The CLI and the dashboard do not follow redirects: a sign-in or access-denied redirect is reported as an error, never
as a change that was made.

The dashboard is served with a strict Content-Security-Policy (`default-src 'none'`, `frame-ancestors 'none'`), with
`X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`, and renders every value as text.

### Alert ingestion

- Protect `MapHomeostatAlerts` with a policy or `AlertIngestionOptions.Token`: at least 16 characters of random data,
  sent as `Authorization: Bearer`, over HTTPS.
- The Azure Monitor endpoint also accepts the token as the `token` query parameter, because plain action group
  webhooks cannot set headers; the URL may then appear in access logs. Prefer a policy with secure webhooks when you
  can, and rotate the token if logs are shared.
- Alert sources get the `alert:` prefix (`SourcePrefix`), so a sender cannot impersonate the signals of detectors and
  probes. Keep the prefix, and write conditions on `alert:` sources knowing that whoever holds the token can trigger
  them.
- Size limits bound what one sender can cost: `MaxRequestBodySize` (1 MB by default), `MaxAlertsPerRequest` (500) and
  `MaxTrackedSources` (1,000).

### Slack and Microsoft Teams

`MapHomeostatSlackInteractions` and `MapHomeostatTeamsCommands` authenticate the platform by its signature, so they
are mapped without an authorization policy:

- Set `SlackInteractionOptions.SigningSecret` and `TeamsCommandOptions.SecurityToken` from a secret store. Requests
  without a valid HMAC-SHA256 signature are refused with `401`, with a constant-time comparison.
- Requests older than `MaxRequestAge` (5 minutes by default) are refused, against replays; Teams message ids are also
  acted on once per instance.
- Name the approvers: `AllowedUserIds` (Slack user ids, Microsoft Entra object ids). `AllowAnyChannelMember` and
  `AllowAnyTeamMember` let everyone who can post in the channel approve a failover: use them only in small, closed
  channels.
- Expose these endpoints over HTTPS only.
- The Slack endpoint posts its replies only to Slack response URLs (`hooks.slack.com`, `hooks.slack-gov.com`), so a
  forged payload cannot make the application call another address.

See [ASP.NET Core endpoints](aspnetcore.md#slack-approvals).

### Tools that call the endpoints

- The `homeostat` CLI sends its token only to the server in `--url`, and can read it from `HOMEOSTAT_TOKEN` so it
  stays out of the shell history. Give pipelines a token with the narrowest role that their commands need.
- The Aspire commands send a token only when `HomeostatResourceOptions.BearerToken` returns one; map the endpoints
  anonymously only in the development environment.

## Outgoing notifications

- Keep secrets in headers: `WebhookOptions.Headers["Authorization"]`, the PagerDuty routing key, the Opsgenie key
  (sent as `Authorization: GenieKey ...`). Incoming webhook URLs of Slack and Teams are secrets themselves: load them
  from a secret store, not from source control.
- The notifiers escape text that comes from signals (an alert message cannot mention `@channel` in Slack or forge
  links), but events still carry condition names and error messages: send them only to channels whose members may
  see that.
- Use HTTPS for every destination; route through a proxy with `IHttpClientFactory` if your network requires it.
  The notifiers do not follow redirects (a redirect is a failed delivery), so a destination cannot send their
  headers, secrets included, to another host.

## Platform permissions

Platform actions run with the identity of the application, so a compromised application has the same rights. Grant
the least that the plans need.

### Kubernetes

- Use a `Role` (namespace-scoped), with only the verbs of the actions you use, and `resourceNames` for deployments.
- Prefer `ScaleDeployment` (`deployments/scale`) to `RolloutRestart` when it is enough: `patch` on `deployments`
  allows rewriting the pod template.
- `create` on `pods/eviction` covers every pod of the namespace: run the application in its own namespace (evictions still respect PodDisruptionBudgets).
- `CordonNode` needs a `ClusterRole` on `nodes`; grant it only if you use it, with an admission policy that limits the
  change to `spec.unschedulable`.

The full role is in [Cloud and orchestrators](cloud.md#permissions).

### Azure

- Give the managed identity a role assignment **scoped to the resource** the action targets (the App Service site,
  the virtual machine, the Front Door profile), not to the resource group or subscription.
- Use the narrowest built-in role: Website Contributor, Virtual Machine Contributor, CDN Profile Contributor; or a
  custom role with only the actions (`Microsoft.Web/sites/restart/action`, ...) that the plans call.
- `AzureResourceManager` can call any operation the identity is allowed: review each one, and keep
  `requireApproval: true`.

### Brokers and databases

- Service Bus counts need the **Manage** right (or Azure Service Bus Data Owner) on the namespace: give the
  administration client its own identity or connection string, separate from the one that sends and receives.
- Use a read-only monitoring user for Artemis management (Jolokia) and allow only the needed origin in
  `jolokia-access.xml`.
- MongoDB replication lag needs `clusterMonitor`; ping and pool signals need no extra role.
- Database probes run `SELECT 1` by default (`DatabaseProbeOptions.CommandText`): the application's own login is
  enough.

## Rollout and blast radius

The guardrails are also security controls: they bound what a faulty signal, a misconfigured condition or a forged
alert can make Homeostat do.

| Control | Recommendation |
|---|---|
| [Dry-run](guardrails.md#dry-run) | On by default. Turn it off per environment, after reviewing the decisions in the logs |
| [Approvals](healing-plans.md#human-in-the-loop-requireapproval) | Keep `RequireApproval` on every step that changes infrastructure or what users see; platform actions require it by default |
| [Action budget](guardrails.md#action-budget) | Size `MaxActionsPerWindow` for the worst plausible incident; with a shared store it covers the whole cluster |
| [Cooldown and backoff](healing-plans.md#cooldown-backoff) | Long enough for a step to show its effect |
| [Restart limits](cloud.md#restart-the-application) | Keep the store persistent so the limits survive restarts |
| [Kill switch](guardrails.md#kill-switch) | Know how to pause in an incident: endpoint, CLI, dashboard |

## Data at rest

- The file store, the audit log and signal recordings contain condition names, error messages and, with
  `IncludeDetails`, signal messages and tags. Keep them on a volume that only the application can write, with the
  access rules of your logs. Recording files are created readable only by the application's user on Linux and
  macOS.
- With the Redis store, use a dedicated `KeyPrefix`, authentication and TLS. Anyone who can write those keys can
  change cooldowns, holds, the budget, the pause and the switches.

See also: [ASP.NET Core endpoints](aspnetcore.md), [Guardrails](guardrails.md), [Cloud and orchestrators](cloud.md).
