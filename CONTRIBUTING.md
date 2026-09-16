# Contributing

## Before you begin

Please check existing issues before opening a new one. Read the [Code of Conduct](CODE_OF_CONDUCT.md), and use the private process in [SECURITY.md](SECURITY.md) for vulnerability reports.

Use synthetic values in tests and examples. Never include production credentials, customer data, or other live sensitive data in an issue, pull request, fixture, or log.

## Making changes

Keep changes focused on the documented provider-boundary verification behavior. Add or update product-level tests and documentation when behavior changes. Do not add a CLI, source generator, generic secret scanner, or sink-specific implementation without first updating the product specification.

## Validation

From the repository root, install the .NET SDK selected by `global.json` and run:

```powershell
dotnet restore LogLeak.Probe.sln --configfile NuGet.config --force
dotnet test tests/KeelMatrix.LogLeak.Tests/KeelMatrix.LogLeak.Tests.csproj -c Release
pwsh ./build/Invoke-PackageGate.ps1 -Stage All
dotnet format whitespace LogLeak.Probe.sln --verify-no-changes --no-restore
```

The package gate uses a fresh consumer directory and disables telemetry for local validation. See [docs/DEV.md](docs/DEV.md) for individual build, pack, inspection, smoke, and API-analysis commands.

## Public API changes

The shipping assembly uses Public API analyzers. After an intentional public API change, run:

```powershell
dotnet format analyzers src/KeelMatrix.LogLeak/KeelMatrix.LogLeak.csproj --diagnostics RS0016 --severity warn
```

Review the generated `PublicAPI.Unshipped.txt` change, then promote an accepted first-release surface to `PublicAPI.Shipped.txt` according to the release process. Public API files contain analyzer entries only.

## Pull requests

Describe the problem, the user-visible result, tests run, documentation changes, package/consumer validation, and any remaining platform or dependency limitation. Keep the pull request focused and use the repository template.
