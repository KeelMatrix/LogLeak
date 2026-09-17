[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $Solution = 'LogLeak.Probe.sln',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
$solutionPath = (Resolve-Path -LiteralPath (Join-Path $root $Solution) -ErrorAction Stop).Path
$configPath = (Resolve-Path -LiteralPath (Join-Path $root 'NuGet.config') -ErrorAction Stop).Path
$shippingProject = (Resolve-Path -LiteralPath (Join-Path $root 'src/KeelMatrix.LogLeak/KeelMatrix.LogLeak.csproj') -ErrorAction Stop).Path
$focusedTests = (Resolve-Path -LiteralPath (Join-Path $root 'tests/KeelMatrix.LogLeak.Tests/KeelMatrix.LogLeak.Tests.csproj') -ErrorAction Stop).Path
$probeProject = (Resolve-Path -LiteralPath (Join-Path $root 'tests/LogLeak.Probe.Runner/LogLeak.Probe.Runner.csproj') -ErrorAction Stop).Path

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $displayArguments = $Arguments | ForEach-Object {
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }
    Write-Host ('dotnet ' + ($displayArguments -join ' '))
    & dotnet @Arguments
    $exitCode = $LASTEXITCODE
    Write-Host "exit code $exitCode"
    if ($exitCode -ne 0) {
        throw "dotnet command failed with exit code $exitCode."
    }
}

$versionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $versionArguments = @("-p:Version=$Version", "-p:PackageVersion=$Version")
}

Invoke-DotNet @('restore', $solutionPath, '--configfile', $configPath, '--no-cache', '--force')
Invoke-DotNet (@('build', $solutionPath, '--configuration', $Configuration, '--no-restore') + $versionArguments)
Invoke-DotNet @('test', $focusedTests, '--configuration', $Configuration, '--no-build', '--no-restore')
Invoke-DotNet @('run', '--project', $probeProject, '--configuration', $Configuration, '--no-build', '--no-restore')
Invoke-DotNet @('format', 'whitespace', $solutionPath, '--verify-no-changes', '--no-restore')
Invoke-DotNet @('format', 'analyzers', $shippingProject, '--verify-no-changes', '--no-restore', '--severity', 'error')

$auditScript = Join-Path $root 'build/Invoke-DependencyAudit.ps1'
& $auditScript -RepositoryRoot $root -Solution $Solution
if ($LASTEXITCODE -ne 0) {
    throw "The vulnerability audit failed with exit code $LASTEXITCODE."
}

Write-Host "Release verification completed for $Configuration on $([Environment]::OSVersion.Platform)."
