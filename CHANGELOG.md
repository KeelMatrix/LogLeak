# Changelog

All notable changes to this project are documented here.

## [Unreleased]

### Added

- Bounded `KeelMatrix.LogLeak` provider-boundary verification for registered test-only sentinel values.
- Safe findings for formatted messages, direct string structured properties, nested scope strings, and exception representations.
- Direct string values in both supported generic dictionary-state and dictionary-scope shapes, with state and scope contracts documented separately and excluded dictionary value types documented as outside the verification boundary.
- Deterministic capture, payload, inspection-unit, and finding limits shared by the `net8.0` and `netstandard2.0` package assets.
- Sentinel-safe completed diagnostics, including composed metadata and limit explanations.
- Explicit inconclusive outcomes for capture and resource-bound overflow.
- Same-probe reentrant provider-boundary capture is rejected without budget mutation and makes later verification inconclusive.
- Escaped findings, results, and diagnostic exceptions retain only precomputed safe text and metadata.
- Scope handles and provider-boundary argument failures remain sentinel-safe, including their `ToString()` representations.
- Verification attempted during an active formatter, exception, state, or scope callback is explicitly inconclusive without success telemetry.
- Consumer documentation and package smoke cover source-generated `LoggerMessage` usage.
