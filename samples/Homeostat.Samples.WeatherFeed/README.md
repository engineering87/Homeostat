# Weather feed sample

A small ASP.NET Core application whose weather feed can be broken on demand, so you can watch Homeostat
detect the problem, heal it, ask for approval and record everything.

```bash
dotnet run --project samples/Homeostat.Samples.WeatherFeed
```

The examples below assume the application listens on `http://localhost:5000`; use the URL printed at startup.

## 1. A fault that Homeostat fixes on its own

```bash
curl -X POST http://localhost:5000/faults/stuck-client
```

Within a few seconds the `weather-feed` health check turns unhealthy, the `weather-feed-down` condition becomes
active and Homeostat renews the client. The logs show each decision; `GET /feed` shows one more renewal.

## 2. A fault that needs a person

```bash
curl -X POST http://localhost:5000/faults/primary-outage
curl http://localhost:5000/homeostat/status
```

Renewing does not help, so Homeostat escalates to the backup provider, a step that requires approval. The
request appears under `pendingApprovals` in the status. Approve it (or reject it) with its id:

```bash
curl -X POST http://localhost:5000/homeostat/approvals/<id>/approve
```

Without an answer within five minutes, the run stops and the condition stays visible in the status; after the
cooldown, Homeostat tries again. A rejection holds the plan until the feed recovers: a person has taken over.

## 3. Everything else

| Try | What happens |
|---|---|
| `POST /homeostat/pause`, then a fault | Conditions are tracked, nothing runs; `/health` reports Homeostat as degraded |
| `POST /homeostat/resume` | Healing resumes on the next unhealthy check |
| `POST /restore` | The feed recovers; the condition clears after 6 seconds of good checks (`ClearAfter`) |
| `logs/homeostat-*.jsonl` | The audit trail of every event |
| `data/homeostat.json` | Cooldowns, budget and history: restart the sample and they are still there |

The sample turns dry-run off and maps the control endpoints without authorization, to keep the demo short.
Do neither in a real application.
