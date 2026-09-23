[CmdletBinding()]
param(
    [string] $RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
else {
    $RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
}

$paths = @(
    '.vs/LogLeak.Probe.sln.v17/.suo'
    'tests/LogLeak.Probe.AspNetApp/Properties/LogLeak.Probe.AspNetApp.user'
    'tests/LogLeak.Probe.AspNetApp/Properties/LogLeak.Probe.AspNetApp.suo'
    'tests/LogLeak.Probe.AspNetApp/Properties/LogLeak.Probe.AspNetApp.userosscache'
    'tests/LogLeak.Probe.AspNetApp/Properties/launchSettings.json'
    'LogLeak.Probe.sln.docstates'
)

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($path in $paths) {
    & git -C $RepositoryRoot check-ignore --no-index --quiet -- $path
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $failures.Add("'$path' was not ignored (git check-ignore exit code $exitCode).")
    }
}
$stopwatch.Stop()

if ($failures.Count -gt 0) {
    throw "Visual Studio developer-local path guard failed:`n - $($failures -join "`n - ")"
}

Write-Host "Visual Studio developer-local path guard passed for $($paths.Count) paths; duration $($stopwatch.Elapsed.ToString('hh\:mm\:ss\.fff'))."
