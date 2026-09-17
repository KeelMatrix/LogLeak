[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $Solution = 'LogLeak.Probe.sln'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Write-Host 'validation telemetry: KEELMATRIX_NO_TELEMETRY=1; DOTNET_CLI_TELEMETRY_OPTOUT=1 (inherited by the audit child process)'

$root = (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
$solutionPath = (Resolve-Path -LiteralPath (Join-Path $root $Solution) -ErrorAction Stop).Path
$configPath = (Resolve-Path -LiteralPath (Join-Path $root 'NuGet.config') -ErrorAction Stop).Path

$arguments = @(
    'list', $solutionPath, 'package',
    '--vulnerable',
    '--include-transitive',
    '--format', 'json',
    '--output-version', '1',
    '--configfile', $configPath
)

$reportLines = @(& dotnet @arguments 2>&1)
$exitCode = $LASTEXITCODE
$reportText = ($reportLines | ForEach-Object { [string] $_ }) -join [Environment]::NewLine
if ($reportText.Trim() -ne '') {
    Write-Host $reportText
}
if ($exitCode -ne 0) {
    throw "The vulnerability audit command failed with exit code $exitCode."
}

try {
    $report = $reportText | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw "The vulnerability audit did not produce valid JSON: $($_.Exception.Message)"
}

if ($null -eq $report.PSObject.Properties['version'] -or $report.version -ne 1) {
    throw 'The vulnerability audit report did not declare output version 1.'
}
if ($null -eq $report.PSObject.Properties['projects'] -or @($report.projects).Count -eq 0) {
    throw 'The vulnerability audit report did not contain project results.'
}

function Find-VulnerabilityEntries {
    param(
        [AllowNull()][object] $Value,
        [Parameter(Mandatory)][string] $Path
    )

    if ($null -eq $Value -or $Value -is [string] -or $Value.GetType().IsPrimitive) {
        return @()
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [pscustomobject]) {
        $entries = @()
        $index = 0
        foreach ($item in $Value) {
            $entries += Find-VulnerabilityEntries $item "$Path[$index]"
            $index++
        }
        return $entries
    }

    $entries = @()
    foreach ($property in $Value.PSObject.Properties) {
        $propertyPath = "$Path.$($property.Name)"
        if ($property.Name -ieq 'vulnerabilities' -and $null -ne $property.Value) {
            foreach ($vulnerability in @($property.Value)) {
                $entries += [pscustomobject]@{
                    Path = $propertyPath
                    Value = [string] $vulnerability
                }
            }
        }
        $entries += Find-VulnerabilityEntries $property.Value $propertyPath
    }
    return $entries
}

$findings = @(Find-VulnerabilityEntries $report '$')
if ($findings.Count -gt 0) {
    $details = ($findings | ForEach-Object { "$($_.Path): $($_.Value)" }) -join '; '
    throw "The vulnerability audit found $($findings.Count) vulnerable package record(s): $details"
}

Write-Host "Vulnerability audit passed for $(@($report.projects).Count) project(s); no vulnerable packages were reported."
