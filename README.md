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

services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("PaymentClient")
    .LogInformation("request completed");

probe.AssertNoLeaks();
```

Use synthetic values created only for tests. A planted value produces a safe `LogLeakAssertionException` containing the registration label, broad leak location, and safe logging metadata. The value, complete message, exception payload, and surrounding state are never included.

Registration labels are safe identifiers limited to 64 characters using letters, digits, `-`, `_`, `.`, and `:`. No registered label may textually contain a registered value, and no registered value may textually contain a registered label. The rule uses exact ordinal comparison (case-sensitive, with no normalization) and is checked at every registration in either order.

Registration freezes when the first event is captured or verification starts. A later `AddSecret` call throws `LogLeakConfigurationException`; this prevents retained findings from being reinterpreted under a changed sentinel set.

## Documentation

- [Supported fields](docs/supported-fields.md)
- [ASP.NET Core sample](samples/KeelMatrix.LogLeak.AspNetCore/README.md)
- [Privacy](PRIVACY.md)
- [Security](SECURITY.md)
- [Development guide](docs/DEV.md)
- [Release process](docs/RELEASE.md)

## Supported fields

The supported boundary is intentionally frozen to:

- formatted message output, including message templates and source-generated `LoggerMessage` calls;
- direct string values in structured state, excluding reserved `{OriginalFormat}` metadata;
- plain string nested scopes;
- direct string values in templated scopes such as `logger.BeginScope("Authorization {Token}", value)`;
- direct string values in dictionary or `IReadOnlyDictionary<string, object?>` scopes;
- the string representation of a logging exception.

Matching is exact ordinal literal matching. Non-string scope values and opaque scope objects are excluded; LogLeak does not recursively serialize objects, decode, normalize, hash, encode, or infer unregistered secrets. It does not inspect arbitrary sinks.

## Bounds and privacy

Capture defaults are bounded to 4,096 UTF-16 characters per text unit, 1,024 inspection units per event, 256 findings per event, 4,096 findings overall, and 4,096 captured events. The transient per-event allocation guard is 1 MiB. A bound breach returns `Inconclusive` and never `Clean`.

Only a clean verification requests best-effort activation and weekly heartbeat signals through `KeelMatrix.Telemetry`. Detected leaks and inconclusive captures do not request activation or heartbeat telemetry. LogLeak sends no sentinel, log content, exception text, category, event name, or property value. Core verification requires no network. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.

## Limitations

LogLeak verifies only the captured `Microsoft.Extensions.Logging` provider boundary. It does not inspect arbitrary downstream sinks or discover unregistered sensitive data. The package targets `net8.0` and `netstandard2.0`.

## Scope boundary

Register `probe.Provider` before opening any scopes that the probe should observe. Scopes opened before provider registration are outside the declared observed boundary and may result in a clean verification even when they contain a registered sentinel. Within the observed boundary, only the supported shallow scope forms above are inspected.

## Troubleshooting

An `Inconclusive` result means a configured capture or resource limit was reached. Increase the relevant `LogLeakOptions` limit only when the test can safely handle the additional bounded work; an inconclusive result is never treated as clean.

## License

MIT

## Development

The Phase 0 corpus runs against the shipping `KeelMatrix.LogLeak` implementation through `tests/LogLeak.Probe.Runner`:

```text
dotnet run --project tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj -c Release --no-restore
```

The shipping package is under `src/KeelMatrix.LogLeak`. Focused contract tests are under `tests/KeelMatrix.LogLeak.Tests`; the corpus and package gate are the release-equivalent integration checks.
