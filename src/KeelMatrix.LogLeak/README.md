# KeelMatrix.LogLeak

Redaction code is not proof that sensitive values stayed out of logs. LogLeak lets a test register synthetic sensitive values, exercise the real Microsoft logging path, and fail safely if any registered value survives into supported log fields.

## Install

```bash
dotnet add package KeelMatrix.LogLeak
```

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

When a registered value is found, `LogLeakAssertionException` reports its label, broad location, and safe logging metadata. The value, complete message, exception payload, and surrounding state are never retained in a finding or assertion message.

## Supported boundary fields

- formatted message output, including message templates and source-generated `LoggerMessage` calls;
- direct string values in structured state, excluding the reserved `{OriginalFormat}` metadata entry;
- direct string values in nested logging scopes;
- the string representation of a logging exception.

Matching is exact ordinal literal matching. The package does not decode, normalize, hash, encode, or infer secrets.

## Bounds and privacy

Capture is bounded. The defaults inspect at most 4,096 UTF-16 characters per text unit, 1,024 units per event, 256 findings per event, 4,096 findings overall, and 4,096 events. The transient per-event allocation guard is 1 MiB. Any limit breach produces an explicit `Inconclusive` result and never a clean result.

The package uses `KeelMatrix.Telemetry` only for best-effort activation and weekly heartbeat signals after conclusive verification. No sentinel, log content, exception text, category, event name, or property value is sent by LogLeak. Core verification requires no network. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.

Use synthetic test-only values. This package verifies registered sentinels at the captured Microsoft logging boundary; it does not inspect arbitrary sinks or discover unregistered sensitive data.

## Documentation

- [Supported fields](https://github.com/KeelMatrix/LogLeak/blob/main/docs/supported-fields.md)
- [Privacy](https://github.com/KeelMatrix/LogLeak/blob/main/PRIVACY.md)
- [Security](https://github.com/KeelMatrix/LogLeak/blob/main/SECURITY.md)
