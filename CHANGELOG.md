# Changelog

All notable changes to this project are documented here.

## [Unreleased]

## [0.1.0] - 2026-09-23

### Added

- Bounded, exact-literal verification of registered test-only sentinel values at the `Microsoft.Extensions.Logging` provider boundary, covering formatted messages, source-generated `LoggerMessage` calls, direct string structured properties, nested scopes, and exception representations.
- Safe leak findings that identify registration labels, broad leak locations, and available logging metadata without retaining or emitting sentinel values, complete messages, exception payloads, or surrounding state.
- Explicit fail-closed `Inconclusive` outcomes for capture, payload, inspection-unit, finding, event, callback, and same-probe reentrancy limits across the `net8.0` and `netstandard2.0` package assets.
- Test-framework-neutral integration with ordinary `ILogger` and ASP.NET Core setups, with best-effort telemetry after clean verification and no network requirement for core verification.
