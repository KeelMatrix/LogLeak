[CmdletBinding()]
param(
    [ValidateSet('All', 'Build', 'Pack', 'Inspect', 'Smoke')]
    [string] $Stage = 'All',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Version = '0.1.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Write-Host 'validation telemetry: KEELMATRIX_NO_TELEMETRY=1; DOTNET_CLI_TELEMETRY_OPTOUT=1 (inherited by every child process)'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$buildDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = Split-Path -Parent $buildDirectory
$shippingProject = Join-Path $repositoryRoot 'src/KeelMatrix.LogLeak/KeelMatrix.LogLeak.csproj'
$restoreConfig = Join-Path $repositoryRoot 'NuGet.config'
$artifactRoot = Join-Path $repositoryRoot 'artifacts/gate'
$buildRoot = Join-Path $artifactRoot 'build'
$buildOutput = Join-Path $buildRoot 'bin'
$buildIntermediate = Join-Path $buildRoot 'obj'
$packageOutput = Join-Path $artifactRoot 'packages'
$expectedPackageName = "KeelMatrix.LogLeak.$Version.nupkg"
$expectedSymbolsName = "KeelMatrix.LogLeak.$Version.snupkg"

function Add-TrailingSeparator {
    param([Parameter(Mandatory)][string] $Path)

    if ($Path.EndsWith([IO.Path]::DirectorySeparatorChar) -or $Path.EndsWith([IO.Path]::AltDirectorySeparatorChar)) {
        return $Path
    }

    return $Path + [IO.Path]::DirectorySeparatorChar
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)][string[]] $Arguments,
        [hashtable] $Environment
    )

    $displayArguments = $Arguments | ForEach-Object {
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }
    Write-Host ('dotnet ' + ($displayArguments -join ' '))
    Write-Host 'child environment: KEELMATRIX_NO_TELEMETRY=1; DOTNET_CLI_TELEMETRY_OPTOUT=1'

    $previousEnvironment = @{}
    if ($null -ne $Environment) {
        foreach ($name in $Environment.Keys) {
            $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, [string] $Environment[$name], 'Process')
        }
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & dotnet @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        $stopwatch.Stop()
        if ($null -ne $Environment) {
            foreach ($name in $Environment.Keys) {
                [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
            }
        }
    }

    Write-Host ('exit code ' + $exitCode + '; duration ' + $stopwatch.Elapsed.ToString('hh\:mm\:ss\.fff'))
    if ($exitCode -ne 0) {
        throw "dotnet command failed with exit code $exitCode."
    }
}

function Remove-Directory {
    param([Parameter(Mandatory)][string] $Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Ensure-Directory {
    param([Parameter(Mandatory)][string] $Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Invoke-Build {
    Remove-Directory $buildRoot
    Ensure-Directory $buildOutput
    Ensure-Directory $buildIntermediate

    $intermediatePath = Add-TrailingSeparator $buildIntermediate
    $outputPath = Add-TrailingSeparator $buildOutput
    Invoke-DotNet @(
        'restore', $shippingProject,
        '--configfile', $restoreConfig,
        '--force', '--no-cache',
        "-p:BaseIntermediateOutputPath=$intermediatePath",
        "-p:Version=$Version",
        "-p:PackageVersion=$Version"
    )
    Invoke-DotNet @(
        'build', $shippingProject,
        '--configuration', $Configuration,
        '--no-restore',
        "-p:BaseOutputPath=$outputPath",
        "-p:BaseIntermediateOutputPath=$intermediatePath",
        "-p:Version=$Version",
        "-p:PackageVersion=$Version"
    )
}

function Invoke-Pack {
    Remove-Directory $packageOutput
    Ensure-Directory $packageOutput

    $intermediatePath = Add-TrailingSeparator $buildIntermediate
    $outputPath = Add-TrailingSeparator $buildOutput
    Invoke-DotNet @(
        'pack', $shippingProject,
        '--configuration', $Configuration,
        '--no-build',
        '--output', $packageOutput,
        "-p:BaseOutputPath=$outputPath",
        "-p:BaseIntermediateOutputPath=$intermediatePath",
        "-p:Version=$Version",
        "-p:PackageVersion=$Version",
        '-p:IncludeSymbols=true',
        '-p:SymbolPackageFormat=snupkg'
    )
}

function Get-ArchiveEntries {
    param([Parameter(Mandatory)][string] $Path)

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | ForEach-Object FullName)
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ArchiveBytes {
    param(
        [Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string] $EntryName
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) {
            throw "Archive '$ArchivePath' is missing '$EntryName'."
        }

        $stream = $entry.Open()
        try {
            $memory = New-Object IO.MemoryStream
            try {
                $stream.CopyTo($memory)
                return $memory.ToArray()
            }
            finally {
                $memory.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ArchiveText {
    param(
        [Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string] $EntryName
    )

    $bytes = Get-ArchiveBytes $ArchivePath $EntryName
    return ([Text.Encoding]::UTF8.GetString($bytes)).TrimStart([char]0xFEFF)
}

function Assert-SetEqual {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string[]] $Expected,
        [Parameter(Mandatory)][string[]] $Actual
    )

    $missing = @($Expected | Where-Object { $Actual -notcontains $_ })
    $unexpected = @($Actual | Where-Object { $Expected -notcontains $_ })
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0 -or $Expected.Count -ne $Actual.Count) {
        throw "$Name mismatch. Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ')."
    }
}

function Assert-ArchiveSafety {
    param(
        [Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string[]] $Entries
    )

    $forbiddenPattern = '(?i)(^|/)(\.env(?:\..*)?|keelmatrix\.telemetry\.json|AGENTS\.md|SOUL\.md|.*(?:secret|credential|password|research).*)$|(^|/)(?:bin|obj|tests?|samples?|probes?)(/|$)|(^|/).*\.(?:cs|csproj|props|targets)$|(^|/).*\.(?:pfx|p12|pem|key|crt)$'
    $forbidden = @($Entries | Where-Object { $_ -match $forbiddenPattern })
    if ($forbidden.Count -gt 0) {
        throw "Archive '$ArchivePath' contains forbidden entries: $($forbidden -join ', ')."
    }
}

function Get-PngDimension {
    param(
        [Parameter(Mandatory)][byte[]] $Bytes,
        [Parameter(Mandatory)][int] $Offset
    )

    return ([uint32]$Bytes[$Offset] -shl 24) -bor
        ([uint32]$Bytes[$Offset + 1] -shl 16) -bor
        ([uint32]$Bytes[$Offset + 2] -shl 8) -bor
        [uint32]$Bytes[$Offset + 3]
}

function Invoke-Inspect {
    if (-not (Test-Path -LiteralPath $packageOutput)) {
        throw "Package output '$packageOutput' does not exist. Run the Pack stage first."
    }

    $files = @(Get-ChildItem -LiteralPath $packageOutput -File)
    $actualFiles = @($files | ForEach-Object Name)
    Assert-SetEqual 'Package artifact set' @($expectedPackageName, $expectedSymbolsName) $actualFiles

    $packagePath = Join-Path $packageOutput $expectedPackageName
    $symbolsPath = Join-Path $packageOutput $expectedSymbolsName
    $entries = Get-ArchiveEntries $packagePath
    $symbolsEntries = Get-ArchiveEntries $symbolsPath
    Assert-ArchiveSafety $packagePath $entries
    Assert-ArchiveSafety $symbolsPath $symbolsEntries

    $nuspecEntries = @($entries | Where-Object { $_ -match '\.nuspec$' })
    if ($nuspecEntries.Count -ne 1) {
        throw "Expected one package nuspec, found $($nuspecEntries.Count)."
    }
    $nuspecEntry = $nuspecEntries[0]
    $coreProperties = @($entries | Where-Object { $_ -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' })
    if ($coreProperties.Count -ne 1) {
        throw "Expected one package core-properties entry, found $($coreProperties.Count)."
    }

    $frameworkEntries = @(
        'lib/net8.0/KeelMatrix.LogLeak.dll',
        'lib/net8.0/KeelMatrix.LogLeak.xml',
        'lib/netstandard2.0/KeelMatrix.LogLeak.dll',
        'lib/netstandard2.0/KeelMatrix.LogLeak.xml'
    )
    $expectedEntries = @(
        '_rels/.rels',
        '[Content_Types].xml',
        $nuspecEntry,
        'README.md',
        'LICENSE',
        'icon.png'
    ) + $frameworkEntries + $coreProperties
    Assert-SetEqual 'Package contents' $expectedEntries $entries

    $nuspec = [xml](Get-ArchiveText $packagePath $nuspecEntry)
    $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) {
        throw 'Package nuspec has no metadata element.'
    }

    $expectedMetadata = @{
        id = 'KeelMatrix.LogLeak'
        version = $Version
        authors = 'KeelMatrix'
        description = 'Verify that explicitly registered sensitive values do not survive into your Microsoft.Extensions.Logging event stream during tests.'
        tags = 'logging security privacy testing ilogger aspnetcore redaction ci'
        license = 'MIT'
        readme = 'README.md'
        icon = 'icon.png'
    }
    foreach ($key in $expectedMetadata.Keys) {
        $node = $metadata.SelectSingleNode("./*[local-name()='$key']")
        if ($null -eq $node) {
            throw "Package metadata is missing '$key'."
        }
        $actualValue = $node.InnerText
        if ($key -eq 'tags') {
            $actualValue = (($actualValue -split '\s+') | Where-Object { $_ -ne '' }) -join ' '
        }
        if ($actualValue -ne $expectedMetadata[$key]) {
            throw "Package metadata '$key' was '$actualValue', expected '$($expectedMetadata[$key])'."
        }
    }

    $repository = $metadata.SelectSingleNode("./*[local-name()='repository']")
    if ($null -eq $repository -or $repository.GetAttribute('url') -ne 'https://github.com/KeelMatrix/LogLeak' -or $repository.GetAttribute('type') -ne 'git') {
        throw 'Package repository metadata is incorrect.'
    }

    $dependencyGroups = @($metadata.SelectNodes("./*[local-name()='dependencies']/*[local-name()='group']"))
    if ($dependencyGroups.Count -ne 2) {
        throw "Expected dependency groups for net8.0 and netstandard2.0, found $($dependencyGroups.Count)."
    }
    $dependencyNodes = @($dependencyGroups | ForEach-Object { $_.SelectNodes("./*[local-name()='dependency']") })
    $expectedDependencies = @{
        'KeelMatrix.Telemetry' = '[0.1.1]'
        'Microsoft.Extensions.Logging.Abstractions' = '8.0.2'
    }
    if ($dependencyNodes.Count -ne ($expectedDependencies.Count * $dependencyGroups.Count)) {
        throw "Package dependency count was $($dependencyNodes.Count), expected $($($expectedDependencies.Count * $dependencyGroups.Count))."
    }
    foreach ($group in $dependencyGroups) {
        $groupDependencies = @($group.SelectNodes("./*[local-name()='dependency']"))
        if ($groupDependencies.Count -ne $expectedDependencies.Count) {
            throw "A package dependency group contained $($groupDependencies.Count) dependencies, expected $($expectedDependencies.Count)."
        }
        foreach ($dependency in $groupDependencies) {
            $id = $dependency.GetAttribute('id')
            if (-not $expectedDependencies.ContainsKey($id)) {
                throw "Unexpected package dependency '$id'."
            }
            if ($dependency.GetAttribute('version') -ne $expectedDependencies[$id]) {
                throw "Dependency '$id' resolved as '$($dependency.GetAttribute('version'))', expected '$($expectedDependencies[$id])'."
            }
        }
    }

    $symbolsCoreProperties = @($symbolsEntries | Where-Object { $_ -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' })
    if ($symbolsCoreProperties.Count -ne 1) {
        throw "Expected one symbols core-properties entry, found $($symbolsCoreProperties.Count)."
    }
    $symbolNuspecEntries = @($symbolsEntries | Where-Object { $_ -match '\.nuspec$' })
    if ($symbolNuspecEntries.Count -ne 1) {
        throw "Expected one symbols nuspec, found $($symbolNuspecEntries.Count)."
    }
    $expectedSymbolsEntries = @(
        '_rels/.rels',
        '[Content_Types].xml',
        $symbolNuspecEntries[0],
        'lib/net8.0/KeelMatrix.LogLeak.pdb',
        'lib/netstandard2.0/KeelMatrix.LogLeak.pdb'
    ) + $symbolsCoreProperties
    Assert-SetEqual 'Symbols package contents' $expectedSymbolsEntries $symbolsEntries

    $iconBytes = Get-ArchiveBytes $packagePath 'icon.png'
    if ($iconBytes.Length -gt 204800) {
        throw "Package icon is $($iconBytes.Length) bytes; the maximum is 204800 bytes."
    }
    if ($iconBytes.Length -lt 24 -or [BitConverter]::ToString($iconBytes[0..7]) -ne '89-50-4E-47-0D-0A-1A-0A' -or [Text.Encoding]::ASCII.GetString($iconBytes[12..15]) -ne 'IHDR') {
        throw 'Package icon is not a valid PNG with an IHDR chunk.'
    }
    $iconWidth = Get-PngDimension $iconBytes 16
    $iconHeight = Get-PngDimension $iconBytes 20
    if ($iconWidth -ne 512 -or $iconHeight -ne 512) {
        throw "Package icon dimensions were ${iconWidth}x${iconHeight}; expected 512x512."
    }

    Write-Host "Inspected $expectedPackageName and ${expectedSymbolsName}: exact metadata, dependencies, TFMs, README, license, icon, symbols, and forbidden-entry checks passed."
}

function Invoke-Smoke {
    $packagePath = Join-Path $packageOutput $expectedPackageName
    if (-not (Test-Path -LiteralPath $packagePath)) {
        throw "Package '$packagePath' does not exist. Run the Pack stage first."
    }

    $smokeRoot = Join-Path $artifactRoot 'consumer'
    Remove-Directory $smokeRoot
    $localFeed = Join-Path $smokeRoot 'local-feed'
    $consumerCache = Join-Path $smokeRoot 'nuget-packages'
    $httpCache = Join-Path $smokeRoot 'nuget-http-cache'
    $cliHome = Join-Path $smokeRoot 'dotnet-home'
    Ensure-Directory $localFeed
    Ensure-Directory $consumerCache
    Ensure-Directory $httpCache
    Ensure-Directory $cliHome
    Copy-Item -LiteralPath $packagePath -Destination (Join-Path $localFeed $expectedPackageName)

    $consumerProject = Join-Path $smokeRoot 'PackageConsumer.csproj'
    $consumerConfig = Join-Path $smokeRoot 'NuGet.config'
    $consumerProgram = Join-Path $smokeRoot 'Program.cs'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$repositoryRoot/docs/examples/SourceGeneratedLoggingExample.cs" Link="SourceGeneratedLoggingExample.cs" />
    <PackageReference Include="KeelMatrix.LogLeak" Version="$Version" GeneratePathProperty="true" ExcludeAssets="compile;runtime" />
    <PackageReference Include="KeelMatrix.Telemetry" Version="0.1.1" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="8.0.1" />
    <Reference Include="KeelMatrix.LogLeak">
      <HintPath>`$(PkgKeelMatrix_LogLeak)/lib/netstandard2.0/KeelMatrix.LogLeak.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <None Include="`$(PkgKeelMatrix_LogLeak)/lib/netstandard2.0/KeelMatrix.LogLeak.dll"
          Link="consumer-assets/netstandard2.0/KeelMatrix.LogLeak.dll"
          CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $consumerProject -Encoding utf8NoBOM

    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-package-feed" value="$localFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local-package-feed">
      <package pattern="KeelMatrix.LogLeak" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="KeelMatrix.Telemetry" />
      <package pattern="Microsoft.AspNetCore.*" />
      <package pattern="Microsoft.Bcl.*" />
      <package pattern="Microsoft.Extensions.*" />
      <package pattern="Microsoft.WindowsDesktop.*" />
      <package pattern="System.*" />
      <package pattern="runtime.*" />
      <package pattern="NETStandard.Library" />
      <package pattern="Microsoft.NETCore.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $consumerConfig -Encoding utf8NoBOM

    @'
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using KeelMatrix.LogLeak;
using KeelMatrix.LogLeak.Documentation;
using Microsoft.Extensions.Logging;

var netstandardAsset = Path.Combine(AppContext.BaseDirectory, "consumer-assets", "netstandard2.0", "KeelMatrix.LogLeak.dll");
if (!File.Exists(netstandardAsset))
{
    throw new InvalidOperationException($"The packaged netstandard2.0 asset was not copied to '{netstandardAsset}'.");
}

var loadedLogLeakAssembly = typeof(LogLeakProbe).Assembly;
Console.WriteLine($"Consumer runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"LogLeak asset: {loadedLogLeakAssembly.Location}");
if (!string.Equals(
        Path.GetFullPath(loadedLogLeakAssembly.Location),
        Path.GetFullPath(netstandardAsset),
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("The package consumer did not execute the packaged netstandard2.0 LogLeak asset.");
}

SourceGeneratedLoggingExample.Run();

using var cleanProbe = new LogLeakProbe()
    .AddSecret("probe-pass", "synthetic-consumer-clean-18c2");
using (var cleanFactory = LoggerFactory.Create(builder => builder.AddProvider(cleanProbe.Provider)))
{
    cleanFactory.CreateLogger("PackageConsumer").LogInformation("safe package event");
    cleanProbe.AssertNoLeaks();
}

const string stateSentinel = "synthetic-consumer-dictionary-state-23cd";
using var stateProbe = new LogLeakProbe().AddSecret("S", stateSentinel);
using (var stateFactory = LoggerFactory.Create(builder => builder.AddProvider(stateProbe.Provider)))
{
    stateFactory.CreateLogger("PackageConsumer").Log(
        LogLevel.Information,
        new EventId(11),
        new Dictionary<string, string> { ["Authorization"] = stateSentinel },
        null,
        static (_, _) => "safe dictionary state event");
}

var stateResult = stateProbe.Verify();
if (stateResult.Status != LogLeakVerificationStatus.LeaksDetected
    || !stateResult.Findings.Any(finding => finding.Location == LogLeakLocation.StructuredProperty))
{
    throw new InvalidOperationException("The netstandard2.0 package asset did not inspect string-valued dictionary state.");
}

const string generatedSentinel = "synthetic-consumer-generated-67ab";
using var generatedProbe = new LogLeakProbe().AddSecret("G", generatedSentinel);
using (var generatedFactory = LoggerFactory.Create(builder => builder.AddProvider(generatedProbe.Provider)))
{
    GeneratedLogging.Write(generatedFactory.CreateLogger("PackageConsumer"), generatedSentinel);
}

var generatedResult = generatedProbe.Verify();
if (generatedResult.Status != LogLeakVerificationStatus.LeaksDetected
    || !generatedResult.Findings.Any(finding => finding.Location == LogLeakLocation.FormattedMessage))
{
    throw new InvalidOperationException("The packaged source-generated LoggerMessage path was not verified.");
}

const string scopeSentinel = "synthetic-consumer-scope-handle-78bc";
using var scopeProbe = new LogLeakProbe().AddSecret("H", scopeSentinel);
var scopeLogger = scopeProbe.Provider.CreateLogger("PackageConsumer");
var scopeHandle = scopeLogger.BeginScope(scopeSentinel);
if (scopeHandle is null)
{
    throw new InvalidOperationException("The package consumer scope handle was null.");
}

if ((scopeHandle.ToString() ?? string.Empty).Contains(scopeSentinel, StringComparison.Ordinal))
{
    throw new InvalidOperationException("The package consumer scope handle exposed the registered sentinel.");
}

scopeLogger.LogInformation("event inside scope");
scopeHandle.Dispose();
scopeLogger.LogInformation("event after scope");
if (scopeProbe.Verify().Findings.Count != 1)
{
    throw new InvalidOperationException("The package consumer scope handle did not preserve disposal behavior.");
}

AssertProviderArgumentIsSafe("categoryName", provider => provider.CreateLogger(null!), expectedParameterName: null);
AssertProviderArgumentIsSafe("formatter", provider => provider.CreateLogger("PackageConsumer").Log<string>(LogLevel.Information, default, "safe", null, null!), expectedParameterName: null);
AssertProviderArgumentIsSafe("newScopeProvider", provider => ((ISupportExternalScope)provider).SetScopeProvider(null!), expectedParameterName: null);
AssertProviderArgumentIsSafe("Value cannot be null.", provider => provider.CreateLogger(null!), expectedParameterName: "categoryName");
AssertProviderArgumentIsSafe("Value cannot be null. Parameter: categoryName.", provider => provider.CreateLogger(null!), expectedParameterName: "categoryName");
AssertProviderArgumentIsSafe("Value cannot be null.", provider => provider.CreateLogger("PackageConsumer").Log<string>(LogLevel.Information, default, "safe", null, null!), expectedParameterName: "formatter");
AssertProviderArgumentIsSafe("Value cannot be null. Parameter: formatter.", provider => provider.CreateLogger("PackageConsumer").Log<string>(LogLevel.Information, default, "safe", null, null!), expectedParameterName: "formatter");
AssertProviderArgumentIsSafe("Value cannot be null.", provider => ((ISupportExternalScope)provider).SetScopeProvider(null!), expectedParameterName: "newScopeProvider");
AssertProviderArgumentIsSafe("Value cannot be null. Parameter: newScopeProvider.", provider => ((ISupportExternalScope)provider).SetScopeProvider(null!), expectedParameterName: "newScopeProvider");

const string duringCaptureSentinel = "synthetic-consumer-during-capture-89cd";
using var duringCaptureProbe = new LogLeakProbe().AddSecret("I", duringCaptureSentinel);
using (var duringCaptureFactory = LoggerFactory.Create(builder => builder.AddProvider(duringCaptureProbe.Provider)))
{
    LogLeakVerificationResult? nested = null;
    var duringCaptureLogger = duringCaptureFactory.CreateLogger("PackageConsumer");
    duringCaptureLogger.Log(LogLevel.Information, default, duringCaptureSentinel, null, (state, _) =>
    {
        nested = duringCaptureProbe.Verify();
        return state;
    });

    if (nested?.Status != LogLeakVerificationStatus.Inconclusive)
    {
        throw new InvalidOperationException("Verification during an active formatter callback was not rejected.");
    }
}

if (duringCaptureProbe.Verify().Status != LogLeakVerificationStatus.LeaksDetected)
{
    throw new InvalidOperationException("The outer package consumer verification did not preserve its finding.");
}

const string exceptionSentinel = "synthetic-consumer-exception-capture-9ade";
using var exceptionProbe = new LogLeakProbe().AddSecret("K", exceptionSentinel);
var exceptionCallback = new DuringCaptureException(exceptionProbe, exceptionSentinel);
using (var exceptionFactory = LoggerFactory.Create(builder => builder.AddProvider(exceptionProbe.Provider)))
{
    exceptionFactory.CreateLogger("PackageConsumer").LogError(exceptionCallback, "safe exception event");
}

if (exceptionCallback.DuringCapture?.Status != LogLeakVerificationStatus.Inconclusive
    || !exceptionProbe.Verify().Findings.Any(finding => finding.Location == LogLeakLocation.ExceptionRepresentation))
{
    throw new InvalidOperationException("Verification during exception representation capture was not rejected.");
}

const string callbackStateSentinel = "synthetic-consumer-state-capture-abcf";
const string callbackScopeSentinel = "synthetic-consumer-scope-capture-def0";
using var callbackProbe = new LogLeakProbe()
    .AddSecret("L", callbackStateSentinel)
    .AddSecret("N", callbackScopeSentinel);
var callbackState = new DuringCaptureEntries(callbackProbe, "Authorization", callbackStateSentinel);
using (var callbackFactory = LoggerFactory.Create(builder => builder.AddProvider(callbackProbe.Provider)))
{
    var callbackLogger = callbackFactory.CreateLogger("PackageConsumer");
    callbackLogger.Log(LogLevel.Information, default, callbackState, null, static (_, _) => "safe state event");
    var callbackScope = new DuringCaptureEntries(callbackProbe, "ScopeToken", callbackScopeSentinel);
    using (callbackLogger.BeginScope(callbackScope))
    {
        callbackLogger.LogInformation("safe scope event");
    }

    if (callbackState.DuringCapture?.Status != LogLeakVerificationStatus.Inconclusive
        || callbackScope.DuringCapture?.Status != LogLeakVerificationStatus.Inconclusive)
    {
        throw new InvalidOperationException("Verification during state or scope enumeration was not rejected.");
    }
}

var callbackResult = callbackProbe.Verify();
if (callbackResult.Status != LogLeakVerificationStatus.LeaksDetected
    || !callbackResult.Findings.Any(finding => finding.Location == LogLeakLocation.StructuredProperty)
    || !callbackResult.Findings.Any(finding => finding.Location == LogLeakLocation.Scope))
{
    throw new InvalidOperationException("The package consumer callback findings were not preserved.");
}

const string reentrantSentinel = "synthetic-consumer-reentrant-45ef";
using var reentrantProbe = new LogLeakProbe().AddSecret("R", reentrantSentinel);
using (var reentrantFactory = LoggerFactory.Create(builder => builder.AddProvider(reentrantProbe.Provider)))
{
    var reentrantLogger = reentrantFactory.CreateLogger("PackageConsumer");
    reentrantLogger.Log(
        LogLevel.Information,
        new EventId(12),
        "outer safe event",
        null,
        (state, _) =>
        {
            reentrantLogger.LogInformation("nested safe event");
            return state;
        });
}

var reentrantResult = reentrantProbe.Verify();
if (reentrantResult.Status != LogLeakVerificationStatus.Inconclusive
    || reentrantResult.CapturedEventCount != 1
    || !reentrantResult.InconclusiveReason!.Contains("reentrant", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("The netstandard2.0 package asset did not reject same-probe reentrant capture.");
}

const string plantedValue = "synthetic-consumer-planted-91de";
using var plantedProbe = new LogLeakProbe()
    .AddSecret("leak", plantedValue);
using (var plantedFactory = LoggerFactory.Create(builder => builder.AddProvider(plantedProbe.Provider)))
{
    plantedFactory.CreateLogger("PackageConsumer").LogInformation("planted value {Value}", plantedValue);
    string diagnostic;
    try
    {
        plantedProbe.AssertNoLeaks();
        throw new InvalidOperationException("The package did not detect the planted value.");
    }
    catch (LogLeakAssertionException exception)
    {
        diagnostic = exception.ToString();
    }

    if (diagnostic.Contains(plantedValue, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The assertion diagnostic contained the planted value.");
    }

    if (!diagnostic.Contains("Sentinel label 'leak'", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The assertion diagnostic did not identify the planted registration.");
    }
}

using var boundedProbe = new LogLeakProbe(new LogLeakOptions(maximumCapturedEvents: 1))
    .AddSecret("B2", "synthetic-consumer-bound-34ef");
using (var boundedFactory = LoggerFactory.Create(builder => builder.AddProvider(boundedProbe.Provider)))
{
    var boundedLogger = boundedFactory.CreateLogger("PackageConsumer");
    boundedLogger.LogInformation("first bounded event");
    boundedLogger.LogInformation("second bounded event");
}

var boundedResult = boundedProbe.Verify();
if (boundedResult.Status != LogLeakVerificationStatus.Inconclusive
    || !boundedResult.InconclusiveReason!.Contains("capture event budget", StringComparison.Ordinal))
{
    throw new InvalidOperationException("The netstandard2.0 package asset did not fail closed on a deterministic capture bound breach.");
}

try
{
    boundedProbe.AssertNoLeaks();
    throw new InvalidOperationException("The netstandard2.0 package asset did not throw for a deterministic capture bound breach.");
}
catch (LogLeakInconclusiveException exception) when (!exception.ToString().Contains("synthetic-consumer-bound-34ef", StringComparison.Ordinal))
{
}

Console.WriteLine("Package consumer smoke passed.");

static void AssertProviderArgumentIsSafe(string sentinel, Action<ILoggerProvider> action, string? expectedParameterName)
{
    using var probe = new LogLeakProbe().AddSecret("J", sentinel);
    var exception = AssertThrows<ArgumentNullException>(() => action(probe.Provider));
    if (!string.Equals(exception.ParamName, expectedParameterName, StringComparison.Ordinal)
        || exception.Message.Contains(sentinel, StringComparison.Ordinal)
        || exception.ToString().Contains(sentinel, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("A provider argument diagnostic contained the registered sentinel.");
    }
}

static TException AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

internal static partial class GeneratedLogging
{
    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "generated package value {Value}")]
    internal static partial void Write(ILogger logger, string value);
}

internal static class PackageAssetLoader
{
    internal static readonly string AssetPath = Path.Combine(AppContext.BaseDirectory, "consumer-assets", "netstandard2.0", "KeelMatrix.LogLeak.dll");

    [ModuleInitializer]
    internal static void Register()
    {
        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
            string.Equals(assemblyName.Name, "KeelMatrix.LogLeak", StringComparison.Ordinal)
                && File.Exists(AssetPath)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(AssetPath)
                : null;
    }
}

internal sealed class DuringCaptureException : Exception
{
    private readonly LogLeakProbe probe;
    private readonly string representation;

    internal DuringCaptureException(LogLeakProbe probe, string representation)
        : base("safe exception")
    {
        this.probe = probe;
        this.representation = representation;
    }

    internal LogLeakVerificationResult? DuringCapture { get; private set; }

    public override string ToString()
    {
        DuringCapture = probe.Verify();
        return representation;
    }
}

internal sealed class DuringCaptureEntries : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly LogLeakProbe probe;
    private readonly string key;
    private readonly string value;

    internal DuringCaptureEntries(LogLeakProbe probe, string key, string value)
    {
        this.probe = probe;
        this.key = key;
        this.value = value;
    }

    internal LogLeakVerificationResult? DuringCapture { get; private set; }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        DuringCapture = probe.Verify();
        yield return new KeyValuePair<string, object?>(key, value);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();
}
'@ | Set-Content -LiteralPath $consumerProgram -Encoding utf8NoBOM

    Invoke-DotNet @(
        'restore', $consumerProject,
        '--configfile', $consumerConfig,
        '--force', '--no-cache',
        "-p:RestorePackagesPath=$consumerCache"
    ) @{
        NUGET_PACKAGES = $consumerCache
        NUGET_HTTP_CACHE_PATH = $httpCache
        DOTNET_CLI_HOME = $cliHome
        KEELMATRIX_NO_TELEMETRY = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }
    Invoke-DotNet @(
        'run', '--project', $consumerProject,
        '--configuration', 'Release',
        '--no-restore'
    ) @{
        NUGET_PACKAGES = $consumerCache
        NUGET_HTTP_CACHE_PATH = $httpCache
        DOTNET_CLI_HOME = $cliHome
        KEELMATRIX_NO_TELEMETRY = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }

    $assetPath = Join-Path $smokeRoot 'bin/Release/net8.0/consumer-assets/netstandard2.0/KeelMatrix.LogLeak.dll'
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "The net8.0 package consumer did not copy '$assetPath'."
    }
    Write-Host 'Consumer asset proof: net8.0 runtime output loaded lib/netstandard2.0/KeelMatrix.LogLeak.dll from consumer-assets/netstandard2.0.'

    $normalConsumerProject = Join-Path $repositoryRoot 'tests/LogLeak.PackageConsumer.Net8/LogLeak.PackageConsumer.Net8.csproj'
    $normalConsumerRoot = Join-Path $smokeRoot 'normal-net8-consumer'
    $normalConsumerIntermediate = Join-Path $normalConsumerRoot 'obj'
    $normalConsumerOutput = Join-Path $normalConsumerRoot 'bin'
    Ensure-Directory $normalConsumerRoot
    Ensure-Directory $normalConsumerIntermediate
    Ensure-Directory $normalConsumerOutput

    Invoke-DotNet @(
        'restore', $normalConsumerProject,
        '--configfile', $consumerConfig,
        '--force', '--no-cache',
        "-p:LogLeakPackageVersion=$Version",
        "-p:BaseIntermediateOutputPath=$(Add-TrailingSeparator $normalConsumerIntermediate)",
        "-p:RestorePackagesPath=$consumerCache"
    ) @{
        NUGET_PACKAGES = $consumerCache
        NUGET_HTTP_CACHE_PATH = $httpCache
        DOTNET_CLI_HOME = $cliHome
        KEELMATRIX_NO_TELEMETRY = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }

    $normalConsumerAssets = Join-Path $normalConsumerIntermediate 'project.assets.json'
    if (-not (Test-Path -LiteralPath $normalConsumerAssets)) {
        throw "The normal net8.0 package consumer did not produce '$normalConsumerAssets'."
    }
    $assetJson = Get-Content -Raw -LiteralPath $normalConsumerAssets | ConvertFrom-Json
    $net8Target = $assetJson.targets.'net8.0'."KeelMatrix.LogLeak/$Version"
    $net8CompileAssets = @($net8Target.compile.PSObject.Properties.Name)
    if ($net8CompileAssets -notcontains 'lib/net8.0/KeelMatrix.LogLeak.dll') {
        throw 'The normal net8.0 package consumer did not select the packaged lib/net8.0 asset.'
    }
    Write-Host 'Consumer asset proof: normal PackageReference selected lib/net8.0/KeelMatrix.LogLeak.dll.'

    Invoke-DotNet @(
        'run', '--project', $normalConsumerProject,
        '--configuration', 'Release',
        '--no-restore',
        "-p:LogLeakPackageVersion=$Version",
        "-p:BaseOutputPath=$(Add-TrailingSeparator $normalConsumerOutput)",
        "-p:BaseIntermediateOutputPath=$(Add-TrailingSeparator $normalConsumerIntermediate)"
    ) @{
        NUGET_PACKAGES = $consumerCache
        NUGET_HTTP_CACHE_PATH = $httpCache
        DOTNET_CLI_HOME = $cliHome
        KEELMATRIX_NO_TELEMETRY = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }
    Write-Host 'Normal net8.0 PackageReference consumer proof: clean, planted leak, source-generated logging, and sentinel-safe argument diagnostics passed.'

    $aspnetRoot = Join-Path $smokeRoot 'aspnet-consumer'
    $aspnetCache = Join-Path $aspnetRoot 'nuget-packages'
    $aspnetHttpCache = Join-Path $aspnetRoot 'nuget-http-cache'
    $aspnetCliHome = Join-Path $aspnetRoot 'dotnet-home'
    $aspnetIntermediate = Join-Path $aspnetRoot 'obj'
    $aspnetOutput = Join-Path $aspnetRoot 'bin'
    $aspnetConfig = Join-Path $aspnetRoot 'NuGet.config'
    Ensure-Directory $aspnetRoot
    Ensure-Directory $aspnetCache
    Ensure-Directory $aspnetHttpCache
    Ensure-Directory $aspnetCliHome
    Ensure-Directory $aspnetIntermediate
    Ensure-Directory $aspnetOutput

    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-package-feed" value="$localFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local-package-feed">
      <package pattern="KeelMatrix.LogLeak" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="KeelMatrix.Telemetry" />
      <package pattern="Microsoft.AspNetCore.*" />
      <package pattern="Microsoft.Bcl.*" />
      <package pattern="Microsoft.Extensions.*" />
      <package pattern="Microsoft.NETCore.*" />
      <package pattern="Microsoft.WindowsDesktop.*" />
      <package pattern="System.*" />
      <package pattern="runtime.*" />
      <package pattern="NETStandard.Library" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $aspnetConfig -Encoding utf8NoBOM

    $aspnetProject = Join-Path $repositoryRoot 'samples/KeelMatrix.LogLeak.AspNetCore/KeelMatrix.LogLeak.AspNetCore.csproj'
    Invoke-DotNet @(
        'restore', $aspnetProject,
        '--configfile', $aspnetConfig,
        '--force', '--no-cache',
        "-p:BaseIntermediateOutputPath=$(Add-TrailingSeparator $aspnetIntermediate)",
        "-p:RestorePackagesPath=$aspnetCache"
    ) @{
        NUGET_PACKAGES = $aspnetCache
        NUGET_HTTP_CACHE_PATH = $aspnetHttpCache
        DOTNET_CLI_HOME = $aspnetCliHome
        KEELMATRIX_NO_TELEMETRY = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }
    Invoke-DotNet @(
        'build', $aspnetProject,
        '--configuration', 'Release',
        '--no-restore',
        "-p:BaseOutputPath=$(Add-TrailingSeparator $aspnetOutput)",
        "-p:BaseIntermediateOutputPath=$(Add-TrailingSeparator $aspnetIntermediate)"
    ) @{
        NUGET_PACKAGES = $aspnetCache
        NUGET_HTTP_CACHE_PATH = $aspnetHttpCache
        DOTNET_CLI_HOME = $aspnetCliHome
        KEELMATRIX_NO_TELEMETRY = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }

    $aspnetDll = Join-Path $aspnetOutput 'Release/net8.0/KeelMatrix.LogLeak.AspNetCore.dll'
    if (-not (Test-Path -LiteralPath $aspnetDll)) {
        throw "The ASP.NET Core package consumer did not produce '$aspnetDll'."
    }

    $aspnetStdout = Join-Path $aspnetRoot 'stdout.log'
    $aspnetStderr = Join-Path $aspnetRoot 'stderr.log'
    $aspnetUrl = 'http://127.0.0.1:5287'
    $startProcessParameters = @{
        FilePath = 'dotnet'
        ArgumentList = @($aspnetDll, '--urls', $aspnetUrl)
        PassThru = $true
        RedirectStandardOutput = $aspnetStdout
        RedirectStandardError = $aspnetStderr
    }
    if ($IsWindows) {
        $startProcessParameters.WindowStyle = 'Hidden'
    }

    $aspnetProcess = Start-Process @startProcessParameters
    try {
        $responseContent = $null
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and $null -eq $responseContent) {
            if ($aspnetProcess.HasExited) {
                break
            }

            try {
                $response = Invoke-WebRequest -Uri ($aspnetUrl + '/health') -UseBasicParsing -TimeoutSec 2
                $responseContent = $response.Content
            }
            catch {
                Start-Sleep -Milliseconds 250
            }
        }

        if ($responseContent -ne '{"status":"LogLeak clean"}') {
            $details = if (Test-Path -LiteralPath $aspnetStderr) { Get-Content -Raw -LiteralPath $aspnetStderr } else { '' }
            throw "The packaged ASP.NET Core sample did not return the expected clean response. Response='$responseContent'. Error='$details'."
        }
    }
    finally {
        if (-not $aspnetProcess.HasExited) {
            Stop-Process -Id $aspnetProcess.Id -Force
            $aspnetProcess.WaitForExit()
        }
    }
    Write-Host 'ASP.NET Core artifact proof: isolated source-mapped restore selected the local package and /health returned LogLeak clean.'
}

switch ($Stage) {
    'All' {
        Remove-Directory $artifactRoot
        Ensure-Directory $artifactRoot
        Invoke-Build
        Invoke-Pack
        Invoke-Inspect
        Invoke-Smoke
    }
    'Build' {
        Invoke-Build
    }
    'Pack' {
        Invoke-Build
        Invoke-Pack
    }
    'Inspect' {
        Invoke-Inspect
    }
    'Smoke' {
        Invoke-Smoke
    }
}

Write-Host "Package gate stage '$Stage' completed successfully."
