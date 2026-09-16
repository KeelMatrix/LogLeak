# Development guide

This guide covers local validation for the LogLeak repository. It does not change the package's consumer setup.

## Prerequisites

- PowerShell 7 (`pwsh`).
- The .NET SDK selected by `global.json` (`10.0.401`).
- Network access to the public NuGet feed for a clean restore and the isolated consumer.

Check the toolchain from the repository root:

```powershell
pwsh --version
dotnet --version
```

For local validation, set `KEELMATRIX_NO_TELEMETRY=1` in the current process. The package gate applies this setting to its consumer automatically.

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

The consumer smoke restores `KeelMatrix.LogLeak` only from the just-built local package. Its package cache, HTTP cache, CLI home, and project directory are fresh for each run. It exercises one clean first-success path and one planted leak, and rejects any assertion diagnostic containing the planted value.

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
