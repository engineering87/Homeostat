# Security Policy

## Supported versions

Only the latest released version receives security fixes. From 1.0 on, fixes go to the latest minor version of the
current major version.

## Reporting a vulnerability

Please **do not** open a public issue. Use GitHub's
[private vulnerability reporting](https://github.com/engineering87/Homeostat/security/advisories/new)
for this repository. Include the affected package and version, a description of the
vulnerability and, if possible, the steps or code to reproduce it.

## Disclosure policy

Homeostat follows coordinated disclosure:

1. Reports are acknowledged within **5 working days**.
2. Within **14 days** of the report you receive an assessment: whether the issue is confirmed, its severity, and the
   expected timeline of a fix.
3. Confirmed vulnerabilities are fixed in a new release, normally within **90 days** of the report, and disclosed
   through a [GitHub security advisory](https://github.com/engineering87/Homeostat/security/advisories) (with a CVE
   when applicable) once the fix is published. Reporters are credited unless they prefer otherwise.
4. Please keep the details private until the advisory is published, or until 90 days have passed, whichever comes
   first.

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

The workflows pin every third-party action to a commit, declare every NuGet package with a single version in
`Directory.Packages.props` (central package management), run with read-only permissions unless a job needs more, and
the repository is assessed weekly by [OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/engineering87/Homeostat).

NuGet restores the lowest version that satisfies each reference, so a declared `1.2.3` resolves to exactly `1.2.3`,
and nuget.org never lets a published version change. The repository deliberately uses neither of the two forms that
Scorecard's Pinned-Dependencies check recognizes for NuGet:

- exact ranges (`[1.2.3]`) would be copied into the dependencies of the published packages and force every
  application using Homeostat onto those exact versions, with conflicts at the next servicing release;
- lock files (`packages.lock.json`) change with every .NET SDK patch for projects marked Native AOT compatible, which
  reference a trimming package versioned with the SDK, and would break restores between the CI, contributors and
  Dependabot.

The two `dotnet restore` steps are therefore reported as unpinned by Scorecard, and the alerts are dismissed as
"won't fix" with this reason.
