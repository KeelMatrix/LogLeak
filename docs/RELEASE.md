# Release process

This repository publishes `KeelMatrix.LogLeak` from an exact semantic-version tag. The release workflow is intentionally tag-only: ordinary branch pushes do not publish packages.

## Before tagging

1. Update the package version in `Directory.Build.props` and keep central dependency versions exact in `Directory.Packages.props`.
2. Add a finalized entry to `CHANGELOG.md` in the form `## [X.Y.Z] - YYYY-MM-DD`. Keep `## [Unreleased]` separate. For the first public release, use only an `Added` section and describe the product users receive.
3. Run the repository-controlled contract check with the intended tag and date. The date is required and must match the date of the exact commit that will be tagged:

   ```powershell
   # Replace <exact-commit-date> with the YYYY-MM-DD date of the exact commit being tagged.
   pwsh ./build/Validate-ReleaseContract.ps1 -Version 0.1.0 -Tag v0.1.0 -ExpectedReleaseDate <exact-commit-date>
   ```

   The check verifies the finalized changelog entry, package version, tag, exact dependency versions, install examples, release date, and first-release wording. It fails closed on a planned or unreleased entry, a metadata mismatch, or remediation-history wording.

4. Run the complete local package gate:

   ```powershell
   pwsh ./build/Invoke-PackageGate.ps1 -Stage All -Version 0.1.0
   ```

5. Run the contract scenarios:

   ```powershell
   pwsh ./build/Test-ReleaseContract.ps1
   ```

Commit and push the verified result before creating its release tag.

## Tag-driven release

Create and push an exact `vX.Y.Z` tag for the verified commit:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The workflow is triggered by tags matching `v[0-9]+.[0-9]+.[0-9]+`; it then independently rejects any tag that is not exactly `vX.Y.Z`. It checks out that tag, confirms the tag points at the checked-out commit, and derives the package version from it. It then runs the same contract validator, restores from NuGet.org, builds and tests in Release, runs the package/archive/consumer gate, and checks that the artifact directory contains exactly one `.nupkg` and one `.snupkg` for the tag version.

Only after those checks pass does the publish job obtain a short-lived NuGet credential through GitHub OIDC (`NuGet/login@v1`, NuGet.org user `dmitriyzen`) and push the two exact artifacts. No long-lived NuGet API key is used. The workflow sets `KEELMATRIX_NO_TELEMETRY=1` so release validation is not production usage.

## Troubleshooting

The workflow stops before publication if the tag is not exactly `vX.Y.Z`, the changelog entry is still planned, the release date does not match the tagged commit date, package/dependency/install metadata differs, or any unexpected artifact is present. Correct the source commit and use a new unused release version when a public version has already been published.
