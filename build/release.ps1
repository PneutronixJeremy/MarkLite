<#
  Uploads the packed release in releases/ to GitHub Releases
  (Setup.exe + full/delta nupkg + portable zip + RELEASES manifest).

  Run build\pack.ps1 first. Requires a GitHub token with repo scope:
  set GITHUB_TOKEN, or pass -Token. -Draft leaves the release unpublished
  for a manual review on github.com before it goes live.
#>
param(
    [string]$Token = $env:GITHUB_TOKEN,
    [switch]$Draft
)
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$releases = Join-Path $repoRoot 'releases'
if (-not (Test-Path (Join-Path $releases 'RELEASES'))) {
    throw "No packed release found in releases\ - run build\pack.ps1 first"
}
if (-not $Token) {
    throw 'GitHub token required: set GITHUB_TOKEN or pass -Token'
}

$version = ([xml](Get-Content (Join-Path $repoRoot 'src\MarkLite\MarkLite.csproj'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1

$publishArgs = if ($Draft) { @() } else { @('--publish', 'true') }
vpk upload github `
    --outputDir $releases `
    --repoUrl 'https://github.com/PneutronixJeremy/MarkLite' `
    --token $Token `
    --tag "v$version" `
    --releaseName "MarkLite $version" `
    @publishArgs
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed with exit code $LASTEXITCODE" }

Write-Host "Uploaded MarkLite $version to GitHub Releases$(if ($Draft) { ' (draft)' })"
