# Repository guide

## Layout

- `src/KeelMatrix.LogLeak` contains the shipping bounded provider-boundary verifier and its package README.
- `tests/KeelMatrix.LogLeak.Tests` contains focused, product-level xUnit coverage for the shipping API.
- `samples/KeelMatrix.LogLeak.AspNetCore` is a non-packable ASP.NET Core consumer example.
- `tests/LogLeak.PackageConsumer` is an isolated package-reference smoke consumer and is not part of the solution.
- `src/LogLeak.Probe.Core` and `tests/LogLeak.Probe.*` contain the retained feasibility corpus.

## Validation

Run the focused test project during implementation:

```text
dotnet test tests/KeelMatrix.LogLeak.Tests/KeelMatrix.LogLeak.Tests.csproj -c Release
```

Run the complete probe corpus only when validating the retained corpus:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-restore
```

Before handoff, build `LogLeak.Probe.sln` in Release and inspect the package plus an isolated package consumer. Keep all non-shipping projects non-packable and keep generated output out of source control.

## Scope

The supported boundary is limited to formatted messages, direct string structured properties, nested scope strings, and exception representations. Do not add a CLI, analyzer, source generator, sink-specific implementation, or generic secret scanner.
