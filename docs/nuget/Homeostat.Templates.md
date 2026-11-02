# Homeostat.Templates

`dotnet new` templates for Homeostat: an ASP.NET Core API (`homeostat-webapi`) and a worker service (`homeostat-worker`), with signals, conditions, plans, the endpoints or a restartable worker, and an audit log set up. Both start in dry-run.

## Install

```bash
dotnet new install Homeostat.Templates::1.0.0-rc.1
```

The current version is a release candidate: name it explicitly (`::1.0.0-rc.1`) until 1.0.0 is published, then `dotnet new install Homeostat.Templates` is enough.

## Example

```bash
dotnet new homeostat-webapi -o Forecast.Api      # ASP.NET Core minimal API
dotnet new homeostat-worker -o Ingest.Worker     # worker service
```

Read the logs in dry-run, then set `Homeostat:DryRun` to `false` in `appsettings.json`. The templates reference the Homeostat packages of the same version as the template package.

## Documentation

[Project templates](https://github.com/engineering87/Homeostat/blob/main/docs/tools.md#project-templates)

Works with [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
