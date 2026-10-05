[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$buildDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = Split-Path -Parent $buildDirectory
$sourceRoot = Join-Path $repositoryRoot 'src'
$manifestPath = Join-Path $repositoryRoot 'keelmatrix.website.json'
$expectedRepositoryUrl = 'https://github.com/KeelMatrix/LogLeak'
$allowedVisibility = @('public-product', 'internal-package')
$allowedRoles = @('primary', 'component')
$knownWebsiteTags = @(
    'keelmatrix-public-product',
    'keelmatrix-internal-package',
    'keelmatrix-primary',
    'keelmatrix-component'
)

function Fail-WebsiteMetadata {
    param([Parameter(Mandatory)][string] $Message)

    throw "Website metadata contract failed: $Message"
}

function Assert-ExactPropertyNames {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary] $Object,
        [Parameter(Mandatory)][string[]] $Expected,
        [Parameter(Mandatory)][string] $Context
    )

    $actual = @($Object.Keys | ForEach-Object { [string] $_ })
    $missing = @($Expected | Where-Object { $actual -cnotcontains $_ })
    $unexpected = @($actual | Where-Object { $Expected -cnotcontains $_ })
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0 -or $actual.Count -ne $Expected.Count) {
        Fail-WebsiteMetadata "$Context properties differ. Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ')."
    }
}

function Get-ProjectPropertyNode {
    param(
        [Parameter(Mandatory)][xml] $Project,
        [Parameter(Mandatory)][string] $Name
    )

    return $Project.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$Name']")
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Fail-WebsiteMetadata "root manifest '$manifestPath' is missing."
}

try {
    $manifest = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($manifestPath)) -AsHashtable -ErrorAction Stop
}
catch {
    Fail-WebsiteMetadata "root manifest is not valid JSON: $($_.Exception.Message)"
}

if ($manifest -isnot [System.Collections.IDictionary]) {
    Fail-WebsiteMetadata 'root JSON value must be an object.'
}
Assert-ExactPropertyNames $manifest @('schemaVersion', 'packages') 'root manifest'
$schemaVersion = $manifest['schemaVersion']
if ((($schemaVersion -isnot [int]) -and ($schemaVersion -isnot [long])) -or $schemaVersion -ne 1) {
    Fail-WebsiteMetadata 'schemaVersion must be the JSON integer 1.'
}
$manifestPackages = $manifest['packages']
if ($manifestPackages -isnot [System.Collections.IDictionary] -or $manifestPackages.Count -eq 0) {
    Fail-WebsiteMetadata 'packages must be a non-empty object.'
}

$projectFiles = @(
    Get-ChildItem -LiteralPath $sourceRoot -Filter '*.csproj' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
)
if ($projectFiles.Count -eq 0) {
    Fail-WebsiteMetadata "no shipping package projects were found under '$sourceRoot'."
}

$sourcePackages = @()
foreach ($projectFile in $projectFiles) {
    try {
        [xml] $project = [System.IO.File]::ReadAllText($projectFile.FullName)
    }
    catch {
        Fail-WebsiteMetadata "project '$($projectFile.FullName)' is not valid XML: $($_.Exception.Message)"
    }

    $isPackableNode = Get-ProjectPropertyNode $project 'IsPackable'
    if ($null -ne $isPackableNode -and $isPackableNode.InnerText.Trim() -ieq 'false') {
        continue
    }

    $packageIdNode = Get-ProjectPropertyNode $project 'PackageId'
    if ($null -eq $packageIdNode -or [string]::IsNullOrWhiteSpace($packageIdNode.InnerText)) {
        Fail-WebsiteMetadata "packable project '$($projectFile.Name)' must define PackageId."
    }
    $packageId = $packageIdNode.InnerText.Trim()

    foreach ($other in $sourcePackages) {
        if ($other.PackageId -ceq $packageId) {
            Fail-WebsiteMetadata "PackageId '$packageId' is declared by more than one packable source project."
        }
    }

    $manifestIds = @($manifestPackages.Keys | ForEach-Object { [string] $_ })
    $matchingManifestIds = @($manifestIds | Where-Object { $_ -ceq $packageId })
    if ($matchingManifestIds.Count -ne 1) {
        Fail-WebsiteMetadata "packable package '$packageId' must have one exact-case manifest entry."
    }
    $manifestEntry = $manifestPackages[$matchingManifestIds[0]]
    if ($manifestEntry -isnot [System.Collections.IDictionary]) {
        Fail-WebsiteMetadata "manifest entry for '$packageId' must be an object."
    }
    Assert-ExactPropertyNames $manifestEntry @('visibility', 'role') "manifest entry '$packageId'"

    $visibility = [string] $manifestEntry['visibility']
    $role = [string] $manifestEntry['role']
    if ($allowedVisibility -cnotcontains $visibility) {
        Fail-WebsiteMetadata "manifest entry '$packageId' has unsupported visibility '$visibility'."
    }
    if ($allowedRoles -cnotcontains $role) {
        Fail-WebsiteMetadata "manifest entry '$packageId' has unsupported role '$role'."
    }

    $tagsNode = Get-ProjectPropertyNode $project 'PackageTags'
    if ($null -eq $tagsNode) {
        Fail-WebsiteMetadata "packable project '$packageId' must define PackageTags."
    }
    $tagTokens = @([regex]::Split($tagsNode.InnerText.Trim(), '[;\s,]+') | Where-Object { $_ -ne '' })
    $websiteTags = @($tagTokens | Where-Object { $_.StartsWith('keelmatrix-', [System.StringComparison]::OrdinalIgnoreCase) })
    $unknownWebsiteTags = @($websiteTags | Where-Object { $knownWebsiteTags -cnotcontains $_ })
    if ($unknownWebsiteTags.Count -gt 0) {
        Fail-WebsiteMetadata "project '$packageId' contains unrecognized website sentinel tag(s): $($unknownWebsiteTags -join ', ')."
    }

    $visibilityTags = @($tagTokens | Where-Object { $_ -match '^keelmatrix-(public-product|internal-package)$' })
    if ($visibilityTags.Count -ne 1) {
        Fail-WebsiteMetadata "project '$packageId' must contain exactly one website visibility sentinel tag."
    }
    $expectedVisibilityTag = switch ($visibility) {
        'public-product' { 'keelmatrix-public-product' }
        'internal-package' { 'keelmatrix-internal-package' }
    }
    if ($visibilityTags[0] -cne $expectedVisibilityTag) {
        Fail-WebsiteMetadata "project '$packageId' visibility tag '$($visibilityTags[0])' disagrees with manifest visibility '$visibility'."
    }

    $roleTags = @($tagTokens | Where-Object { $_ -match '^keelmatrix-(primary|component)$' })
    if ($roleTags.Count -ne 1) {
        Fail-WebsiteMetadata "project '$packageId' must contain exactly one website artifact-role sentinel tag."
    }
    $expectedRoleTag = switch ($role) {
        'primary' { 'keelmatrix-primary' }
        'component' { 'keelmatrix-component' }
    }
    if ($roleTags[0] -cne $expectedRoleTag) {
        Fail-WebsiteMetadata "project '$packageId' role tag '$($roleTags[0])' disagrees with manifest role '$role'."
    }

    $projectUrlNode = Get-ProjectPropertyNode $project 'PackageProjectUrl'
    $repositoryUrlNode = Get-ProjectPropertyNode $project 'RepositoryUrl'
    $repositoryTypeNode = Get-ProjectPropertyNode $project 'RepositoryType'
    if ($null -eq $projectUrlNode -or $projectUrlNode.InnerText.Trim() -cne $expectedRepositoryUrl) {
        Fail-WebsiteMetadata "project '$packageId' PackageProjectUrl must be '$expectedRepositoryUrl'."
    }
    if ($null -eq $repositoryUrlNode -or $repositoryUrlNode.InnerText.Trim() -cne $expectedRepositoryUrl) {
        Fail-WebsiteMetadata "project '$packageId' RepositoryUrl must be '$expectedRepositoryUrl'."
    }
    if ($null -eq $repositoryTypeNode -or $repositoryTypeNode.InnerText.Trim() -cne 'git') {
        Fail-WebsiteMetadata "project '$packageId' RepositoryType must be 'git'."
    }

    $descriptionNode = Get-ProjectPropertyNode $project 'Description'
    if ($null -eq $descriptionNode -or [string]::IsNullOrWhiteSpace($descriptionNode.InnerText)) {
        Fail-WebsiteMetadata "packable project '$packageId' must define a non-empty Description."
    }
    if ($null -ne (Get-ProjectPropertyNode $project 'PackageType')) {
        Fail-WebsiteMetadata "project '$packageId' must retain NuGet's default Dependency package type without an explicit PackageType."
    }

    $sourcePackages += [pscustomobject]@{
        PackageId = $packageId
        Visibility = $visibility
        Role = $role
        RepositoryUrl = $repositoryUrlNode.InnerText.Trim()
    }
}

if ($sourcePackages.Count -eq 0) {
    Fail-WebsiteMetadata 'no packable package projects were found under src.'
}

$manifestIds = @($manifestPackages.Keys | ForEach-Object { [string] $_ })
$sourceIds = @($sourcePackages | ForEach-Object { $_.PackageId })
$unownedManifestIds = @($manifestIds | Where-Object { $sourceIds -cnotcontains $_ })
if ($unownedManifestIds.Count -gt 0 -or $manifestIds.Count -ne $sourceIds.Count) {
    Fail-WebsiteMetadata "manifest package set differs from packable source projects. Unmatched entries: $($unownedManifestIds -join ', ')."
}

$publicFamilies = @($sourcePackages | Where-Object { $_.Visibility -ceq 'public-product' } | Group-Object -Property RepositoryUrl)
foreach ($family in $publicFamilies) {
    $primaryCount = @($family.Group | Where-Object { $_.Role -ceq 'primary' }).Count
    if ($primaryCount -ne 1) {
        Fail-WebsiteMetadata "public product family '$($family.Name)' must have exactly one primary package; found $primaryCount."
    }
}

Write-Host "Website metadata contract passed for $($sourcePackages.Count) packable package(s); source sentinels agree with the root manifest."
