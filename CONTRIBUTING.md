# Contributing to Homeostat

Thanks for your interest in contributing.

## Before you start

- For bugs, open an issue with a minimal reproduction.
- For new features, open an issue first to discuss the design: Homeostat acts in production, so
  safety and predictability matter more than feature count.

## Development

Requirements: .NET 10 SDK and the .NET 8 runtime (the libraries and tests target both).

```bash
dotnet restore Homeostat.slnx
dotnet build Homeostat.slnx
dotnet test Homeostat.slnx
```

## Pull requests

- Keep changes focused; one topic per pull request.
- Add or update tests for behavior changes.
- Public APIs must have XML documentation: missing comments are build warnings, and errors in CI.
- New guardrail-relevant behavior must default to the safe option.
- Update `CHANGELOG.md` under `[Unreleased]`.

## Releasing

Maintainers only.

1. Move the `[Unreleased]` entries of `CHANGELOG.md` under a new version heading.
2. Push a tag named after the version: `git tag v0.1.0-preview.1 && git push origin v0.1.0-preview.1`.
3. The `Release` workflow builds, tests, packs (with symbols and SourceLink), pushes the packages to NuGet
   and creates a GitHub release. Versions with a suffix (`-preview.1`) are marked as pre-releases.

The workflow needs a repository secret `NUGET_API_KEY` (a NuGet API key scoped to the `Homeostat*`
packages) and runs in the `nuget` environment, where you can require a manual approval.
After the first release, set `PackageValidationBaselineVersion` in `Directory.Build.props` to the latest
published version, so that accidental breaking changes fail the build.

By contributing, you agree that your contributions are licensed under the MIT License.
