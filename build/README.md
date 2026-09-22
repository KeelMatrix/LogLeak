# Build validation

`Invoke-PackageGate.ps1` is the reproducible local package validation entry point. It builds the shipping project, creates the exact `.nupkg` and `.snupkg` set, inspects both archives, and runs an isolated package consumer against the `netstandard2.0` asset.

From the repository root:

```powershell
pwsh ./build/Invoke-PackageGate.ps1 -Stage All
```

The focused test project loads its committed telemetry-isolating runsettings automatically. The consumer smoke uses a temporary package cache and maps `KeelMatrix.LogLeak` only to the local feed. It covers clean, planted-leak, string-valued dictionary-state, same-probe reentrant, and capture-bound behavior. Package validation disables telemetry with `KEELMATRIX_NO_TELEMETRY=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1`.

The source-generated logging documentation is synchronized by `docs/examples/SourceGeneratedLoggingExample.cs`. Both README blocks and the focused/package-consumer coverage are checked by `build/Test-DocumentedExample.ps1`, which is also run by `build/Test-ReleaseContract.ps1`.

The release package setup lives in `.github/workflows/package-gate.yml`, which installs the `10.0.x` SDK and supported `8.0.x` SDK/runtime before running this gate. The reusable workflow is called by the publishing workflow and by the non-publishing CI package job; it also supports manual dispatch. The consumer runs on `net8.0` while explicitly loading and reporting the packaged `lib/netstandard2.0/KeelMatrix.LogLeak.dll` asset.
