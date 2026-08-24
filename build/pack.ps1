<#
  Packs the published app into a Velopack release (Setup.exe + full/delta
  nupkg + portable zip + RELEASES manifest) under releases/.

  Version comes from ONE place: <Version> in src/MarkLite/MarkLite.csproj.
  Requires the vpk global tool (dotnet tool install -g vpk).

  -SkipPublish packs whatever is already in publish/ (faster iteration);
  by default a fresh AOT publish runs first.
#>
param(
    [switch]$SkipPublish,
    [string]$OutputDir
)
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repoRoot 'src\MarkLite\MarkLite.csproj'
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'releases' }

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $csproj" }

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1')
}

if (-not (Test-Path (Join-Path $repoRoot 'publish\MarkLite.exe'))) {
    throw "publish\MarkLite.exe not found - run build\publish.ps1 first"
}

# Icon is optional until the logo lands; vpk falls back to its default.
$iconArgs = @()
$icon = Join-Path $repoRoot 'assets\MarkLite.ico'
if (Test-Path $icon) { $iconArgs = @('--icon', $icon) }

vpk pack `
    --packId MarkLite `
    --packVersion $version `
    --packDir (Join-Path $repoRoot 'publish') `
    --mainExe MarkLite.exe `
    --packTitle MarkLite `
    --packAuthors Pneutronix `
    --outputDir $OutputDir `
    --shortcuts StartMenuRoot `
    @iconArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }

Write-Host "Packed MarkLite $version into $OutputDir"
