# Supported logging fields

KeelMatrix.LogLeak verifies registered sentinel values at the `Microsoft.Extensions.Logging` provider boundary. The supported field set is intentionally limited to four locations:

1. Formatted message output, including message templates and source-generated `LoggerMessage` calls.
2. Direct string values in structured state or properties.
3. Direct string values in nested logging scopes.
4. The string representation of an exception supplied to a logging call.

The `{OriginalFormat}` state entry is reserved framework metadata. It is not classified as a structured property. A sentinel in the rendered message is still classified as a formatted-message finding.

Matching is exact ordinal literal matching. The verifier does not perform case folding, URL decoding, Base64 expansion, hashing, alternate encodings, entropy scanning, or arbitrary object serialization. Values of non-string structured properties are not recursively inspected.

Findings contain only the safe registration label, broad location, and metadata that does not contain a registered sentinel. They never contain sentinel values, full messages, exception payloads, or surrounding state.

The [ASP.NET Core sample](../samples/KeelMatrix.LogLeak.AspNetCore/README.md) shows the same provider boundary in a minimal web application, including the expected clean response and the safe assertion failure shape.

Registration labels are safe identifiers limited to 64 characters using letters, digits, hyphens, underscores, periods, and colons. No registered label may textually contain a registered value, and no registered value may textually contain a registered label. The rule uses exact ordinal comparison (case-sensitive, with no normalization) and is checked at every registration in either order.

## Scope boundary

Register the capture provider before opening scopes that the probe should observe. Scopes opened before provider registration are outside the declared observed boundary and may result in a clean verification even when they contain a registered sentinel. This is a limitation of the provider-registration boundary, not proof that the earlier scope was safe.
