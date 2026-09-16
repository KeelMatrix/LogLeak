# KeelMatrix.LogLeak

This repository contains a bounded feasibility probe for the proposed LogLeak test-time logging verifier. It is not a shipping package and does not define a public API.

The probe observes the `Microsoft.Extensions.Logging` provider boundary, tests exact literal matching across a deliberately small supported-field set, exercises common application paths, and records conservative overflow behavior. Its output is evidence for deciding whether a future product implementation is technically and safely feasible.

Run the complete corpus from the repository root:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-restore
```

All projects are non-packable. No package or durable product API is produced at this stage.
