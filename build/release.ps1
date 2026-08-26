<#
  Uploads the packed release in releases/ to GitHub Releases
  (Setup.exe + full/delta nupkg + portable zip + RELEASES manifest).

  Run build\pack.ps1 first. Requires a GitHub token with repo scope:
  set PneutronixJeremy_Github_Token (GITHUB_TOKEN works as a fallback, e.g.
  in CI), or pass -Token. -Draft leaves the release unpublished for a manual
  review on github.com before it goes live.

  Release notes come from docs\release-notes\v<version>.md (override with
  -NotesFile, skip with -NoNotes). vpk itself has no notes option, so the body
  is written afterwards over the GitHub API with the same token.
#>
param(
    [string]$Token = ($env:PneutronixJeremy_Github_Token ?? $env:GITHUB_TOKEN),
    [string]$NotesFile,
    [switch]$NoNotes,
    [switch]$Draft
)
$ErrorActionPreference = 'Stop'

$repoUrl = 'https://github.com/PneutronixJeremy/MarkLite'
$repoRoot = Split-Path -Parent $PSScriptRoot
$releases = Join-Path $repoRoot 'releases'
if (-not (Test-Path (Join-Path $releases 'RELEASES'))) {
    throw "No packed release found in releases\ - run build\pack.ps1 first"
}
if (-not $Token) {
    throw 'GitHub token required: set PneutronixJeremy_Github_Token (or GITHUB_TOKEN), or pass -Token'
}

$version = ([xml](Get-Content (Join-Path $repoRoot 'src\MarkLite\MarkLite.csproj'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1

<#  Notes are resolved BEFORE the upload: a missing file should stop the run
    while nothing has been published yet, not after the release is live with an
    empty body. #>
$notes = $null
if (-not $NoNotes) {
    if (-not $NotesFile) {
        $NotesFile = Join-Path $repoRoot "docs\release-notes\v$version.md"
    }
    if (-not (Test-Path -LiteralPath $NotesFile)) {
        throw "Release notes not found: $NotesFile - write them, pass -NotesFile, or use -NoNotes"
    }
    $notes = (Get-Content -LiteralPath $NotesFile -Raw).Trim()
    if (-not $notes) {
        throw "Release notes file is empty: $NotesFile"
    }
}

$publishArgs = if ($Draft) { @() } else { @('--publish', 'true') }
vpk upload github `
    --outputDir $releases `
    --repoUrl $repoUrl `
    --token $Token `
    --tag "v$version" `
    --releaseName "MarkLite $version" `
    @publishArgs
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed with exit code $LASTEXITCODE" }

if ($notes) {
    $headers = @{
        Authorization          = "Bearer $Token"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'MarkLite-release'
    }
    $slug = ([uri]$repoUrl).AbsolutePath.Trim('/')

    <#  By tag_name over the release LIST, not /releases/tags/<tag>: that
        endpoint does not resolve drafts, which is exactly what -Draft leaves
        behind. The list includes them for an authenticated caller. #>
    $all = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug/releases?per_page=100" -Headers $headers
    $release = $all | Where-Object { $_.tag_name -eq "v$version" } | Select-Object -First 1
    if (-not $release) {
        throw "Uploaded, but no release tagged v$version came back from the API - set the notes by hand"
    }

    $body = @{ body = $notes } | ConvertTo-Json -Compress
    [void](Invoke-RestMethod -Method Patch -Uri $release.url -Headers $headers -Body $body -ContentType 'application/json')
    Write-Host "Release notes written from $NotesFile"
}

Write-Host "Uploaded MarkLite $version to GitHub Releases$(if ($Draft) { ' (draft)' })"
