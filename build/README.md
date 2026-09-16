# Build validation

`Invoke-PackageGate.ps1` is the reproducible local package validation entry point. It builds the shipping project, creates the exact `.nupkg` and `.snupkg` set, inspects both archives, and runs an isolated package consumer.

From the repository root:

```powershell
pwsh ./build/Invoke-PackageGate.ps1 -Stage All
```

The consumer smoke uses a temporary package cache and maps `KeelMatrix.LogLeak` only to the local feed. Local validation disables telemetry with `KEELMATRIX_NO_TELEMETRY=1`.
