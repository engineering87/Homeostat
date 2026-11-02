# Tools

## Command-line tool

`homeostat` operates Homeostat in a running application through its endpoints: read the status in a terminal or a
pipeline, pause during a deployment, answer approvals, flip switches, send a signal to rehearse a plan, and replay
recorded signals against a configuration offline.

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
homeostat coverage --last 4h          # what a game day exercised
homeostat history                     # latest activations and clears
homeostat replay ./signals --config appsettings.json   # offline: what would these conditions have done?
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
`--fail-on-active`, `4` coverage below `--fail-below`, `5` a plan that would run more than `--max-heals` times in a
replay. `status`, `coverage` and `history` need the status endpoints (`MapHomeostatStatus`), `signal` the alert endpoints,
the other commands but `replay` the control endpoints.

`homeostat coverage --last 4h [--fail-below 0.8] [--json]` shows which conditions activated, which plans ran and which
steps never ran (exit code `4` below the threshold): see [Learning and foresight](intelligence.md#coverage).
`homeostat history [--condition feed-down] [--json]` lists the latest activations and clears.

`homeostat replay` needs no running application: it replays recorded signals against the conditions of a
configuration file, offline, and prints what the plans would have done. Put it in the pipeline of a configuration
change, with the recordings of a busy day:

```bash
homeostat replay ./signals --config appsettings.Production.json            # directories or files (*.jsonl)
homeostat replay ./signals --config appsettings.json --section Homeostat:Conditions --json
homeostat replay ./signals --config appsettings.json --max-heals 3         # exit code 5 if a plan would run more often
```

Action names do not need the application's code in a replay: actions are never executed, only named. The files are
merged in time order (several days and instances form one timeline: pass the files of one instance to see it alone),
and the `HomeostatOptions` in `Homeostat:Options` of the same file (`--options-section`) are applied, since cooldowns
and the budget change how often a plan would run. See
[Learning and foresight](intelligence.md#recording-and-replay).

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
