<#
.SYNOPSIS
    Runs every verification script against one MarkLite build and tabulates the
    results.

.DESCRIPTION
    Each script launches the app itself, so they run one at a time: they share a
    single-instance group, a window position and the debug log directory, and
    two at once would answer each other's commands.

    Every script exits 0 when all its assertions pass and non-zero otherwise, so
    the table below is exit codes, not parsed output. A script that throws
    (a crash, a missing fixture) counts as a failure like any other.

    NOTE: test-selection.ps1 uses the clipboard. It saves the text contents at
    the start and puts them back at the end, but a clipboard manager will see
    the round trip.

.PARAMETER Exe
    MarkLite.exe to check. Defaults to publish/MarkLite.exe (build/publish.ps1).
    Point it at an unzipped portable build to smoke-test a package.

.PARAMETER CaptureDir
    Where the scripts that capture the window write their PNGs. Defaults to
    the temp directory, as the scripts themselves do.

.EXAMPLE
    pwsh -NoProfile -File tools/verify/run-all.ps1

.EXAMPLE
    pwsh -NoProfile -File tools/verify/run-all.ps1 -Exe ../portable/MarkLite.exe
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$CaptureDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (git rev-parse --show-toplevel)
if (-not $Exe) {
    $Exe = Join-Path $repoRoot 'publish/MarkLite.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host "run-all: no exe at $Exe - run build/publish.ps1 first" -ForegroundColor Red
    exit 1
}
$Exe = (Resolve-Path -LiteralPath $Exe).Path

<#  Order is deliberate rather than alphabetical: the cheap structural checks
    come first, so a build that is broken outright fails in seconds instead of
    after the long scroll-through in test-virtual. #>
$scripts = @(
    'test-tabs.ps1'
    'test-html-comments.ps1'
    'test-virtual.ps1'
    'test-toc-search.ps1'
    'test-gutter.ps1'
    'test-reload.ps1'
    'test-session.ps1'
    'test-selection.ps1'
)

Write-Host ''
Write-Host "== run-all: $Exe" -ForegroundColor Cyan

$results = @()
foreach ($name in $scripts) {
    $path = Join-Path $PSScriptRoot $name
    $arguments = @('-NoProfile', '-File', $path, '-Exe', $Exe)
    #  Only the scripts that take -CaptureDir get it; the rest would reject it.
    if ($CaptureDir -and (Select-String -LiteralPath $path -Pattern '\$CaptureDir' -Quiet)) {
        $arguments += @('-CaptureDir', $CaptureDir)
    }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    & pwsh @arguments
    $code = $LASTEXITCODE
    $timer.Stop()

    $results += [pscustomobject]@{
        Script  = $name
        Result  = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
        Seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 1)
    }
}

Write-Host ''
Write-Host '== run-all summary' -ForegroundColor Cyan
Write-Host ''
Write-Host ('| {0,-24} | {1,-6} | {2,7} |' -f 'Script', 'Result', 'Seconds')
Write-Host ('|{0}|{1}|{2}|' -f ('-' * 26), ('-' * 8), ('-' * 9))
foreach ($result in $results) {
    $colour = if ($result.Result -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host ('| {0,-24} | {1,-6} | {2,7} |' -f $result.Script, $result.Result, $result.Seconds) `
        -ForegroundColor $colour
}

$failed = @($results | Where-Object { $_.Result -eq 'FAIL' })
Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "run-all: ALL PASS ($($results.Count) scripts)" -ForegroundColor Green
    exit 0
}
Write-Host "run-all: $($failed.Count) of $($results.Count) scripts FAILED" -ForegroundColor Red
exit 1
