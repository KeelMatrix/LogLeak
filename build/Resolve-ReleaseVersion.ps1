[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $Tag = $env:GITHUB_REF_NAME,
    [string] $RefType = $env:GITHUB_REF_TYPE,
    [string] $ExpectedCommit = $env:GITHUB_SHA,
    [string] $OutputPath = $env:GITHUB_OUTPUT,
    [string] $TagCommitOverride,
    [string] $CommitDateOverride
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail-Resolution {
    param([Parameter(Mandatory)][string] $Message)

    throw "Release version resolution failed: $Message"
}

if ($RefType -ne 'tag') {
    Fail-Resolution "release workflow requires a tag ref, got '$RefType'."
}

$tagMatch = [regex]::Match(
    $Tag,
    '^v(?<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$')
if (-not $tagMatch.Success) {
    Fail-Resolution "malformed release tag '$Tag'. Expected vX.Y.Z."
}

$version = $tagMatch.Groups['version'].Value
if ([string]::IsNullOrWhiteSpace($version)) {
    Fail-Resolution 'the release tag produced an empty version.'
}

$tagCommit = if ([string]::IsNullOrWhiteSpace($TagCommitOverride)) {
    $tagCommitOutput = (& git -C $RepositoryRoot rev-parse --verify "refs/tags/$Tag^{commit}" 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        Fail-Resolution "could not resolve tag '$Tag' to a commit."
    }

    $tagCommitOutput
}
else {
    $TagCommitOverride.Trim()
}

if ([string]::IsNullOrWhiteSpace($tagCommit)) {
    Fail-Resolution "tag '$Tag' resolved to an empty commit."
}
if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    Fail-Resolution 'the checked-out commit is empty.'
}
if ($tagCommit -ne $ExpectedCommit.Trim()) {
    Fail-Resolution "tag '$Tag' resolves to commit '$tagCommit', but the checked-out commit is '$ExpectedCommit'."
}

$commitDate = if ([string]::IsNullOrWhiteSpace($CommitDateOverride)) {
    $commitDateOutput = (& git -C $RepositoryRoot show -s --format=%cs $tagCommit 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        Fail-Resolution "could not derive the commit date for tag '$Tag'."
    }

    $commitDateOutput
}
else {
    $CommitDateOverride.Trim()
}

if ($commitDate -notmatch '^\d{4}-\d{2}-\d{2}$') {
    Fail-Resolution "could not derive an ISO commit date for '$Tag'."
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Fail-Resolution 'the GitHub output path is empty.'
}

@(
    "version=$version"
    "release-date=$commitDate"
) | Out-File -FilePath $OutputPath -Encoding utf8 -Append

Write-Host "Resolved release version $version from $Tag (commit date $commitDate)."
