# Probe benchmark policy

The performance gate is a probe-only resource contract. It uses an environment-relative reference workload; it is not a cross-platform performance guarantee.

## Fixed rule

The policy is fixed before the measured gate run:

```text
threshold = ceiling(worst normalized sample × 1.25)
```

<!-- benchmark-policy: rule-id=worst-normalized-sample-plus-headroom-v3; headroom-percent=25; normalized-rounding=0.01 -->

Normalized thresholds round upward to the next 0.01 ratio unit. The 25% headroom is applied to the worst normalized value, not to a single favorable observation and not to the current run. A threshold or margin change requires a new committed sample set and policy change together.

The 25% headroom is a fixed design allowance applied after reference normalization: it is not recalibrated from the current run, and it leaves a closed margin for a meaningful normalized regression.

The margin may not be widened in response to a failing measurement. A host change or implementation change requires a new sample campaign; the fixed rule is then applied to that committed campaign before measuring the gate.

## Recorded sample set

These ten samples were recorded on Windows x64, target `net8.0`, .NET 8.0.31, Microsoft Windows 10.0.19045, process architecture X64, using the 100,000-event benchmark and the deterministic pure-CPU reference workload. Raw values and reference measurements are executable data in `tests/LogLeak.Probe.Runner/BenchmarkSamples.json`; the runner derives normalized summaries and thresholds from those values.

| Sample | Reference (ms) | Emit (ms) | Matching (ms) | Sampled heap delta (bytes) |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 27.12 | 82.75 | 46.15 | 8,310,736 |
| 2 | 26.67 | 74.15 | 42.40 | 8,310,736 |
| 3 | 26.91 | 89.19 | 49.12 | 8,314,352 |
| 4 | 27.18 | 72.42 | 31.88 | 8,314,352 |
| 5 | 27.76 | 37.34 | 14.27 | 8,314,352 |
| 6 | 29.07 | 105.33 | 58.35 | 8,310,736 |
| 7 | 28.01 | 115.23 | 62.61 | 8,310,736 |
| 8 | 34.99 | 213.87 | 102.96 | 8,314,352 |
| 9 | 29.65 | 39.11 | 14.62 | 8,314,352 |
| 10 | 31.88 | 43.39 | 15.75 | 8,314,352 |

Derived statistics:

- Median raw: reference `27.89 ms`, emit `78.45 ms`, matching `44.27 ms`, sampled heap delta `8,314,352 bytes`.
- Median normalized: emit `2.92`, matching `1.65`, sampled heap delta `298107.2` ratio units.
- Worst raw: reference `34.99 ms`, emit `213.87 ms`, matching `102.96 ms`, sampled heap delta `8,314,352 bytes`.
- Worst normalized: emit `6.11`, matching `2.94`, sampled heap delta `311613.65` ratio units.
- Derived normalized thresholds: emit `7.65`, matching `3.68`, sampled heap delta `389517.07` ratio units.

Reproduce the committed sample-set arithmetic with:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-build --no-restore --performance-samples
```

The committed sample set is collected by running the same Release runner twice with `--performance`; each run records five same-process attempts, and the ten raw attempt rows are copied into `BenchmarkSamples.json` before the policy summaries and thresholds are regenerated. The live `--performance` gate measures a deterministic CPU reference workload and the product workload in the same process on every attempt, takes an odd number of at least five attempts, and normalizes each product metric by that attempt's reference measurement before comparing the median normalized ratio with these derived thresholds. Raw absolute measurements and every per-attempt normalized ratio remain visible as evidence; the closed normalized criterion reduces host-contention sensitivity without making the gate advisory. A product/reference slowdown that affects both identically can normalize away, and only sustained regressions that move the median are detected. A different implementation, target, runtime, or process architecture is a different baseline: rerun and commit a replacement sample set under this same policy before changing a threshold. This gate does not prove behavior for other platforms.

This normalized criterion does not detect a change that slows the product workload and the reference workload identically, because that change can divide away. It also does not treat one fast observation as representative: only sustained regressions that move the median can fail the gate. The median is therefore the pass/fail statistic, while the worst committed normalized sample plus fixed headroom remains the threshold derivation rule.

## Provenance

Earlier pre-release revisions of this probe set wrote threshold values directly (margin 75%, then 125%). Those hand-set values are superseded: thresholds are now derived from the committed sample set by the fixed rule, and a threshold or margin change requires a committed sample-set or policy change with the recompute check passing.

The live gate consumes one computed statistics object derived from the committed sample set. The provenance check explicitly compares its normalized emit, matching, and sampled-memory thresholds with that same live-consumed object; any divergence names the affected threshold and fails. `--performance` runs this binding check before measuring the gate, so it fails closed before it can report a pass with decoupled thresholds.

Run the deterministic provenance check with:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-build --no-restore --verify-benchmark-provenance
```
