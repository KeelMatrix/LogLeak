# Supported logging fields

KeelMatrix.LogLeak verifies registered sentinel values at the `Microsoft.Extensions.Logging` provider boundary. The supported field set is intentionally limited to four locations:

1. Formatted message output, including message templates and source-generated `LoggerMessage` calls.
2. Direct string values in structured state or properties:
   - `IEnumerable<KeyValuePair<string, object?>>`, excluding the reserved `{OriginalFormat}` metadata entry;
   - `IEnumerable<KeyValuePair<string, string>>`, including `Dictionary<string, string>`, `IReadOnlyDictionary<string, string>`, and other implementations of that shape.
   A state object matching both interfaces is inspected once, not twice.
3. Direct string values in nested logging scopes:
   - plain string scopes;
   - templated scopes such as `logger.BeginScope("Authorization {Token}", value)`;
   - `IEnumerable<KeyValuePair<string, object?>>` scopes, including direct string values in `Dictionary<string, object?>` and `IReadOnlyDictionary<string, object?>` implementations;
   - `IEnumerable<KeyValuePair<string, string>>` scopes, including `Dictionary<string, string>`, `IReadOnlyDictionary<string, string>`, and other implementations of that shape.
4. The string representation of an exception supplied to a logging call.

The `{OriginalFormat}` state entry is reserved framework metadata. It is not classified as a structured property. A sentinel in the rendered message is still classified as a formatted-message finding.

Matching is exact ordinal literal matching. The verifier does not perform case folding, URL decoding, Base64 expansion, hashing, alternate encodings, entropy scanning, or arbitrary object serialization. In the `object?` shape, only values whose runtime value is a direct `string` are inspected. A state or scope dictionary shape whose generic value type is neither `string` nor `object?` (for example, `Dictionary<string, int>`) is excluded entirely; a sentinel held only in such a value can therefore produce a clean result. Opaque state and scope objects are excluded rather than serialized recursively. Dictionary-scope support does not imply support for an unrelated dictionary-state shape.

Findings, verification results, and diagnostic exceptions contain only precomputed safe text and metadata composed while the probe owns the sentinel set. They never retain sentinel values, full messages, exception payloads, or surrounding state after they escape the probe.

The [ASP.NET Core sample](../samples/KeelMatrix.LogLeak.AspNetCore/README.md) shows the same provider boundary in a minimal web application, including the expected clean response and the safe assertion failure shape.

Registration labels are safe identifiers limited to 64 characters using letters, digits, hyphens, underscores, periods, and colons. No registered label may textually contain a registered value, and no registered value may textually contain a registered label. The rule uses exact ordinal comparison (case-sensitive, with no normalization) and is checked at every registration in either order.

Registration freezes when the first event is captured or verification starts. A later `AddSecret` call throws `LogLeakConfigurationException`, so retained findings cannot become stale under a newly registered sentinel.

Only a clean verification requests best-effort activation and heartbeat telemetry. Detected leaks and inconclusive captures do not request telemetry, and telemetry failure never changes the verification result.

If a formatter or exception representation synchronously logs through the same probe, the nested capture is rejected without capture or budget mutation. The probe records a sticky safe reentrancy condition, so every later verification is `Inconclusive`, never `Clean`.

## Scope boundary

Register the capture provider before opening scopes that the probe should observe. Scopes opened before provider registration are outside the declared observed boundary and may result in a clean verification even when they contain a registered sentinel. This is a limitation of the provider-registration boundary, not proof that the earlier scope was safe.
