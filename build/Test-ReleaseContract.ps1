[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$validatorPath = Join-Path $PSScriptRoot 'Validate-ReleaseContract.ps1'
$resolverPath = Join-Path $PSScriptRoot 'Resolve-ReleaseVersion.ps1'
$scratchRoot = [IO.Path]::GetTempPath()
$fixtureRoot = Join-Path $scratchRoot "logleak-release-contract-$([guid]::NewGuid().ToString('N'))"
$releaseDate = '2026-09-16'

function New-Fixture {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Changelog,
        [string] $SourceVersion = '0.1.0',
        [string] $InstallVersion = $SourceVersion,
        [string] $ShippingPackageVersion = '0.1.0'
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
    <PackageVersion Include="KeelMatrix.LogLeak" Version="[$ShippingPackageVersion]" />
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
        [string] $ShippingPackageVersion = '0.1.0',
        [string] $ExpectedDiagnostic
    )

    $scenarioRoot = Join-Path $fixtureRoot ($Name -replace '[^A-Za-z0-9-]', '-')
    New-Fixture $scenarioRoot $Changelog $SourceVersion $InstallVersion $ShippingPackageVersion

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

function Test-ReleaseResolver {
    $outputPath = Join-Path $fixtureRoot 'resolver-output.txt'
    $head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    $commitDate = (& git -C $repositoryRoot show -s --format=%cs $head).Trim()
    $arguments = @(
        '-NoProfile',
        '-File', $resolverPath,
        '-RepositoryRoot', $repositoryRoot,
        '-Tag', 'v0.1.0',
        '-RefType', 'tag',
        '-ExpectedCommit', $head,
        '-OutputPath', $outputPath,
        '-TagCommitOverride', $head,
        '-CommitDateOverride', $commitDate
    )
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $output = @(& pwsh @arguments 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    $stopwatch.Stop()

    Write-Host '[release-resolver]'
    if ($output.Count -gt 0 -and $output.Trim() -ne '') {
        Write-Host $output.Trim()
    }
    Write-Host "Exit code: $exitCode; duration $($stopwatch.Elapsed.ToString('hh\:mm\:ss\.fff'))"
    if ($exitCode -ne 0) {
        throw "Release resolver should pass but exited $exitCode."
    }

    $values = @{}
    foreach ($line in @(Get-Content -LiteralPath $outputPath)) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $values[$parts[0]] = $parts[1]
        }
    }

    $version = [string] $values['version']
    if ($version -ne '0.1.0') {
        throw "Release resolver output version '$version'; expected '0.1.0'."
    }
    $expectedArtifacts = @(
        "KeelMatrix.LogLeak.$version.nupkg"
        "KeelMatrix.LogLeak.$version.snupkg"
    ) | Sort-Object
    $actualArtifactNames = @(
        "KeelMatrix.LogLeak.$($values['version']).nupkg"
        "KeelMatrix.LogLeak.$($values['version']).snupkg"
    ) | Sort-Object
    if (Compare-Object -ReferenceObject $expectedArtifacts -DifferenceObject $actualArtifactNames) {
        throw "Release artifact naming did not follow resolver output."
    }
}

function Test-WorkflowTrigger {
    $workflowPath = Join-Path $repositoryRoot '.github/workflows/release.yml'
    $workflowText = Get-Content -LiteralPath $workflowPath -Raw
    $tagPatternMatch = [regex]::Match(
        $workflowText,
        '(?m)^\s*tags:\s*\r?\n\s*-\s*[''\"](?<pattern>[^''\"]+)[''\"]\s*$'
    )
    if (-not $tagPatternMatch.Success) {
        throw "Could not read the release tag pattern from '$workflowPath'."
    }

    $tagPattern = $tagPatternMatch.Groups['pattern'].Value
    $releaseDocumentationPath = Join-Path $repositoryRoot 'docs/RELEASE.md'
    $releaseDocumentation = Get-Content -LiteralPath $releaseDocumentationPath -Raw
    $documentationContainsPattern = $releaseDocumentation.Contains($tagPattern)
    $firstReleaseTag = 'v0.1.0'
    $nonReleaseTag = 'release-0.1.0'
    $firstReleaseMatches = $firstReleaseTag -like $tagPattern
    $nonReleaseMatches = $nonReleaseTag -like $tagPattern

    Write-Host '[workflow-trigger]'
    Write-Host "Workflow: $workflowPath"
    Write-Host "Pattern: '$tagPattern'"
    Write-Host "First release '$firstReleaseTag' matches: $firstReleaseMatches"
    Write-Host "Non-release '$nonReleaseTag' matches: $nonReleaseMatches"
    Write-Host "Release documentation contains pattern: $documentationContainsPattern"

    if (-not $documentationContainsPattern) {
        throw "Release documentation '$releaseDocumentationPath' does not contain workflow tag pattern '$tagPattern'."
    }
    if (-not $firstReleaseMatches) {
        throw "Release workflow tag pattern '$tagPattern' does not match intended tag '$firstReleaseTag'."
    }
    if ($nonReleaseMatches) {
        throw "Release workflow tag pattern '$tagPattern' unexpectedly matches non-release tag '$nonReleaseTag'."
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

$remediationPhrases = @(
    'now',
    'no longer',
    'not yet published',
    'previously',
    'formerly',
    'used to',
    'fixed',
    'fixes',
    'fixing',
    'correction',
    'corrected',
    'resolved',
    'resolution',
    'addressed',
    'addressing',
    'this removes',
    'this fixes',
    'changed from',
    'rectified',
    'rectifying',
    'replaces',
    'replacing',
    'revised',
    'revising',
    'supersedes',
    'superseded',
    'overrides',
    'rewritten',
    'unlike the earlier',
    'compared with the earlier',
    'in contrast to the previous',
    'rather than the prior'
)

$capabilityWording = @(
    'Provides bounded sentinel verification for captured logging fields.',
    'Supports structured logging fields.',
    'Reports safe finding metadata.',
    'Uses deterministic capture limits.',
    'Bounded exception and scope support.'
)

try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    Test-WorkflowTrigger
    Test-ReleaseResolver
    Invoke-Scenario -Name 'planned-unreleased-rejected' -Changelog $plannedChangelog -ShouldPass $false -ExpectedDiagnostic 'no finalized'
    Invoke-Scenario -Name 'finalized-consistent-passes' -Changelog $finalizedChangelog -ShouldPass $true
    Invoke-Scenario -Name 'changelog-package-mismatch-rejected' -Changelog $finalizedChangelog -ShouldPass $false -SourceVersion '0.1.1' -InstallVersion '0.1.1' -ExpectedDiagnostic "source property 'Version'"
    Invoke-Scenario -Name 'shipping-package-version-mismatch-rejected' -Changelog $finalizedChangelog -ShouldPass $false -ShippingPackageVersion '0.1.1' -ExpectedDiagnostic "central shipping package 'KeelMatrix.LogLeak'"
    for ($index = 0; $index -lt $remediationPhrases.Count; $index++) {
        $phrase = $remediationPhrases[$index]
        $changelog = @"
# Changelog

## [Unreleased]

## [0.1.0] - $releaseDate

### Added

- Provides the initial capability; $phrase.
"@
        Invoke-Scenario -Name "first-release-remediation-$index" -Changelog $changelog -ShouldPass $false
    }
    for ($index = 0; $index -lt $capabilityWording.Count; $index++) {
        $changelog = @"
# Changelog

## [Unreleased]

## [0.1.0] - $releaseDate

### Added

- $($capabilityWording[$index])
"@
        Invoke-Scenario -Name "first-release-capability-$index" -Changelog $changelog -ShouldPass $true
    }
    Write-Host 'Release contract scenarios passed.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
