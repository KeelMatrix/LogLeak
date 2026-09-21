# Changelog

All notable changes to this project are documented here.

## [Unreleased]

### Added

- Bounded `KeelMatrix.LogLeak` provider-boundary verification for registered test-only sentinel values.
- Safe findings for formatted messages, direct string structured properties, nested scope strings, and exception representations.
- Direct string values in both supported generic dictionary-scope shapes, with excluded dictionary value types documented as outside the verification boundary.
- Deterministic capture, payload, inspection-unit, and finding limits shared by the `net8.0` and `netstandard2.0` package assets.
- Sentinel-safe completed diagnostics, including composed metadata and limit explanations.
- Explicit inconclusive outcomes for capture and resource-bound overflow.
