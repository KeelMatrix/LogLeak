# Repository guide

## Layout

- `src/KeelMatrix.LogLeak` contains the shipping bounded provider-boundary verifier and its package README.
- `tests/KeelMatrix.LogLeak.Tests` contains focused, product-level xUnit coverage for the shipping API.
- `samples/KeelMatrix.LogLeak.AspNetCore` is a non-packable ASP.NET Core consumer example.
- `tests/LogLeak.PackageConsumer` is an isolated package-reference smoke consumer and is not part of the solution.
- `tests/LogLeak.Probe.*` and `tests/LogLeak.Probe.Runner` contain the Phase 0 corpus, bound to the shipping implementation.
- `build/Invoke-PackageGate.ps1` is the reproducible Release pack, archive inspection, and isolated consumer gate.

## Validation

Run the focused test project during implementation:

```text
dotnet test tests/KeelMatrix.LogLeak.Tests/KeelMatrix.LogLeak.Tests.csproj -c Release
```

Run the package gate before handoff:

```text
pwsh ./build/Invoke-PackageGate.ps1 -Stage All
```

Run the complete Phase 0 corpus against the shipping implementation:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-restore
```

Keep all non-shipping projects non-packable and keep generated output out of source control. Use `docs/DEV.md` for the complete developer command set.

## Scope

The supported boundary is limited to formatted messages, direct string structured properties, nested scope strings, and exception representations. Do not add a CLI, analyzer, source generator, sink-specific implementation, or generic secret scanner.
