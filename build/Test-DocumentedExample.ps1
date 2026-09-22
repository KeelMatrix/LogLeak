[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
$canonicalPath = Join-Path $root 'docs/examples/SourceGeneratedLoggingExample.cs'
$rootReadmePath = Join-Path $root 'README.md'
$packageReadmePath = Join-Path $root 'src/KeelMatrix.LogLeak/README.md'
$testProjectPath = Join-Path $root 'tests/KeelMatrix.LogLeak.Tests/KeelMatrix.LogLeak.Tests.csproj'
$consumerProjectPath = Join-Path $root 'tests/LogLeak.PackageConsumer.Net8/LogLeak.PackageConsumer.Net8.csproj'
$netstandardConsumerProjectPath = Join-Path $root 'tests/LogLeak.PackageConsumer/LogLeak.PackageConsumer.csproj'
$testSourcePath = Join-Path $root 'tests/KeelMatrix.LogLeak.Tests/LogLeakProbeTests.cs'
$consumerSourcePath = Join-Path $root 'tests/LogLeak.PackageConsumer.Net8/Program.cs'
$netstandardConsumerSourcePath = Join-Path $root 'tests/LogLeak.PackageConsumer/Program.cs'
$packageGatePath = Join-Path $root 'build/Invoke-PackageGate.ps1'

function Normalize-Text {
    param([Parameter(Mandatory)][string] $Text)

    return (($Text -replace "`r`n?", "`n").TrimEnd("`n") + "`n")
}

function Get-SourceGeneratedExample {
    param([Parameter(Mandatory)][string] $Markdown)

    $heading = '## Source-generated logging'
    $headingIndex = $Markdown.IndexOf($heading, [StringComparison]::Ordinal)
    if ($headingIndex -lt 0) {
        throw "The README is missing the '$heading' section."
    }

    $openingFence = $Markdown.IndexOf('```csharp', $headingIndex, [StringComparison]::Ordinal)
    if ($openingFence -lt 0) {
        throw "The README section '$heading' is missing its csharp code block."
    }

    $contentStart = $openingFence + '```csharp'.Length
    if ($contentStart -lt $Markdown.Length -and $Markdown[$contentStart] -eq "`r") {
        $contentStart++
    }
    if ($contentStart -lt $Markdown.Length -and $Markdown[$contentStart] -eq "`n") {
        $contentStart++
    }

    $closingFence = $Markdown.IndexOf('```', $contentStart, [StringComparison]::Ordinal)
    if ($closingFence -lt 0) {
        throw "The README section '$heading' has an unterminated csharp code block."
    }

    return $Markdown.Substring($contentStart, $closingFence - $contentStart)
}

function Assert-ExactMatch {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Expected,
        [Parameter(Mandatory)][string] $Actual
    )

    if ((Normalize-Text $Actual) -cne (Normalize-Text $Expected)) {
        throw "$Name does not exactly match the canonical source '$canonicalPath'."
    }
}

foreach ($path in @($canonicalPath, $rootReadmePath, $packageReadmePath, $testProjectPath, $consumerProjectPath, $netstandardConsumerProjectPath, $testSourcePath, $consumerSourcePath, $netstandardConsumerSourcePath, $packageGatePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required documented-example surface '$path' does not exist."
    }
}

$canonical = Get-Content -Raw -LiteralPath $canonicalPath
$rootReadme = Get-Content -Raw -LiteralPath $rootReadmePath
$packageReadme = Get-Content -Raw -LiteralPath $packageReadmePath
Assert-ExactMatch 'Root README example' $canonical (Get-SourceGeneratedExample $rootReadme)
Assert-ExactMatch 'Package README example' $canonical (Get-SourceGeneratedExample $packageReadme)

$canonicalInclude = 'docs/examples/SourceGeneratedLoggingExample.cs'
foreach ($projectPath in @($testProjectPath, $consumerProjectPath, $netstandardConsumerProjectPath, $packageGatePath)) {
    $projectText = Get-Content -Raw -LiteralPath $projectPath
    if ($projectText -notmatch [Regex]::Escape($canonicalInclude)) {
        throw "Project '$projectPath' does not compile the canonical example '$canonicalInclude'."
    }
}

$canonicalCall = 'SourceGeneratedLoggingExample.Run();'
foreach ($sourcePath in @($testSourcePath, $consumerSourcePath, $netstandardConsumerSourcePath, $packageGatePath)) {
    $sourceText = Get-Content -Raw -LiteralPath $sourcePath
    if ($sourceText -notmatch [Regex]::Escape($canonicalCall)) {
        throw "Executable coverage '$sourcePath' does not run the canonical example."
    }
}

Write-Host 'Documented source-generated logging example is synchronized across both READMEs and executable coverage.'
