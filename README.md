# KeelMatrix.LogLeak

Catch registered test-only sentinel values at the `Microsoft.Extensions.Logging` provider boundary without exposing them in diagnostics.

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

services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("PaymentClient")
    .LogInformation("request completed");

probe.AssertNoLeaks();
```

Use synthetic values created only for tests. A planted value produces a safe `LogLeakAssertionException` containing the registration label, broad leak location, and safe logging metadata. The value, complete message, exception payload, and surrounding state are never included.

## Supported fields

The supported boundary is intentionally frozen to:

- formatted message output, including message templates and source-generated `LoggerMessage` calls;
- direct string values in structured state, excluding reserved `{OriginalFormat}` metadata;
- direct string values in nested logging scopes;
- the string representation of a logging exception.

Matching is exact ordinal literal matching. LogLeak does not decode, normalize, hash, encode, recursively serialize objects, or infer unregistered secrets. It does not inspect arbitrary sinks.

## Bounds and privacy

Capture defaults are bounded to 4,096 UTF-16 characters per text unit, 1,024 inspection units per event, 256 findings per event, 4,096 findings overall, and 4,096 captured events. The transient per-event allocation guard is 1 MiB. A bound breach returns `Inconclusive` and never `Clean`.

Conclusive verification requests best-effort activation and weekly heartbeat signals through `KeelMatrix.Telemetry`. LogLeak sends no sentinel, log content, exception text, category, event name, or property value. Core verification requires no network. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.

## Development

The feasibility corpus remains under `src/LogLeak.Probe.Core` and `tests/LogLeak.Probe.*`:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-restore
```

The shipping package is under `src/KeelMatrix.LogLeak`. Focused contract tests are under `tests/KeelMatrix.LogLeak.Tests`.
