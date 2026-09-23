# Privacy

KeelMatrix.LogLeak keeps registered sentinel values and inspected logging text in process memory only. It retains safe findings rather than raw events, and disposal clears the probe's retained state.

After a clean verification, the package requests best-effort activation and weekly heartbeat signals through the published `KeelMatrix.Telemetry` 0.1.1 package. Detected leaks and inconclusive captures do not request activation or heartbeat telemetry. The shared contract records the calling package/tool identity, its version, the telemetry schema/version, pseudonymous project and installation identifiers, and coarse runtime/operating-system/CI fields for activation plus the ISO week for heartbeats. LogLeak does not send sentinel values, hashes of sentinel values, messages, templates, structured values, scopes, exception text, categories, event names, property values, test names, or repository identity.

Telemetry failure never changes verification results, and core verification does not require network access. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out for the current process. The shared telemetry package also supports its documented repository-local opt-out files and precedence rules; see the [KeelMatrix.Telemetry privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md).
