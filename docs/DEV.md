# Development guide

This guide covers local validation for the LogLeak repository. It does not change the package's consumer setup.

## Prerequisites

- PowerShell 7 (`pwsh`).
- The .NET SDK selected by `global.json` (`10.0.401`) and the supported .NET 8 runtime used by the executable tests and package consumer.
- Network access to the public NuGet feed for a clean restore and the isolated consumer.

Check the toolchain from the repository root:

```powershell
pwsh --version
dotnet --version
```

Repository validation sets `KEELMATRIX_NO_TELEMETRY=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1` for every spawned process. The focused test project loads these values from its committed `tests/KeelMatrix.LogLeak.Tests/LogLeak.Tests.runsettings` automatically through its project file. The package gate applies the same settings to its consumer automatically.

## Restore and focused tests

```powershell
dotnet restore LogLeak.Probe.sln --configfile NuGet.config --force
dotnet test tests/KeelMatrix.LogLeak.Tests/KeelMatrix.LogLeak.Tests.csproj -c Release
```

## Build, pack, inspect, and package smoke

The single gate entry point accepts `All`, `Build`, `Pack`, `Inspect`, and `Smoke` stages. `All` creates clean output under `artifacts/gate`, validates the exact package set, and runs the consumer smoke.

```powershell
pwsh ./build/Invoke-PackageGate.ps1 -Stage Build
pwsh ./build/Invoke-PackageGate.ps1 -Stage Pack
pwsh ./build/Invoke-PackageGate.ps1 -Stage Inspect
pwsh ./build/Invoke-PackageGate.ps1 -Stage Smoke
pwsh ./build/Invoke-PackageGate.ps1 -Stage All
```

The consumer smoke restores `KeelMatrix.LogLeak` only from the just-built local package. Its package cache, HTTP cache, CLI home, and project directory are fresh for each run. The isolated consumer runs on the supported `net8.0` runtime and explicitly references the package's `lib/netstandard2.0/KeelMatrix.LogLeak.dll` asset from the restored package. Its own runtime output prints and checks the loaded assembly path. It exercises clean, planted-leak, source-generated logging, sentinel-safe scope/argument boundaries, verification during formatter and exception callbacks, string-valued dictionary-state, same-probe reentrancy, and deterministic capture-bound paths; unsafe or bounded paths fail explicitly as `Inconclusive`.

The release package job is extracted to `.github/workflows/package-gate.yml`. It installs the `10.0.x` SDK and the supported `8.0.x` SDK/runtime required by the `net8.0` test corpus and package consumer, then runs the exact package gate. CI calls this reusable workflow with artifact upload disabled, providing a non-publishing release-package-job path; the workflow is also manually dispatchable with a package version for the same smoke.

## Formatting and analysis

```powershell
dotnet format whitespace LogLeak.Probe.sln --verify-no-changes --no-restore
dotnet format analyzers src/KeelMatrix.LogLeak/KeelMatrix.LogLeak.csproj --verify-no-changes --no-restore --severity error
```

For an intentional public API addition, use the analyzer code fix and review the generated ledger:

```powershell
dotnet format analyzers src/KeelMatrix.LogLeak/KeelMatrix.LogLeak.csproj --diagnostics RS0016 --severity warn
```

## Dependency audit

```powershell
dotnet list LogLeak.Probe.sln package --vulnerable --include-transitive
```

The package gate is the required local package/consumer validation. It does not publish packages, create tags, or require a remote build service.
