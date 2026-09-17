[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$validatorPath = Join-Path $PSScriptRoot 'Validate-ReleaseContract.ps1'
$scratchRoot = [IO.Path]::GetTempPath()
$fixtureRoot = Join-Path $scratchRoot "logleak-release-contract-$([guid]::NewGuid().ToString('N'))"
$releaseDate = '2026-09-16'

function New-Fixture {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Changelog,
        [string] $SourceVersion = '0.1.0',
        [string] $InstallVersion = $SourceVersion
    )

    New-Item -ItemType Directory -Force -Path (Join-Path $Path 'build') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Path 'src/KeelMatrix.LogLeak') | Out-Null

    @"
<Project>
  <PropertyGroup>
    <Version>$SourceVersion</Version>
    <PackageVersion>`$(Version)</PackageVersion>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $Path 'Directory.Build.props') -Encoding utf8NoBOM

    @"
<Project>
  <ItemGroup>
    <PackageVersion Include="KeelMatrix.Telemetry" Version="[0.1.0]" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $Path 'Directory.Packages.props') -Encoding utf8NoBOM

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KeelMatrix.LogLeak</PackageId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="KeelMatrix.Telemetry" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $Path 'src/KeelMatrix.LogLeak/KeelMatrix.LogLeak.csproj') -Encoding utf8NoBOM

    @"
# KeelMatrix.LogLeak

Install with `dotnet add package KeelMatrix.LogLeak --version $InstallVersion`.
"@ | Set-Content -LiteralPath (Join-Path $Path 'README.md') -Encoding utf8NoBOM
    $Changelog | Set-Content -LiteralPath (Join-Path $Path 'CHANGELOG.md') -Encoding utf8NoBOM
}

function Invoke-Scenario {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Changelog,
        [Parameter(Mandatory)][bool] $ShouldPass,
        [string] $Tag = 'v0.1.0',
        [string] $Version = '0.1.0',
        [string] $SourceVersion = '0.1.0',
        [string] $InstallVersion = $SourceVersion,
        [string] $ExpectedDiagnostic
    )

    $scenarioRoot = Join-Path $fixtureRoot ($Name -replace '[^A-Za-z0-9-]', '-')
    New-Fixture $scenarioRoot $Changelog $SourceVersion $InstallVersion

    $arguments = @(
        '-NoProfile',
        '-File', $validatorPath,
        '-RepositoryRoot', $scenarioRoot,
        '-Version', $Version,
        '-Tag', $Tag,
        '-ExpectedReleaseDate', $releaseDate
    )
    $command = "pwsh " + (($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $output = @(& pwsh @arguments 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    $stopwatch.Stop()

    Write-Host "[$Name]"
    Write-Host "Command: $command"
    if ($output.Count -gt 0 -and $output.Trim() -ne '') {
        Write-Host $output.Trim()
    }
    Write-Host "Exit code: $exitCode; duration $($stopwatch.Elapsed.ToString('hh\:mm\:ss\.fff'))"

    if ($ShouldPass -and $exitCode -ne 0) {
        throw "Scenario '$Name' should pass but exited $exitCode."
    }
    if (-not $ShouldPass -and $exitCode -eq 0) {
        throw "Scenario '$Name' should fail but passed."
    }
    if ($ExpectedDiagnostic -and $output -notmatch [regex]::Escape($ExpectedDiagnostic)) {
        throw "Scenario '$Name' did not contain expected diagnostic '$ExpectedDiagnostic'."
    }
}

$plannedChangelog = @'
# Changelog

## [Unreleased]

### Added

- Provides bounded sentinel verification.
'@

$finalizedChangelog = @"
# Changelog

## [Unreleased]

## [0.1.0] - $releaseDate

### Added

- Provides bounded sentinel verification for captured logging fields.
"@

$remediationChangelog = @"
# Changelog

## [Unreleased]

## [0.1.0] - $releaseDate

### Added

- Fixed the diagnostic path.
"@

try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    Invoke-Scenario -Name 'planned-unreleased-rejected' -Changelog $plannedChangelog -ShouldPass $false -ExpectedDiagnostic 'no finalized'
    Invoke-Scenario -Name 'finalized-consistent-passes' -Changelog $finalizedChangelog -ShouldPass $true
    Invoke-Scenario -Name 'changelog-package-mismatch-rejected' -Changelog $finalizedChangelog -ShouldPass $false -SourceVersion '0.1.1' -InstallVersion '0.1.1' -ExpectedDiagnostic "source property 'Version'"
    Invoke-Scenario -Name 'first-release-remediation-marker-rejected' -Changelog $remediationChangelog -ShouldPass $false -ExpectedDiagnostic 'remediation-history'
    Write-Host 'Release contract scenarios passed.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
