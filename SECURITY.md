# Security Policy

## Supported versions

Only the latest released version receives security fixes. From 1.0 on, fixes go to the latest minor version of the
current major version.

## Reporting a vulnerability

Please **do not** open a public issue. Use GitHub's
[private vulnerability reporting](https://github.com/engineering87/Homeostat/security/advisories/new)
for this repository. You will receive an acknowledgement as soon as possible, and a fix or mitigation
plan once the report has been assessed.

## Verifying a release

Every release is built from a tag by the [release workflow](.github/workflows/release.yml), on GitHub-hosted runners,
and carries:

- **Build provenance** for each package: a signed statement, stored by GitHub, that the package was built by that
  workflow from that commit. Check a package downloaded from nuget.org with the GitHub CLI:

  ```bash
  gh attestation verify Homeostat.1.0.0.nupkg --repo engineering87/Homeostat
  ```

  The same signed bundle is attached to the release as `homeostat-<version>.intoto.jsonl`.

- **An SBOM** in SPDX format (`homeostat-<version>.spdx.json`, attached to the GitHub release): the dependencies of
  every package.
- **Signatures**: nuget.org signs every package it accepts (repository signature). When the maintainer's code signing
  certificate is configured, the packages are also author-signed; check with `dotnet nuget verify --all <package>`.

The workflows pin every third-party action to a commit, restore NuGet packages in locked mode from the committed
`packages.lock.json` files, run with read-only permissions unless a job needs more, and
the repository is assessed weekly by [OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/engineering87/Homeostat).
