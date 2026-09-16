# KeelMatrix.LogLeak

This repository contains a bounded feasibility probe for the proposed LogLeak test-time logging verifier. It is not a shipping package and does not define a public API.

The probe observes the `Microsoft.Extensions.Logging` provider boundary, tests exact literal matching across a deliberately small supported-field set, exercises common application paths, and records conservative overflow behavior. Its output is evidence for deciding whether a future product implementation is technically and safely feasible.

Run the complete corpus from the repository root:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-restore
```

All projects are non-packable. No package or durable product API is produced at this stage.

## Probe resource contract

Every inspected text unit is limited to 4,096 UTF-16 characters: formatted output, each direct string structured value, each direct string scope value, and the exception representation. This is approximately 8 KiB of UTF-16 character data per unit: large enough for ordinary test logging while rejecting pathological multi-megabyte payloads. The probe does not truncate oversized text; it stops verification with an explicit inconclusive result, never a clean event.

For each event, the probe inspects at most 1,024 formatted/text units, structured-entry or scope candidates; records at most 256 findings; and applies a 1,048,576-byte transient-allocation guard measured on the provider thread. The configured event and total-finding capture limits also remain in force. Countable structured states and scope collections are checked before enumeration. Reaching any aggregate budget stops inspection and makes verification explicitly inconclusive. Captured raw logging text is not retained.

The benchmark policy, ten-sample raw set, median, worst values, and host caveat are committed in [BENCHMARK-POLICY.md](BENCHMARK-POLICY.md). The runner prints the policy, sample basis, derived threshold, measured value, margin, and pass/fail result. The benchmark gate is a probe-only resource contract for the named Windows x64 host, not a cross-platform performance guarantee.

Diagnostic evidence in this feasibility probe covers probe-owned representations and simulated output paths. Real xUnit/NUnit/MSTest adapter output and the shared `KeelMatrix.Telemetry` contract remain deferred evidence for a later implementation phase.
