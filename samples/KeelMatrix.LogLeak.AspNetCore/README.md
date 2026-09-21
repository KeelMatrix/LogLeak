# ASP.NET Core sample

This sample demonstrates the smallest ASP.NET Core integration: a `LogLeakProbe` registers a synthetic request token, the probe provider is added to the application's logging builder, and the `/health` endpoint verifies the captured Microsoft logging boundary after writing a safe event.

## Run against the built package

From the repository root, build the package and create the isolated local feed:

```powershell
pwsh ./build/Invoke-PackageGate.ps1 -Stage All
```

Restore and build the sample using that package feed:

```powershell
$feed = (Resolve-Path ./artifacts/gate/packages).Path
dotnet restore ./samples/KeelMatrix.LogLeak.AspNetCore/KeelMatrix.LogLeak.AspNetCore.csproj --source $feed --source https://api.nuget.org/v3/index.json --force
dotnet build ./samples/KeelMatrix.LogLeak.AspNetCore/KeelMatrix.LogLeak.AspNetCore.csproj -c Release --no-restore
```

Run the web app on a fixed local address:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet run --project ./samples/KeelMatrix.LogLeak.AspNetCore/KeelMatrix.LogLeak.AspNetCore.csproj -c Release --no-build --no-restore --urls http://127.0.0.1:5287
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
