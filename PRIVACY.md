# Privacy

KeelMatrix.LogLeak keeps registered sentinel values and inspected logging text in process memory only. It retains safe findings rather than raw events, and disposal clears the probe's retained state.

After a conclusive verification, the package requests best-effort activation and weekly heartbeat signals through `KeelMatrix.Telemetry`. Those signals use the shared telemetry contract's coarse package version, runtime, operating-system, CI classification, and usage cadence fields. LogLeak does not send sentinel values, hashes of sentinel values, messages, templates, structured values, scopes, exception text, categories, event names, property values, test names, or repository identity.

Telemetry failure never changes verification results, and core verification does not require network access. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.
