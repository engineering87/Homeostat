# Versioning

What each version number promises, what counts as a breaking change and how long a version is supported. Read it
before upgrading or before depending on Homeostat's JSON, routes or source names from other tools.

Homeostat follows [Semantic Versioning 2.0](https://semver.org) from version 1.0.0. All packages are released
together with the same version number, so `Homeostat.Redis 1.2.0` always goes with `Homeostat 1.2.0` (and
`Homeostat.Grpc 1.2.0-preview`, see [Stable and preview packages](#stable-and-preview-packages)).

## Current status

The current version is **1.0.0-rc.1**, a release candidate. NuGet hides prerelease versions by default, so install
the packages with `--prerelease` until 1.0.0 ships:

```bash
dotnet add package Homeostat --prerelease
```

During the release candidate, the public API changes only if a problem found in the candidate requires it; any such
change is listed in the [changelog](../CHANGELOG.md). From 1.0.0 the rules below apply.

## Stable and preview packages

Every package is released with every version, but not every package makes the same promise:

| Stability | Packages | Version (release 1.2.0) | Promise |
|---|---|---|---|
| Stable | `Homeostat`, `Homeostat.HealthChecks`, `Homeostat.AspNetCore`, `Homeostat.Http`, `Homeostat.Polly`, `Homeostat.Redis`, `Homeostat.MongoDB`, `Homeostat.ServiceBus`, `Homeostat.RabbitMQ`, `Homeostat.Kafka`, `Homeostat.Artemis`, `Homeostat.Kubernetes`, `Homeostat.Testing`, `Homeostat.Cli`, `Homeostat.Templates` | `1.2.0` | The rules below |
| Preview | `Homeostat.Azure`, `Homeostat.EntityFrameworkCore`, `Homeostat.Grpc`, `Homeostat.Caching`, `Homeostat.FeatureManagement`, `Homeostat.OpenFeature`, `Homeostat.Aspire.Hosting` | `1.2.0-preview` | Works and is tested, depends on the stable packages of the same release; its public API may still change in a minor version, listed in the changelog |

A preview package becomes stable in a minor version, once it has been used in production and its API has settled; it
then follows the rules below like the others. A stable package never goes back to preview. During a release candidate
(`1.0.0-rc.1`) every package has the same prerelease version.

In the build, a project is a preview with `<HomeostatPreview>true</HomeostatPreview>`. `Directory.Build.targets` adds
the suffix when the version has none (a release candidate keeps its own), and the release workflow sets the version
from the tag (`-p:HomeostatVersion=1.2.0`).

## What the version number promises

| Release | May contain | Never contains |
|---|---|---|
| Patch (1.0.x) | Bug fixes, security fixes, documentation | New public API, changes of behavior other than the fix |
| Minor (1.x.0) | New features, new packages, new optional parameters on new overloads, new enum members, new fields in JSON documents, new log messages, new .NET targets | Breaking changes |
| Major (x.0.0) | Breaking changes, announced in the changelog with a migration note | |

## What counts as a breaking change

Homeostat is used from code, from configuration and from other tools (dashboards, alert rules, log queries,
scripts), so its contract is wider than its C# API. Each of the following is a breaking change, made only in a major
version:

- **Public API**: removing or renaming a public type or member, changing a signature or a default parameter value,
  changing the underlying value of an enum member, sealing a class or adding an abstract member to a public base class or interface without a default
  implementation. Public positional records (such as `RestartLimits`) keep their constructor fixed: new fields arrive
  as init-only properties with a default, so adding a field in a minor version is not a breaking change.
- **Default signal source names**, such as `threadpool`, `http:{client}`, `cache:memory`, `sqlclient:pool`,
  `npgsql:pool`, `http-server:errors`, `rabbitmq:{queue}:messages`, `servicebus:{queue}:messages` and
  `hosted-service:{type}`: conditions and alert rules refer to them. See
  [Naming sources](signals.md#naming-sources).
- **JSON documents**: the answers of the status, history, coverage, control and alert endpoints, the audit log and webhook
  events, the signal recording files, and the data written by the file and Redis stores (file format, Redis keys). A
  new version reads the data written by the previous ones of the same major version. Fields can be added; existing
  fields keep their names, types and meaning, and enums stay written as names. For the endpoints the JSON is the
  contract: the .NET types that produce it are internal, so read it as JSON (or with your own types).
- **Configuration keys** read by `AddConditionsFromConfiguration` and their meaning.
- **Endpoint routes** and their default prefixes, **CLI commands, options and exit codes**, metric and activity
  names and tags.
- **Safety defaults**: dry-run on by default, the default budget, cooldown and timeouts, approval by default for
  platform actions. A default may become safer in a minor version (for example a new guard), never less safe.

Not part of the contract: the text of log messages and exception messages (match on event names and outcomes, not
on text), internal types, the timing of background work, and the layout of the dashboard.

## How it is enforced

- **Recorded public API.** The public API of every library package is listed in [`api/`](../api), one file per package,
  generated by reflection. A test fails when the code no longer matches, so every change to the public API shows up
  as a diff of `api/*.txt` in the pull request, where reviewers approve it (see
  [Contributing](../CONTRIBUTING.md#public-api)).
- **Package validation.** After 1.0.0 is published, the build compares each package with the latest published
  version (`PackageValidationBaselineVersion`), so a binary breaking change fails the build before it is released.
- **Changelog.** Every release lists its changes in [CHANGELOG.md](../CHANGELOG.md), with breaking changes in their own section.

## Support

Security fixes and bug fixes go to the **latest minor version of the current major version**, as stated in the
[security policy](../SECURITY.md). Upgrading within a major version is meant to be a package update with no code
changes, so stay on the latest minor version.

## .NET support

- Homeostat targets **.NET 10**, a long-term support (LTS) release.
- New .NET versions are supported as they are released; targets for them are added in minor versions.
- A .NET version is no longer targeted once it has reached its end of support, and only in a major version.

See also: [Platforms](platforms.md) for operating systems and client library versions ·
[Packages](packages.md) · [Changelog](../CHANGELOG.md)
