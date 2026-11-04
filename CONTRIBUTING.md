# Contributing to Homeostat

Thanks for your interest in contributing.

## Before you start

- For bugs, open an issue with a minimal reproduction.
- For new features, open an issue first to discuss the design: Homeostat acts in production, so
  safety and predictability matter more than feature count.

## Development

Requirements: .NET 10 SDK.

```bash
dotnet restore Homeostat.slnx
dotnet build Homeostat.slnx
dotnet test Homeostat.slnx
```

## Commit messages

Commits and pull request titles follow [Conventional Commits](https://www.conventionalcommits.org):

```text
<type>(<scope>): <subject in lower case>

<optional body: what and why>

<optional footers, for example BREAKING CHANGE: ... or Co-Authored-By: ...>
```

| Part | Allowed values |
|---|---|
| Type | `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, `chore`, `style`, `revert` |
| Scope (optional) | `core`, `analyzers`, `artemis`, `aspire`, `aspnetcore`, `azure`, `caching`, `cli`, `data`, `efcore`, `featureflags`, `grpc`, `healthchecks`, `http`, `kafka`, `kubernetes`, `mongodb`, `polly`, `rabbitmq`, `redis`, `servicebus`, `templates`, `testing`, `samples`, `docs`, `deps`, `ci`, `release` |

Examples: `feat(core): add hysteresis to conditions`, `fix(http): dispose the response when IsFailure throws`,
`docs: explain approvals`. A `!` after the type or scope (`feat(aspnetcore)!: ...`) marks a breaking change.
The header can be up to 100 characters.

The rules live in `commitlint.config.mjs` and are checked:

- **locally**, by a `commit-msg` hook: run `npm install` once (Node.js 22.12 or later); it installs commitlint
  and points git to the hooks in `.githooks`. Without Node.js the hook does nothing.
- **in CI**, by the `Commit lint` workflow, on every commit of a pull request and on the pull request title,
  which becomes the commit message when the pull request is squash-merged.

Dependabot is configured to write compliant messages (`build(deps): ...`, `ci(deps): ...`).

## Public API

The public API of every package is listed in [`api/`](api), one file per package, and `PublicApiTests` fails when the
code no longer matches. When a change to the public API is intended:

```bash
HOMEOSTAT_ACCEPT_API=1 dotnet test Homeostat.slnx --filter PublicApiTests
```

then review the diff of `api/*.txt` and include it in the pull request: reviewers see every API change there.

From 1.0 on, removing or changing a public member is a breaking change and waits for a major version. To keep
additions non-breaking:

- add new members of a public record as `init` properties, not as constructor parameters;
- add new members of a public interface as default interface members;
- add parameters as new overloads, not as optional parameters on an existing method;
- number new enum members explicitly and never reuse a value; outcome enums start at 1, so 0 is never a success.

Signal source names and the JSON written by the endpoints and the stores are part of the contract too: users type
source names in `ForSource`, and dashboards and scripts read the JSON. Default sources follow
`{technology}[:{name}][:{metric}]` (`redis`, `npgsql:pool`, `kafka:telemetry:lag`); the process's own runtime detectors use one
word (`threadpool`, `memory`, `exceptions`), and health checks keep their own name.

Other conventions: `Task` for extension points that run user code once per action (actions, verifiers, sinks, consumers),
`ValueTask` for members the engine calls often and that usually complete synchronously (stores, detectors,
coordinators, control queries). Every asynchronous public method takes a `CancellationToken`.

## Native AOT and performance

The libraries are `IsAotCompatible`: trimming and AOT analyzer warnings are errors in CI. Serialize JSON with the
source-generated contexts or `JsonNode`, never with reflection. The `native-aot` CI job publishes and runs
`tests/Homeostat.AotSmoke`; extend it when a package gains a feature that must work in AOT applications.

Code that runs for every signal (publishing, the engine loop, condition rules) must not allocate in steady state.
`benchmarks/Homeostat.Benchmarks` measures it ([docs/performance.md](docs/performance.md)): run it before and after a
change to those paths and mention the figures in the pull request.

## Pull requests

- Keep changes focused; one topic per pull request.
- Give the pull request a Conventional Commits title (see above).
- Add or update tests for behavior changes. For arithmetic that turns numbers into decisions (ratios, percentiles,
  trends, windows), add a property to `PropertyTests` (FsCheck) as well: it checks an invariant against hundreds of
  generated inputs, and reports the smallest failing case.
- Public APIs must have XML documentation: missing comments are build warnings, and errors in CI.
- New guardrail-relevant behavior must default to the safe option.
- Update `CHANGELOG.md` under `[Unreleased]`.

## Releasing

Maintainers only.

1. Move the `[Unreleased]` entries of `CHANGELOG.md` under a new version heading.
2. Push a tag named after the version: `git tag v1.0.0 && git push origin v1.0.0`.
3. The `Release` workflow builds, tests, packs (with symbols and SourceLink), signs the packages when a certificate is
   configured, produces an SBOM and a build provenance attestation, pushes the packages to NuGet and creates a GitHub
   release with the packages and the SBOM. Versions with a suffix (`-preview.1`) are marked as pre-releases.

The workflow publishes with nuget.org [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
so no API key is stored: on nuget.org, add a trusted publishing policy (owner `engineering87`, repository `Homeostat`,
workflow `release.yml`, environment `nuget`), and save the nuget.org profile name as the repository secret
`NUGET_USER`. The workflow runs in the `nuget` environment, where you can require a manual approval. To author-sign the packages,
add `NUGET_SIGNING_CERTIFICATE` (the code signing certificate as a base64 PFX) and `NUGET_SIGNING_PASSWORD`, and
register the certificate on the nuget.org account first.
After the first release, set `PackageValidationBaselineVersion` in `Directory.Build.props` to the latest
published version, so that accidental breaking changes fail the build.

Notes for dependencies:

- Package versions live only in `Directory.Packages.props` (central package management), always as exact versions:
  no ranges (`[1.0, 2.0)`) and no floating versions (`1.*`). Project files reference packages without a version.
- The templates reference the Homeostat packages with the token `HOMEOSTAT_VERSION`, replaced at pack time, so they
  always match the release. Other versions in the template projects (such as `Microsoft.Extensions.Hosting`) are not
  updated by Dependabot: update them with the Microsoft.Extensions servicing releases.
- The analyzers reference `Microsoft.CodeAnalysis.CSharp` **5.0.0**, the Roslyn of the .NET 10.0.1xx SDK, on
  purpose: an analyzer built against a newer Roslyn does not load in older compilers (CS9057). Dependabot ignores it.
- Move shipped analyzer rules from `AnalyzerReleases.Unshipped.md` to `AnalyzerReleases.Shipped.md` at release.

By contributing, you agree that your contributions are licensed under the MIT License.
