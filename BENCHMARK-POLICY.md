# Probe benchmark policy

The performance gate is a probe-only resource contract. It uses an environment-relative reference workload; it is not a cross-platform performance guarantee.

## Fixed rule

The policy is fixed before the measured gate run:

```text
threshold = ceiling(worst normalized sample × 1.25)
```

<!-- benchmark-policy: rule-id=worst-normalized-sample-plus-headroom-v2; headroom-percent=25; normalized-rounding=0.01 -->

Normalized thresholds round upward to the next 0.01 ratio unit. The 25% headroom is applied to the worst normalized value, not to a single favorable observation and not to the current run. A threshold or margin change requires a new committed sample set and policy change together.

The 25% headroom is a fixed design allowance applied after reference normalization: it is not recalibrated from the current run, and it leaves a closed margin for a meaningful normalized regression.

The margin may not be widened in response to a failing measurement. A host change or implementation change requires a new sample campaign; the fixed rule is then applied to that committed campaign before measuring the gate.

## Recorded sample set

These ten samples were recorded on Windows x64, target `net8.0`, .NET 8.0.31, Microsoft Windows 10.0.19045, process architecture X64, using the 100,000-event benchmark and the deterministic pure-CPU reference workload. Raw values and reference measurements are executable data in `tests/LogLeak.Probe.Runner/BenchmarkSamples.json`; the runner derives normalized summaries and thresholds from those values.

| Sample | Reference (ms) | Emit (ms) | Matching (ms) | Sampled heap delta (bytes) |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 27.74 | 78.19 | 45.26 | 8,318,896 |
| 2 | 27.31 | 68.16 | 40.44 | 8,310,736 |
| 3 | 25.86 | 80.85 | 46.24 | 8,314,352 |
| 4 | 26.53 | 86.49 | 42.38 | 8,314,352 |
| 5 | 26.46 | 23.34 | 9.59 | 8,314,352 |
| 6 | 26.11 | 79.27 | 44.91 | 8,318,896 |
| 7 | 25.92 | 70.87 | 41.33 | 8,310,736 |
| 8 | 25.86 | 69.46 | 40.40 | 8,314,352 |
| 9 | 25.81 | 90.54 | 50.04 | 8,314,352 |
| 10 | 25.79 | 82.99 | 45.53 | 8,314,352 |

Derived statistics:

- Median raw: reference `26.02 ms`, emit `78.73 ms`, matching `43.64 ms`, sampled heap delta `8,314,352 bytes`.
- Median normalized: emit `2.93`, matching `1.61`, sampled heap delta `319619.91` ratio units.
- Worst raw: reference `27.74 ms`, emit `90.54 ms`, matching `50.04 ms`, sampled heap delta `8,318,896 bytes`.
- Worst normalized: emit `3.51`, matching `1.94`, sampled heap delta `322386.66` ratio units.
- Derived normalized thresholds: emit `4.39`, matching `2.43`, sampled heap delta `402983.33` ratio units.

Reproduce the committed sample-set arithmetic with:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-build --no-restore --performance-samples
```

The live `--performance` gate measures a deterministic CPU reference workload and the product workload in the same process on every attempt, takes at least five attempts, and normalizes each product metric by that attempt's reference measurement before comparing the minimum normalized ratios with these derived thresholds. Raw absolute measurements remain visible as evidence; the closed normalized criterion reduces host-contention sensitivity without making the gate advisory. A different implementation, target, runtime, or process architecture is a different baseline: rerun and commit a replacement sample set under this same policy before changing a threshold. This gate does not prove behavior for other platforms.

## Provenance

Earlier pre-release revisions of this probe set wrote threshold values directly (margin 75%, then 125%). Those hand-set values are superseded: thresholds are now derived from the committed sample set by the fixed rule, and a threshold or margin change requires a committed sample-set or policy change with the recompute check passing.

The live gate consumes one computed statistics object derived from the committed sample set. The provenance check explicitly compares its normalized emit, matching, and sampled-memory thresholds with that same live-consumed object; any divergence names the affected threshold and fails. `--performance` runs this binding check before measuring the gate, so it fails closed before it can report a pass with decoupled thresholds.

Run the deterministic provenance check with:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-build --no-restore --verify-benchmark-provenance
```
