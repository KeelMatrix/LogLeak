# KeelMatrix.LogLeak

Redaction code is not proof that sensitive values stayed out of logs. LogLeak lets a test register synthetic sensitive values, exercise the real Microsoft logging path, and fail safely if any registered value survives into supported log fields.

## Install

```bash
dotnet add package KeelMatrix.LogLeak
dotnet add package Microsoft.Extensions.Logging
```

The second package supplies the `AddLogging` registration API used by the quick start.

## Quick start

```csharp
using KeelMatrix.LogLeak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using var probe = new LogLeakProbe()
    .AddSecret("authorization", "synthetic-test-value");

using var services = new ServiceCollection()
    .AddLogging(builder => builder.AddProvider(probe.Provider))
    .BuildServiceProvider();

var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PaymentClient");
logger.LogInformation("request completed");

probe.AssertNoLeaks();
```

When a registered value is found, `LogLeakAssertionException` reports its label, broad location, and safe logging metadata. The value, complete message, exception payload, surrounding state, and registered-sentinel snapshot are never retained in a finding, result, or diagnostic exception; all outward-facing diagnostic text is composed while the probe owns the sentinel set.

Registration labels are safe identifiers limited to 64 characters using letters, digits, `-`, `_`, `.`, and `:`. No registered label may textually contain a registered value, and no registered value may textually contain a registered label. The rule uses exact ordinal comparison (case-sensitive, with no normalization) and is checked at every registration in either order.

Registration freezes when the first event is captured or verification starts. A later `AddSecret` call throws `LogLeakConfigurationException`; this prevents retained findings from being reinterpreted under a changed sentinel set.

## Supported boundary fields

- formatted message output, including message templates and source-generated `LoggerMessage` calls;
- the string representation of a logging exception.

Structured state and scopes are separate supported surfaces:

- structured state inspects direct string values in `IEnumerable<KeyValuePair<string, object?>>` and `IEnumerable<KeyValuePair<string, string>>`, excluding reserved `{OriginalFormat}` metadata; a state object matching both interfaces is inspected once;
- scopes inspect plain strings, templated direct string values, and direct string values in the same two enumerable shapes, including `Dictionary<string, string>` and `IReadOnlyDictionary<string, string>`.

Matching is exact ordinal literal matching. Non-string state or scope values and opaque objects are excluded; the package does not recursively serialize objects, decode, normalize, hash, encode, or infer secrets. A sentinel held only in an excluded shape such as `Dictionary<string, int>` can therefore produce a clean result. Dictionary-scope support does not imply dictionary-state support outside the two documented enumerable shapes.

## Bounds and privacy

The deterministic resource contract is identical on `net8.0` and `netstandard2.0`: by default, at most 128 sentinels may be registered, each sentinel is at most 4,096 UTF-16 characters, each inspected text unit is at most 4,096 UTF-16 characters, each event may inspect 1,024 units and retain 256 findings, the probe may retain 4,096 findings overall, and 4,096 events may complete inspection. LogLeak does not use a process-wide heap delta or transient-allocation measurement. Any limit breach produces an explicit `Inconclusive` result and never a clean result. Same-probe reentrant capture is rejected without capture and makes later verification sticky `Inconclusive` with a safe named reason.

The package uses `KeelMatrix.Telemetry` only for best-effort activation and weekly heartbeat signals after a clean verification. Detected leaks and inconclusive captures do not request activation or heartbeat telemetry. No sentinel, log content, exception text, category, event name, or property value is sent by LogLeak. Core verification requires no network. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.

Use synthetic test-only values. This package verifies registered sentinels at the captured Microsoft logging boundary; it does not inspect arbitrary sinks or discover unregistered sensitive data.

## Scope boundary

Register `probe.Provider` before opening any scopes that the probe should observe. Scopes opened before provider registration are outside the declared observed boundary and may result in a clean verification even when they contain a registered sentinel. Within the observed boundary, structured state and scopes are classified separately: state supports direct strings in the two documented enumerable shapes, while scopes additionally support plain and templated strings. Opaque objects and non-string values are excluded. Dictionary-scope support does not imply dictionary-state support for other shapes, and a sentinel held only in an excluded value such as `Dictionary<string, int>` can therefore produce a clean result.

## Documentation

- [Supported fields](https://github.com/KeelMatrix/LogLeak/blob/main/docs/supported-fields.md)
- [Privacy](https://github.com/KeelMatrix/LogLeak/blob/main/PRIVACY.md)
- [Security](https://github.com/KeelMatrix/LogLeak/blob/main/SECURITY.md)
