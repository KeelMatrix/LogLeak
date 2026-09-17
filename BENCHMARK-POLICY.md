# Probe benchmark policy

The performance gate is a probe-only resource contract. It describes the Windows x64 baseline named below; it is not a cross-platform performance guarantee.

## Fixed rule

The policy is fixed before the measured gate run:

```text
threshold = ceiling(worst recorded sample × 1.25)
```

<!-- benchmark-policy: rule-id=worst-recorded-sample-plus-headroom-v1; headroom-percent=25; time-rounding-ms=1; memory-rounding-bytes=100000 -->

Time thresholds round upward to the next 1 ms. Sampled heap-delta thresholds round upward to the next 100,000 bytes. The 25% headroom is applied to the worst value, not to a single favorable observation and not to the current run. A threshold or margin change requires a new committed sample set and policy change together.

The 25% headroom is a fixed design allowance: the observed spread across the ten samples is below 10% in every dimension, so it absorbs ordinary host scheduling noise while still failing a meaningful regression.

The margin may not be widened in response to a failing measurement. A host change or implementation change requires a new sample campaign; the fixed rule is then applied to that committed campaign before measuring the gate.

## Recorded sample set

These ten samples were recorded on Windows x64, target `net8.0`, .NET 8.0.31, Microsoft Windows 10.0.19045, process architecture X64, using the 100,000-event benchmark after the aggregate inspection guards were implemented. Raw values are executable data in `tests/LogLeak.Probe.Runner/BenchmarkSamples.json`; the runner derives the summaries and thresholds from those values.

| Sample | Emit (ms) | Matching (ms) | Sampled heap delta (bytes) |
| ---: | ---: | ---: | ---: |
| 1 | 87.53 | 44.08 | 8,310,816 |
| 2 | 84.91 | 44.22 | 8,310,816 |
| 3 | 83.14 | 41.66 | 8,310,816 |
| 4 | 83.94 | 41.66 | 8,318,904 |
| 5 | 82.41 | 41.78 | 8,310,760 |
| 6 | 84.10 | 42.49 | 8,310,760 |
| 7 | 82.03 | 41.72 | 8,310,760 |
| 8 | 84.43 | 42.67 | 8,310,760 |
| 9 | 86.88 | 43.35 | 8,310,816 |
| 10 | 80.88 | 40.38 | 8,310,760 |

Derived statistics:

- Median: emit `84.02 ms`, matching `42.14 ms`, sampled heap delta `8,310,788 bytes`.
- Worst: emit `87.53 ms`, matching `44.22 ms`, sampled heap delta `8,318,904 bytes`.
- Derived thresholds: emit `110 ms`, matching `56 ms`, sampled heap delta `10,400,000 bytes`.

Reproduce the committed sample-set arithmetic with:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-build --no-restore --performance-samples
```

The live `--performance` gate measures the current run at least five times in one process and takes the minimum emit, matching, and sampled-memory measurement for each dimension before comparing those minima with these derived thresholds. The minimum is a contention-resistant estimator: a transient scheduler interruption affects only an outlier attempt, while a real regression raises the minimum across attempts. A different host, target, runtime, or process architecture is a different baseline: rerun and commit a replacement sample set under this same policy before changing a threshold. This gate does not prove behavior for other platforms.

## Provenance

Earlier pre-release revisions of this probe set wrote threshold values directly (margin 75%, then 125%). Those hand-set values are superseded: thresholds are now derived from the committed sample set by the fixed rule, and a threshold or margin change requires a committed sample-set or policy change with the recompute check passing.

The live gate consumes one computed statistics object derived from the committed sample set. The provenance check explicitly compares its emit, matching, and sampled-memory thresholds with that same live-consumed object; any divergence names the affected threshold and fails. `--performance` runs this binding check before measuring the gate, so it fails closed before it can report a pass with decoupled thresholds.

Run the deterministic provenance check with:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-build --no-restore --verify-benchmark-provenance
```
