# ASP.NET Core sample

This sample demonstrates the smallest ASP.NET Core integration: a `LogLeakProbe` registers a synthetic request token, the probe provider is added to the application's logging builder, and the `/health` endpoint verifies the captured Microsoft logging boundary after writing a safe event.

## Run against the built package

From the repository root, build the package, restore and build this sample against it through a fresh source-mapped cache, and call `/health`:

```powershell
pwsh ./build/Invoke-PackageGate.ps1 -Stage All
```

The package gate proves the isolated restore selected the just-built local package rather than a cached or published copy, then starts the built artifact and expects `{"status":"LogLeak clean"}` from `/health`. To run that exact built artifact manually after the gate:

```powershell
$sampleDll = (Resolve-Path ./artifacts/gate/consumer/aspnet-consumer/bin/Release/net8.0/KeelMatrix.LogLeak.AspNetCore.dll).Path
```

Run the web app on a fixed local address:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet $sampleDll --urls http://127.0.0.1:5287
```

In a second terminal, call the endpoint:

```powershell
(Invoke-WebRequest http://127.0.0.1:5287/health).Content
```

Expected response:

```text
{"status":"LogLeak clean"}
```

The application log contains `health check completed`, `AssertNoLeaks()` returns clean, and the request completes with HTTP 200. Stop the server with `Ctrl+C`.

## Leak/fail shape

For a disposable local failure demonstration, change the endpoint's log line in `Program.cs` to:

```csharp
logger.LogInformation("health check token {Token}", "synthetic-aspnet-token-4c31");
```

The same request then fails with a non-success response and the server reports a `LogLeakAssertionException` identifying the safe label `request-token` and the broad location `FormattedMessage`. The diagnostic does not include the synthetic token, the complete message, or exception payload. Restore the safe log line after the demonstration.

The sample intentionally verifies only the captured `Microsoft.Extensions.Logging` provider boundary; it does not inspect arbitrary downstream sinks or discover unregistered secrets. Structured state and scopes are separate surfaces: state inspects direct string values in `IEnumerable<KeyValuePair<string, object?>>` and `IEnumerable<KeyValuePair<string, string>>`, while scopes additionally support plain strings and templated direct string values. Other dictionary value types are outside the boundary and may therefore produce a clean result when a sentinel is held only in an excluded value.
