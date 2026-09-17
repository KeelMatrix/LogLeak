[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $Version,
    [string] $Tag,
    [string] $ExpectedReleaseDate,
    [ValidateSet('FirstPublic', 'Subsequent')]
    [string] $ReleaseKind = 'FirstPublic'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail-Contract {
    param([Parameter(Mandatory)][string] $Message)

    throw "Release contract failed: $Message"
}

function Get-RequiredFile {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail-Contract "required file '$Path' was not found."
    }

    return $Path
}

function Get-XmlDocument {
    param([Parameter(Mandatory)][string] $Path)

    try {
        return [xml](Get-Content -LiteralPath (Get-RequiredFile $Path) -Raw)
    }
    catch {
        Fail-Contract "could not parse XML file '$Path': $($_.Exception.Message)"
    }
}

function Get-ExactVersion {
    param(
        [Parameter(Mandatory)][string] $Value,
        [Parameter(Mandatory)][string] $Description
    )

    $candidate = $Value.Trim()
    if ($candidate -match '^\[(?<version>\d+\.\d+\.\d+)\]$') {
        $candidate = $Matches.version
    }

    if ($candidate -notmatch '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
        Fail-Contract "$Description uses non-exact version '$Value'."
    }

    return $candidate
}

function Get-PropertyDefinitions {
    param([Parameter(Mandatory)][xml] $Document)

    $definitions = @{}
    foreach ($node in @($Document.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*"))) {
        $name = $node.LocalName
        if ($definitions.ContainsKey($name)) {
            $definitions[$name] = @($definitions[$name]) + $node.InnerText
        }
        else {
            $definitions[$name] = @($node.InnerText)
        }
    }

    return $definitions
}

function Resolve-PropertyValue {
    param(
        [Parameter(Mandatory)][string] $Value,
        [Parameter(Mandatory)][hashtable] $Definitions,
        [Parameter(Mandatory)][string] $Description,
        [string[]] $Resolving = @()
    )

    $candidate = $Value.Trim()
    if ($candidate -match '^\$\((?<name>[A-Za-z][A-Za-z0-9_.-]*)\)$') {
        $name = $Matches.name
        if ($Resolving -contains $name) {
            Fail-Contract "$Description contains a property-reference cycle involving '$name'."
        }
        if (-not $Definitions.ContainsKey($name)) {
            Fail-Contract "$Description references undefined property '$name'."
        }

        $values = @($Definitions[$name])
        if ($values.Count -ne 1) {
            Fail-Contract "$Description references property '$name', which has $($values.Count) definitions."
        }

        return Resolve-PropertyValue -Value ([string] $values[0]) -Definitions $Definitions -Description $Description -Resolving ($Resolving + $name)
    }

    if ($candidate -match '\$\(') {
        Fail-Contract "$Description contains an unresolved property reference '$candidate'."
    }

    return $candidate
}

function Get-ReleaseEntry {
    param(
        [string[]] $Lines,
        [Parameter(Mandatory)][string] $ReleaseVersion
    )

    $escapedVersion = [regex]::Escape($ReleaseVersion)
    $releaseMatches = @(
        for ($index = 0; $index -lt $Lines.Count; $index++) {
            if ($Lines[$index] -match "^\s*##\s+\[$escapedVersion\]\s+-\s+(?<date>\d{4}-\d{2}-\d{2})\s*$") {
                [pscustomobject]@{
                    Index = $index
                    Date = $Matches.date
                }
            }
        }
    )

    if ($releaseMatches.Count -eq 0) {
        Fail-Contract "CHANGELOG.md has no finalized [$ReleaseVersion] entry with an ISO release date; the target may still be under Unreleased."
    }
    if ($releaseMatches.Count -ne 1) {
        Fail-Contract "CHANGELOG.md contains $($releaseMatches.Count) finalized [$ReleaseVersion] entries; expected exactly one."
    }

    $releaseStart = $releaseMatches[0].Index
    $releaseEnd = $Lines.Count
    for ($index = $releaseStart + 1; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -match '^\s*##\s+') {
            $releaseEnd = $index
            break
        }
    }

    $unreleasedMatches = @(
        for ($index = 0; $index -lt $Lines.Count; $index++) {
            if ($Lines[$index] -match '^\s*##\s+\[Unreleased\]\s*$') {
                $index
            }
        }
    )
    if ($unreleasedMatches.Count -gt 1) {
        Fail-Contract 'CHANGELOG.md contains more than one Unreleased section.'
    }
    if ($unreleasedMatches.Count -eq 1 -and $unreleasedMatches[0] -ge $releaseStart -and $unreleasedMatches[0] -lt $releaseEnd) {
        Fail-Contract "[$ReleaseVersion] is still nested under Unreleased."
    }

    return [pscustomobject]@{
        Start = $releaseStart
        End = $releaseEnd
        Date = $releaseMatches[0].Date
        Lines = if ($releaseEnd -gt ($releaseStart + 1)) {
            @($Lines[($releaseStart + 1)..($releaseEnd - 1)])
        }
        else {
            @()
        }
    }
}

function Assert-ReleaseDate {
    param(
        [Parameter(Mandatory)][string] $Date,
        [string] $ExpectedDate
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedDate)) {
        Fail-Contract 'an expected release date is required; pass -ExpectedReleaseDate YYYY-MM-DD.'
    }

    try {
        [void][DateTime]::ParseExact($Date, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        Fail-Contract "release date '$Date' is not a valid ISO date."
    }

    try {
        $parsedExpected = [DateTime]::ParseExact($ExpectedDate.Trim(), 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        Fail-Contract "expected release date '$ExpectedDate' is not a valid ISO date."
    }

    if ($Date -ne $parsedExpected.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)) {
        Fail-Contract "CHANGELOG.md release date is '$Date', expected '$ExpectedDate'."
    }
}

function Assert-NoPreReleaseWording {
    param([Parameter(Mandatory)][string] $EntryText)

    $preReleaseMarkers = @(
        'planned',
        'not\s+yet\s+published',
        'tbd',
        'unreleased',
        'pending',
        'forthcoming',
        'pre-release'
    )
    foreach ($marker in $preReleaseMarkers) {
        if ($EntryText -match "(?i)\b$marker\b") {
            Fail-Contract "the released entry contains pre-release wording matching '$marker'."
        }
    }
}

function Assert-FirstReleaseEntry {
    param([string[]] $EntryLines)

    $entryText = $EntryLines -join "`n"
    $categoryMatches = @([regex]::Matches($entryText, '(?m)^\s*###\s+(?<category>[^\r\n]+)\s*$'))
    if ($categoryMatches.Count -ne 1 -or $categoryMatches[0].Groups['category'].Value.Trim() -ne 'Added') {
        $categories = @($categoryMatches | ForEach-Object { $_.Groups['category'].Value.Trim() })
        Fail-Contract "the first public release entry must contain only an Added section; found '$($categories -join ', ')'."
    }

    if ($entryText -notmatch '(?m)^\s*[-*+]\s+\S') {
        Fail-Contract 'the first public release Added section must contain at least one bullet.'
    }

    $remediationMarkers = @(
        'now',
        'no\s+longer',
        'previously',
        'formerly',
        'used\s+to',
        'fixed',
        'fixes',
        'corrected',
        'resolved',
        'addressed',
        'this\s+removes',
        'this\s+fixes',
        'changed\s+from'
    )
    foreach ($marker in $remediationMarkers) {
        if ($entryText -match "(?i)\b$marker\b") {
            Fail-Contract "the first public release entry contains remediation-history wording matching '$marker'."
        }
    }
}

function Assert-SourcePackageVersion {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $ReleaseVersion
    )

    $propsPath = Join-Path $Root 'Directory.Build.props'
    $projectPath = Join-Path $Root 'src/KeelMatrix.LogLeak/KeelMatrix.LogLeak.csproj'
    $props = Get-XmlDocument $propsPath
    $project = Get-XmlDocument $projectPath

    $definitions = Get-PropertyDefinitions $props
    $projectDefinitions = Get-PropertyDefinitions $project
    foreach ($name in $projectDefinitions.Keys) {
        $definitions[$name] = $projectDefinitions[$name]
    }
    $versionNodes = @(
        $props.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='Version' or local-name()='PackageVersion' or local-name()='VersionPrefix']")
        $project.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='Version' or local-name()='PackageVersion' or local-name()='VersionPrefix']")
    )
    if ($versionNodes.Count -eq 0) {
        Fail-Contract "'$propsPath' does not declare a package version."
    }
    foreach ($node in $versionNodes) {
        $resolvedValue = Resolve-PropertyValue -Value $node.InnerText -Definitions $definitions -Description "source property '$($node.LocalName)'"
        $sourceVersion = Get-ExactVersion $resolvedValue "source property '$($node.LocalName)'"
        if ($sourceVersion -ne $ReleaseVersion) {
            Fail-Contract "source property '$($node.LocalName)' is '$sourceVersion', expected '$ReleaseVersion'."
        }
    }

    $packageIdNode = $project.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='PackageId']")
    if ($null -eq $packageIdNode -or $packageIdNode.InnerText.Trim() -ne 'KeelMatrix.LogLeak') {
        Fail-Contract "shipping project PackageId must be 'KeelMatrix.LogLeak'."
    }
}

function Assert-DependencyVersions {
    param([Parameter(Mandatory)][string] $Root)

    $projectPath = Join-Path $Root 'src/KeelMatrix.LogLeak/KeelMatrix.LogLeak.csproj'
    $centralPath = Join-Path $Root 'Directory.Packages.props'
    $project = Get-XmlDocument $projectPath
    $central = Get-XmlDocument $centralPath

    $centralVersions = @{}
    foreach ($node in @($central.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageVersion']"))) {
        $id = $node.GetAttribute('Include')
        $version = $node.GetAttribute('Version')
        if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) {
            Fail-Contract "central package version entries must define exact Include and Version attributes."
        }
        if ($centralVersions.ContainsKey($id)) {
            Fail-Contract "central package '$id' is declared more than once."
        }
        $centralVersions[$id] = Get-ExactVersion $version "central package '$id'"
    }

    $references = @($project.SelectNodes("/*[local-name()='Project']//*[local-name()='PackageReference']"))
    if ($references.Count -eq 0) {
        Fail-Contract 'shipping project has no package references to validate.'
    }
    foreach ($reference in $references) {
        $id = $reference.GetAttribute('Include')
        if ([string]::IsNullOrWhiteSpace($id)) {
            Fail-Contract 'a shipping package reference has no package ID.'
        }
        $explicitVersions = @()
        foreach ($attributeName in @('Version', 'VersionOverride')) {
            $attributeValue = $reference.GetAttribute($attributeName)
            if (-not [string]::IsNullOrWhiteSpace($attributeValue)) {
                $explicitVersions += $attributeValue
            }
        }
        $childVersion = $reference.SelectSingleNode("./*[local-name()='Version']")
        if ($null -ne $childVersion -and -not [string]::IsNullOrWhiteSpace($childVersion.InnerText)) {
            $explicitVersions += $childVersion.InnerText
        }

        if ($explicitVersions.Count -gt 1) {
            Fail-Contract "package reference '$id' declares more than one explicit version."
        }
        if ($explicitVersions.Count -eq 1) {
            $explicitVersion = Get-ExactVersion $explicitVersions[0] "package reference '$id'"
            if ($centralVersions.ContainsKey($id) -and $centralVersions[$id] -ne $explicitVersion) {
                Fail-Contract "package reference '$id' uses '$explicitVersion', but central management declares '$($centralVersions[$id])'."
            }
            continue
        }
        if (-not $centralVersions.ContainsKey($id)) {
            Fail-Contract "package reference '$id' has no exact central version."
        }
    }
}

function Assert-InstallExamples {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $ReleaseVersion
    )

    $markdownFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.md' | Where-Object {
        $_.FullName -notmatch '(?i)(\\|/)(\.git|artifacts|bin|obj)(\\|/)'
    })
    $examples = @(
        foreach ($file in $markdownFiles) {
            foreach ($line in (Get-Content -LiteralPath $file.FullName)) {
                if ($line -match '(?i)KeelMatrix\.LogLeak' -and $line -match '(?i)(dotnet\s+add\s+package|PackageReference|PackageVersion)') {
                    [pscustomobject]@{ Path = $file.FullName; Text = $line }
                }
            }
        }
    )
    if ($examples.Count -eq 0) {
        Fail-Contract 'no developer-facing KeelMatrix.LogLeak install example was found.'
    }

    foreach ($example in $examples) {
        $versionMatches = @([regex]::Matches($example.Text, '(?i)(?:--version\s+|Version\s*=\s*["'']|Version\s*:\s*)\[?(?<version>\d+\.\d+\.\d+)\]?'))
        foreach ($versionMatch in $versionMatches) {
            $exampleVersion = $versionMatch.Groups['version'].Value
            if ($exampleVersion -ne $ReleaseVersion) {
                Fail-Contract "install example in '$($example.Path)' uses '$exampleVersion', expected '$ReleaseVersion'."
            }
        }
    }
}

try {
    $root = (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path

    if ($null -ne $Tag -and $Tag.Trim() -ne '') {
        if ($Tag -notmatch '^v(?<tagVersion>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$') {
            Fail-Contract "tag '$Tag' is malformed; expected vX.Y.Z."
        }
        $tagVersion = $Matches.tagVersion
        if ($null -ne $Version -and $Version.Trim() -ne '' -and $Version.Trim() -ne $tagVersion) {
            Fail-Contract "tag '$Tag' resolves to '$tagVersion', but requested version is '$Version'."
        }
        $Version = $tagVersion
    }

    if ($null -eq $Version -or $Version.Trim() -eq '') {
        Fail-Contract 'a target release version or a vX.Y.Z tag is required.'
    }
    $Version = Get-ExactVersion $Version 'target release version'

    $changelogPath = Join-Path $root 'CHANGELOG.md'
    $changelogLines = @(Get-Content -LiteralPath (Get-RequiredFile $changelogPath))
    $entry = Get-ReleaseEntry $changelogLines $Version
    Assert-ReleaseDate $entry.Date $ExpectedReleaseDate
    Assert-NoPreReleaseWording ($entry.Lines -join "`n")

    if ($ReleaseKind -eq 'FirstPublic') {
        Assert-FirstReleaseEntry $entry.Lines
    }

    Assert-SourcePackageVersion $root $Version
    Assert-DependencyVersions $root
    Assert-InstallExamples $root $Version

    $tagDescription = if ($null -ne $Tag -and $Tag.Trim() -ne '') { ", tag $Tag" } else { '' }
    Write-Host "Release contract passed for KeelMatrix.LogLeak $Version$tagDescription (changelog date $($entry.Date); $ReleaseKind release)."
}
catch {
    $message = $_.Exception.Message
    if ($message -notlike 'Release contract failed: *') {
        $message = "Release contract failed: unexpected validation error: $message"
    }
    Write-Error $message
    exit 1
}
