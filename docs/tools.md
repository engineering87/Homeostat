# Tools

The `homeostat` command-line tool for operators and pipelines, the `dotnet new` project templates, and where to find
the Grafana dashboard.

## Command-line tool

`homeostat` operates Homeostat in a running application through its endpoints: read the status in a terminal or a
pipeline, pause during a deployment, answer approvals, flip switches, send a signal to rehearse a plan, and replay
recorded signals against a configuration offline.

```bash
dotnet tool install --global Homeostat.Cli --prerelease

export HOMEOSTAT_URL=https://weather-api.internal
export HOMEOSTAT_TOKEN=...            # bearer token accepted by the endpoints' policy

homeostat status                      # conditions, approvals, switches, budget, healing groups, recent runs
homeostat status --json               # the raw status
homeostat status --fail-on-active     # exit code 3 when a condition is active (pipelines, smoke tests)
homeostat pause --for 30m             # kill switch with automatic resume: 30m, 2h, 1d or hh:mm:ss
homeostat resume
homeostat pause --group reports --for 1h   # pause one healing group only (-g for short); the others go on
homeostat resume --group reports
homeostat approve 4f1c...             # ids are shown by 'homeostat status'
homeostat reject 4f1c...
homeostat release weather-feed-down   # lift the hold of a plan after a rejection
homeostat switch on serve-cached-forecast              # for the whole application
homeostat switch on verbose-client-logs --instance     # only for the instance that answers
homeostat switch off circuit:forecast:isolated
homeostat signal disk-almost-full Degraded -m "rehearsal"   # through MapHomeostatAlerts (source alert:disk-almost-full)
homeostat signal ingest-backlog Healthy --value 120         # with a value, for value and trend rules
homeostat coverage --last 4h          # what a game day exercised (default: the last day)
homeostat history                     # latest activations and clears
homeostat replay ./signals --config appsettings.json   # offline: what would these conditions have done?
```

| Option | Default | Meaning |
|---|---|---|
| `--url`, `-u` | `HOMEOSTAT_URL` | Base address of the application |
| `--prefix` | `/homeostat` | Route prefix of the status and control endpoints; `signal` posts to `{prefix}/alerts/signals`, the default of `MapHomeostatAlerts` |
| `--token`, `-t` | `HOMEOSTAT_TOKEN` | Bearer token sent with every request |

The path of `--url` is kept (`https://gateway/weather-api` works), the token is only ever sent to that server, and
every request carries `X-Requested-With`, as the control endpoints require. Redirects are not followed: a redirect to a
sign-in page is reported as an error, never as a change that was made. A request that gets no answer within 30
seconds fails. When the alert endpoints are protected by a token and the others by a policy, run `signal` with the
alert token in `--token`.

Exit codes: `0` success, `1` failure (unreachable, not allowed, not found, invalid input), `3` active conditions with
`--fail-on-active`, `4` coverage below `--fail-below`, `5` a plan that would run more than `--max-heals` times in a
replay. `status`, `coverage` and `history` need the status endpoints (`MapHomeostatStatus`), `signal` the alert
endpoints (`MapHomeostatAlerts`), and the other commands except `replay` the control endpoints (`MapHomeostatControl`).

`pause`, `resume`, `approve`, `reject`, `release` and `switch` change the [shared control
state](persistence-and-clusters.md#shared-control-state): with a shared store, they apply to every instance, whichever
instance answers the request (`switch on --instance` excepted). When the store is down, `pause` and `switch off` still
apply at once on the instance that answers and exit with `0`; they reach the store, and the other instances, when it
answers again. `resume`, `approve`, `reject`, `release` and `switch on` answer `503` and exit with `1`; the CLI prints
the explanation the application gives (nothing was changed, the change may still be applied so check `status` before
trying again, or the change was not made), as the dashboard does. `homeostat release <condition>` lifts the hold of a plan after a rejected approval, without
waiting for the condition to clear: see [Releasing a hold](healing-plans.md#releasing-a-hold).

`homeostat pause --group <name> [--for <duration>]` and `homeostat resume --group <name>` pause and resume one
[healing group](guardrails.md#healing-groups) through `POST {prefix}/groups/{name}/pause` and `.../resume`, with the same
duration format, store behavior, error messages and exit codes as `pause` and `resume` (a group name with a slash is
sent escaped). In an application with groups, `homeostat status` also lists them under `Groups:` (each group's state,
`paused` or `healing`, its budget use, `EXHAUSTED` when it is used up, and why it is paused), and shows
`(group <name>)` after each condition, approval and recent run of a group; without groups the output is unchanged.
When the application answers `404` with a problem detail, the CLI prints it after `Not found:`.

`homeostat coverage [--last 4h] [--fail-below 0.8] [--json]` shows which conditions activated, which plans ran and
which steps never ran, over the last day unless `--last` says otherwise. With `--fail-below`, a share between 0 and 1,
it exits with code `4` when the share of plan steps that ran is lower: see
[Learning and foresight](intelligence.md#coverage).
`homeostat history [--condition feed-down] [--json]` lists the latest activations and clears.
`homeostat signal <source> <Healthy|Degraded|Unhealthy> [--message|-m text] [--value number]` sends one signal through
the generic alert endpoint (`POST {prefix}/alerts/signals`), so its source gets the `alert:` prefix; it exits with `1`
when the endpoint skips it.

`homeostat replay` needs no running application: it replays recorded signals against the conditions of a JSON
configuration file, offline, and prints what the plans would have done. Put it in the pipeline of a configuration
change, with the recordings of a busy day:

```bash
homeostat replay ./signals --config appsettings.Production.json            # directories or files (*.jsonl)
homeostat replay ./signals --config appsettings.json --section Homeostat:Conditions --json
homeostat replay ./signals --config appsettings.json --max-heals 3         # exit code 5 if a plan would run more often
```

| Option | Default | Meaning |
|---|---|---|
| `--config` | (required) | JSON configuration file with the conditions |
| `--section` | `Homeostat:Conditions` | Section of the conditions in that file |
| `--options-section` | `Homeostat:Options` | Section of `HomeostatOptions` in that file, applied when present |
| `--max-heals` | none | Exit with code `5` when a plan would run more than this many times |
| `--json` | off | Print the report as JSON |

A replay does not need the application's code: actions are never executed, only named. The files are merged in time
order (several days and instances form one timeline: pass the files of one instance to see it alone). The options
section is applied because cooldowns and the budget change how often a plan would run. See
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
dotnet new install Homeostat.Templates::1.0.0-rc.1   # a prerelease needs the explicit version until 1.0.0 ships

dotnet new homeostat-webapi -o Forecast.Api      # ASP.NET Core minimal API
dotnet new homeostat-worker -o Ingest.Worker     # worker service
```

| Template | Contains |
|---|---|
| `homeostat-webapi` | A dependency with a health check, turned into a signal; a condition that renews the client and then serves cached data with a switch; the thread pool detector with an observe-only condition; Homeostat's own health check; status, control and dashboard endpoints (anonymous only in Development, policy `operations` otherwise); audit log |
| `homeostat-worker` | A `RestartableBackgroundService` worker; the memory pressure detector; a backlog probe and a condition that restarts the worker when it is stuck; a condition that restarts it when it crashed; audit log |

Both start in **dry-run**: read the logs, then set `Homeostat:DryRun` to `false` in `appsettings.json` (see
[Configuration](configuration.md#global-options) to bind every option instead). The template version matches the
library version it references. CI installs the templates from the packages of each commit, creates a project from each
one and builds it against those packages with warnings as errors, so a template that no longer compiles fails there.

## Grafana dashboard

The Grafana dashboard is described in [Observability](observability.md#grafana-dashboard).

See also: [ASP.NET Core endpoints](aspnetcore.md), [Security](security.md#tools-that-call-the-endpoints),
[Recipes](recipes.md#pause-during-deployments).
