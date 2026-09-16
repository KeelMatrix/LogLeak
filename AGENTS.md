# Repository guide

## Layout

- `src/LogLeak.Probe.Core` contains the non-shipping provider-boundary capture and safe matching harness.
- `tests/LogLeak.Probe.PlainLogging` contains ordinary, template, source-generated, state, scope, exception, and redaction fixtures.
- `tests/LogLeak.Probe.AspNetApp` is the minimal ASP.NET Core application used by the `WebApplicationFactory` corpus.
- `tests/LogLeak.Probe.Serilog` routes Microsoft logging through Serilog's provider adapter.
- `tests/LogLeak.Probe.Runner` is the single executable corpus entry point.

## Validation

Restore and build the solution in Release, then run the runner from the repository root. The runner must remain the only full-corpus command. Keep all projects non-packable and keep generated output out of source control.

## Scope

This repository is a feasibility probe. Do not add a shipping package, durable product API, CLI, workflow, or sink-specific implementation here.
