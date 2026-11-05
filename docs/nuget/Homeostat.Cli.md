# Homeostat.Cli

The `homeostat` command-line tool. It operates Homeostat in a running application through its endpoints: read the status, coverage and condition history, pause and resume, approve or reject steps, release held plans, flip switches and send test signals. It also replays recorded signals against a configuration offline, with no running application.

## Install

```bash
dotnet tool install --global Homeostat.Cli --prerelease
```

The current version is a release candidate (`1.0.0-rc.2`): `--prerelease` is needed until 1.0.0 is published.

## Example

```bash
export HOMEOSTAT_URL=https://weather-api.internal
export HOMEOSTAT_TOKEN=...                # bearer token accepted by the endpoints' policy

homeostat status                          # conditions, approvals, switches, budget, recent runs
homeostat pause --for 20m                 # kill switch with automatic resume
homeostat approve 4f1c...                 # ids are shown by 'homeostat status'
homeostat release weather-feed-down       # lift the hold of a plan after a rejection
homeostat switch off serve-cached-forecast
homeostat coverage --last 4h --fail-below 0.8
homeostat replay ./signals --config appsettings.json   # offline: what would these conditions have done?
```

The application maps the endpoints of `Homeostat.AspNetCore` (`MapHomeostatStatus`, `MapHomeostatControl`, and `MapHomeostatAlerts` for `homeostat signal`).

## Documentation

[Command-line tool](https://github.com/engineering87/Homeostat/blob/main/docs/tools.md#command-line-tool)

Works with [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
