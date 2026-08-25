<#
.SYNOPSIS
    Line-number gutter: the numbers are the document's real source lines, and
    turning them on moves nothing.

.DESCRIPTION
    The gutter's strip is reserved on both sides of the document whether the
    numbers are showing or not, so the toggle is meant to be free: no reflow, no
    rebuilt blocks, no moved text. That is the claim this script holds it to,
    with a full-resolution capture diff between the two states — every differing
    pixel has to fall inside one strip's width.

    The numbers themselves are checked against the source file rather than
    against a screenshot: `dump-state` reports the first and last visible source
    line and the line of the last block jumped to, so a heading jump can be
    confirmed to have landed on a line that really is a heading in the file.

    Requires MARKLITE_VIRTUAL=1; the script sets it for the app it launches. The
    classic viewer has no gutter (it goes away at the cutover).

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).

.PARAMETER File
    Document to open. Defaults to testdata/sample-plan.md.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$File,
    [string]$CaptureDir
)

. "$PSScriptRoot/common.ps1"

if ($Exe) {
    Set-MarkLiteExe $Exe
}
if (-not $File) {
    $File = Join-Path (git rev-parse --show-toplevel) 'testdata/sample-plan.md'
}
if (-not $CaptureDir) {
    $CaptureDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-verify/captures'
}

$env:MARKLITE_VIRTUAL = '1'

$sourceLines = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $File).Path)
$lineCount = $sourceLines.Count

<#  Is this source line the start of a heading? ATX is the line itself; setext
    is a line of text underlined by === or ---. Both are headings MarkLite puts
    in its contents list, so both are legitimate jump targets. #>
function Test-HeadingLine {
    param([int]$Line)

    if ($Line -lt 1 -or $Line -gt $sourceLines.Count) {
        return $false
    }
    $text = $sourceLines[$Line - 1]
    if ($text -match '^#{1,6}\s') {
        return $true
    }
    if ($Line -lt $sourceLines.Count -and $text.Trim() -ne '') {
        return ($sourceLines[$Line] -match '^(=+|-+)\s*$')
    }
    return $false
}

<#  Counts differing pixels between two captures and reports how wide a band
    they fall in. The whole point of the design is that the band is one gutter
    strip and nothing else. #>
function Measure-CaptureBand {
    param([string]$First, [string]$Second)

    Add-Type -AssemblyName System.Drawing
    $a = [System.Drawing.Bitmap]::FromFile($First)
    $b = [System.Drawing.Bitmap]::FromFile($Second)
    try {
        $width = [Math]::Min($a.Width, $b.Width)
        $height = [Math]::Min($a.Height, $b.Height)
        $columns = [System.Collections.Generic.HashSet[int]]::new()
        $total = 0
        for ($y = 0; $y -lt $height; $y++) {
            for ($x = 0; $x -lt $width; $x++) {
                if ($a.GetPixel($x, $y).ToArgb() -ne $b.GetPixel($x, $y).ToArgb()) {
                    [void]$columns.Add($x)
                    $total++
                }
            }
        }
        $min = if ($columns.Count) { ($columns | Measure-Object -Minimum).Minimum } else { 0 }
        $max = if ($columns.Count) { ($columns | Measure-Object -Maximum).Maximum } else { 0 }
        return [pscustomobject]@{ Total = $total; Min = $min; Max = $max; Band = $max - $min + 1 }
    }
    finally {
        $a.Dispose()
        $b.Dispose()
    }
}

Write-Section "test-gutter: $([IO.Path]::GetFileName($File)) ($lineCount lines)"

try {
    [void](Start-MarkLite -File $File -LogName 'test-gutter')

    # ------------------------------------------------------------- off state
    [void](Send-Cmd 'gutter off')
    $off = Get-State
    Assert-True (-not $off.gutterVisible) 'the gutter starts hidden'
    Assert-Equal 1 $off.firstVisibleLine 'at the top of the document the first visible line is 1'
    Assert-True ($off.lastVisibleLine -le $lineCount) `
        "the last visible line is inside the file ($($off.lastVisibleLine) of $lineCount)"
    $offCapture = Save-WindowCapture (Join-Path $CaptureDir 'gutter-off.png')

    # -------------------------------------------------------------- on state
    [void](Send-Cmd 'gutter on')
    $on = Get-State
    Assert-True $on.gutterVisible 'the gutter is showing'
    $onCapture = Save-WindowCapture (Join-Path $CaptureDir 'gutter-on.png')

    <#  Nothing about the document may have changed: same blocks, same realized
        set, same scroll offset, same visible lines. The numbers are painted into
        space that was already reserved. #>
    Assert-Equal $off.blocks $on.blocks 'the document is unchanged'
    Assert-Equal $off.realizedBlocks $on.realizedBlocks 'no extra blocks were realized'
    Assert-Equal $off.firstVisibleLine $on.firstVisibleLine 'the first visible line is unchanged'
    Assert-Equal (Get-ActiveTabState $off).extent (Get-ActiveTabState $on).extent `
        'the scroll extent is unchanged'
    Assert-Equal (Get-ActiveTabState $off).scrollY (Get-ActiveTabState $on).scrollY `
        'the reader has not moved'

    $band = Measure-CaptureBand -First $offCapture -Second $onCapture
    Assert-True ($band.Total -gt 0) "the numbers are actually drawn ($($band.Total) pixels)"
    #  40 px is the reserved strip. Everything that changed has to be inside one.
    Assert-True ($band.Band -le 40) `
        "the change is confined to one gutter strip (x $($band.Min)..$($band.Max), $($band.Band) px wide)"

    # ------------------------------------------------------- numbers are real
    <#  Jump to a heading and confirm the line the gutter would draw for it is a
        line that really is a heading in the source file. #>
    $target = [Math]::Min(5, $off.tocCount - 1)
    [void](Send-Cmd "toc $target")
    $jumped = Get-State
    foreach ($attempt in 1..6) {
        $next = Get-State
        if ((Get-ActiveTabState $next).scrollY -eq (Get-ActiveTabState $jumped).scrollY) {
            break
        }
        $jumped = $next
    }
    Assert-True (Test-HeadingLine $jumped.targetBlockLine) `
        "toc $target landed on source line $($jumped.targetBlockLine), which is a heading"
    Assert-True ($jumped.firstVisibleLine -le $jumped.targetBlockLine `
        -and $jumped.targetBlockLine -le $jumped.lastVisibleLine) `
        "that heading is on screen (lines $($jumped.firstVisibleLine)..$($jumped.lastVisibleLine))"

    #  Deeper into the document the numbers must have grown, and must still be
    #  inside the file.
    Assert-True ($jumped.firstVisibleLine -ge $off.firstVisibleLine) `
        "the visible lines advanced with the scroll (now $($jumped.firstVisibleLine))"
    Assert-True ($jumped.lastVisibleLine -le $lineCount) `
        "the last visible line is still inside the file ($($jumped.lastVisibleLine) of $lineCount)"

    [void](Send-Cmd 'scroll-end')
    $end = Get-State
    Assert-True ($end.lastVisibleLine -le $lineCount) `
        "at the end of the document the last line is inside the file ($($end.lastVisibleLine) of $lineCount)"

    <#  The gutter draws; it does not build controls. On a large document that
        has to show in both the realized block count and the working set. #>
    if ($end.blocks -gt 1000) {
        [void](Send-Cmd 'scroll 0')
        foreach ($page in 1..10) {
            [void](Send-Cmd 'scroll-page 1')
        }
        [void](Send-Cmd 'gc')
        $worked = Get-State
        Assert-True ($worked.workingSetMb -lt 100) `
            "working set stays under 100 MB with the numbers showing ($($worked.workingSetMb) MB)"
        Assert-True (($worked.realizedBlocks / $worked.blocks) -lt 0.10) `
            "still only $($worked.realizedBlocks) of $($worked.blocks) blocks realized"
    }

    Write-Host "  captures in $CaptureDir" -ForegroundColor DarkGray

    $errors = @(Get-LogLines) -match 'Unhandled|ObjectDisposed|cmd .* -> error'
    Assert-Equal 0 $errors.Count 'no exceptions in the log'
}
finally {
    #  The setting is persisted, so leave it off - the default, and what every
    #  other script expects to find.
    try { [void](Send-Cmd 'gutter off') } catch { }
    Stop-MarkLite
}

# ------------------------------------------------------------- persistence
Write-Section 'test-gutter: the setting survives a restart'

try {
    [void](Start-MarkLite -File $File -LogName 'test-gutter-restart')
    $state = Get-State
    Assert-True (-not $state.gutterVisible) 'the gutter is still hidden after a restart'

    [void](Send-Cmd 'gutter on')
    Stop-MarkLite

    [void](Start-MarkLite -File $File -LogName 'test-gutter-restart2')
    $state = Get-State
    Assert-True $state.gutterVisible 'and shown again after being switched on'
}
finally {
    try { [void](Send-Cmd 'gutter off') } catch { }
    Stop-MarkLite
}

Exit-WithSummary 'test-gutter'
