# Tools

## Command-line tool

`homeostat` operates Homeostat in a running application through its endpoints: read the status in a terminal or a
pipeline, pause during a deployment, answer approvals, flip switches, send a signal to rehearse a plan.

```bash
dotnet tool install --global Homeostat.Cli

export HOMEOSTAT_URL=https://weather-api.internal
export HOMEOSTAT_TOKEN=...            # bearer token accepted by the endpoints' policy

homeostat status                      # conditions, approvals, switches, budget, recent runs
homeostat status --json               # the raw status
homeostat status --fail-on-active     # exit code 3 when a condition is active (pipelines, smoke tests)
homeostat pause --for 30m             # kill switch with automatic resume: 30m, 2h, 1d or hh:mm:ss
homeostat resume
homeostat approve 4f1c...             # ids are shown by 'homeostat status'
homeostat reject 4f1c...
homeostat switch on serve-cached-forecast
homeostat switch off circuit:forecast:isolated
homeostat signal disk-almost-full Degraded -m "rehearsal"   # through MapHomeostatAlerts (source alert:disk-almost-full)
```

| Option | Default | Meaning |
|---|---|---|
| `--url`, `-u` | `HOMEOSTAT_URL` | Base address of the application |
| `--prefix` | `/homeostat` | Route prefix of the status and control endpoints; `signal` posts to `{prefix}/alerts/signals`, the default of `MapHomeostatAlerts` |
| `--token`, `-t` | `HOMEOSTAT_TOKEN` | Bearer token sent with every request |

The path of `--url` is kept (`https://gateway/weather-api` works), the token is only ever sent to that server, and
every request carries `X-Requested-With`, as the control endpoints require. When the alert endpoints are protected by
a token and the others by a policy, run `signal` with the alert token in `--token`.

Exit codes: `0` success, `1` failure (unreachable, not allowed, not found, invalid input), `3` active conditions with
`--fail-on-active`. The status endpoint is needed for `status`, the control endpoints for the other commands, and
the alert endpoints for `signal`.

A deployment script, for example:

```bash
homeostat pause --for 20m            # no healing while instances restart
./deploy.sh
homeostat resume
homeostat status --fail-on-active    # fail the pipeline if something is still wrong
```

## Project templates

```bash
dotnet new install Homeostat.Templates

dotnet new homeostat-webapi -o Forecast.Api      # ASP.NET Core minimal API
dotnet new homeostat-worker -o Ingest.Worker     # worker service
```

| Template | Contains |
|---|---|
| `homeostat-webapi` | A dependency with a health check; a condition that renews the client and then serves cached data with a switch; the thread pool detector; status, control and dashboard endpoints (anonymous only in Development, policy `operations` otherwise); audit log |
| `homeostat-worker` | A `RestartableBackgroundService` worker; a backlog probe and a condition that restarts the worker when it is stuck; a condition that restarts it when it crashed; audit log |

Both start in **dry-run**: read the logs, then set `Homeostat:DryRun` to `false` in `appsettings.json`. The template
version matches the library version it references.

## Grafana dashboard

See [Observability](observability.md#grafana-dashboard).
