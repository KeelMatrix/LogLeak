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

   The isolated package consumer runs on the supported `net8.0` runtime but explicitly references the packaged `lib/netstandard2.0/KeelMatrix.LogLeak.dll` asset. The consumer prints and checks the loaded assembly path, then exercises clean, planted-leak, source-generated logging, sentinel-safe boundaries, during-capture verification, and deterministic-bound-breach paths.

5. The release `package` job uses the reusable `.github/workflows/package-gate.yml`, which provisions `10.0.x` and the supported `8.0.x` SDK/runtime in the same job before running the consumer. CI invokes that workflow with artifact upload disabled, and a `workflow_dispatch` run of the reusable workflow is a non-publishing way to exercise the exact package setup. Record the workflow run id, `Package / inspect and consume` job, and the consumer asset-proof line from the run output.

6. Run the contract scenarios, including the repository-controlled tag-version resolver and downstream artifact-name check:

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

The workflow is triggered by tags matching `v[0-9]*`; `build/Resolve-ReleaseVersion.ps1` then independently rejects any tag that is not exactly `vX.Y.Z`. It checks out that tag, confirms the tag points at the checked-out commit, and derives the package version through `build/Resolve-ReleaseVersion.ps1` before writing the version output consumed by downstream artifact names. It then runs the same contract validator, restores from NuGet.org, builds and tests in Release, runs the package/archive/consumer gate, and checks that the artifact directory contains exactly one `.nupkg` and one `.snupkg` for the tag version.

Only after those checks pass does the publish job obtain a short-lived NuGet credential through GitHub OIDC (`NuGet/login@v1`, NuGet.org user `dmitriyzen`) and push the two exact artifacts. The `.nupkg` push uses `--no-symbols`; the `.snupkg` is submitted exactly once in the following step, and both native command exit codes are checked explicitly. No long-lived NuGet API key is used. The workflow sets `KEELMATRIX_NO_TELEMETRY=1` so release validation is not production usage.

## Troubleshooting

The workflow stops before publication if the tag is not exactly `vX.Y.Z`, the changelog entry is still planned, the release date does not match the tagged commit date, package/dependency/install metadata differs, or any unexpected artifact is present. Correct the source commit and use a new unused release version when a public version has already been published.
