<#
.SYNOPSIS
    Virtualized rendering: realization window, scroll extent, anchors.

.DESCRIPTION
    Asserts that the virtualizing viewer holds only the blocks near the
    viewport, that the scroll extent covers the whole document from the first
    frame, and that jumping around the document realizes the target rather than
    leaving the window empty.

    Requires MARKLITE_VIRTUAL=1; the script sets it for the app it launches.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).

.PARAMETER File
    Document to virtualize. Defaults to the large stress fixture.
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
    $File = Join-Path (git rev-parse --show-toplevel) 'testdata/stress-large.md'
}
if (-not $CaptureDir) {
    $CaptureDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-verify/captures'
}

$env:MARKLITE_VIRTUAL = '1'

Write-Section "test-virtual: $([IO.Path]::GetFileName($File))"

try {
    [void](Start-MarkLite -File $File -LogName 'test-virtual')

    $state = Get-State
    Assert-True $state.virtual 'the virtualizing viewer is in use'
    Assert-True ($state.blocks -gt 1000) "the document parsed to $($state.blocks) blocks"

    $tab = Get-ActiveTabState $state
    Assert-True ($tab.extent -gt 10000) "scroll extent covers the document ($([int]$tab.extent) px)"

    #  The point of the whole exercise: a fraction of the document exists as
    #  controls. Three viewports' worth is the realization window; 10% of a
    #  2000-block document would already be far more than that.
    $realizedFraction = $state.realizedBlocks / $state.blocks
    Assert-True ($realizedFraction -lt 0.10) `
        "only $($state.realizedBlocks) of $($state.blocks) blocks realized ($([math]::Round($realizedFraction * 100, 1))%)"

    #  Scrolling must move the window, not grow it.
    $peak = $state.realizedBlocks
    foreach ($page in 1..10) {
        [void](Send-Cmd 'scroll-page 1')
        $s = Get-State
        $peak = [Math]::Max($peak, $s.realizedBlocks)
    }
    Assert-True (($peak / $state.blocks) -lt 0.10) `
        "ten pages of scrolling never realized more than $peak blocks"

    $mid = Get-State
    Assert-True ($mid.firstRealized -gt 0) `
        "the window moved with the viewport (first realized block $($mid.firstRealized))"
    Assert-True ($mid.measuredBlocks -gt $mid.realizedBlocks) `
        "measured heights outlive realization ($($mid.measuredBlocks) measured, $($mid.realizedBlocks) realized)"

    #  End of document, then back to the top.
    #  "End" is computed from the extent, and the extent is an ESTIMATE that
    #  grows as the last blocks realize and measure — so one jump lands near
    #  the end and the next lands nearer. Repeat until it stops moving, which
    #  is what the scrollbar does under the reader's hand too.
    $end = $null
    foreach ($attempt in 1..6) {
        [void](Send-Cmd 'scroll-end')
        $next = Get-State
        if ($end -and $next.lastRealized -le $end.lastRealized) {
            $end = $next
            break
        }
        $end = $next
    }
    #  "End" is the estimated extent minus a viewport, and the estimate is
    #  still being refined as those last blocks measure, so the landing spot is
    #  near the end rather than exactly on it. Within a screenful is the honest
    #  claim: the reader sees the end of the document.
    Assert-True ($end.lastRealized -ge ($end.blocks - 10)) `
        "scroll-end reaches the end of the document ($($end.lastRealized) of $($end.blocks - 1))"

    [void](Send-Cmd 'scroll 0')
    $top = Get-State
    Assert-Equal 0 $top.firstRealized 'scrolling back to the top realizes block 0'
    Assert-Equal 0 (Get-ActiveTabState $top).scrollY 'offset is back to zero'

    #  Contents sidebar comes from the parsed model, so it is complete even
    #  though almost nothing is realized.
    Assert-True ($top.tocCount -gt 100) "contents built from the model ($($top.tocCount) headings)"

    #  A jump to a heading deep in the document has to realize it.
    [void](Send-Cmd 'toc 250')
    $jumped = Get-State
    Assert-Equal 250 $jumped.tocIndex 'toc 250 became the current section'
    Assert-True ($jumped.firstRealized -gt 100) `
        "the jump realized blocks deep in the document (first realized $($jumped.firstRealized))"

    [void](Save-WindowCapture (Join-Path $CaptureDir 'virtual-toc-jump.png'))
    Write-Host "  captures in $CaptureDir" -ForegroundColor DarkGray

    #  The whole point: a 500 KB document, scrolled end to end, still costs
    #  about what an empty window costs.
    [void](Send-Cmd 'gc')
    $after = Get-State
    Assert-True ($after.workingSetMb -lt 100) `
        "working set stays under 100 MB after the scroll workout ($($after.workingSetMb) MB)"

    #  Resizing re-wraps every block, so every measured height is a guess
    #  again. The extent must survive it and the reader must stay put.
    #  SetWindowPos, not injected input: the window is neither focused nor
    #  raised (same reasoning as the WM_CLOSE shutdown in common.ps1).
    [void](Send-Cmd 'toc 120')
    $beforeResize = Get-State
    $anchorBlock = $beforeResize.firstVisibleBlock
    $rect = New-Object MarkLite.Native+RECT
    [void][MarkLite.Native]::GetWindowRect((Get-MarkLiteWindow), [ref]$rect)
    $SWP_NOACTIVATE = 0x0010
    $SWP_NOZORDER = 0x0004
    $SWP_NOMOVE = 0x0002
    [void][MarkLite.Native]::SetWindowPos((Get-MarkLiteWindow), [IntPtr]::Zero, 0, 0,
        ($rect.Right - $rect.Left - 300), ($rect.Bottom - $rect.Top),
        ($SWP_NOACTIVATE -bor $SWP_NOZORDER -bor $SWP_NOMOVE))
    #  Re-layout after a resize converges over several passes: every height is
    #  a guess again, and each measured block moves the running estimate. Wait
    #  for it to settle instead of reading a number mid-flight.
    $resized = Get-State
    foreach ($attempt in 1..10) {
        $next = Get-State
        if ($next.firstVisibleBlock -eq $resized.firstVisibleBlock -and
            $next.measuredBlocks -eq $resized.measuredBlocks) {
            break
        }
        $resized = $next
    }
    Assert-True ($resized.blocks -eq $beforeResize.blocks) 'the document survived the resize'
    Assert-True ((Get-ActiveTabState $resized).extent -gt 10000) `
        "extent re-estimated after the resize ($([int](Get-ActiveTabState $resized).extent) px)"
    #  Not exact by construction: blocks ABOVE the viewport are never realized,
    #  so their heights remain estimates and the anchor's absolute offset can
    #  only be as good as those. A handful of blocks out of two thousand is the
    #  honest bound - before the anchor was held across the re-measure this
    #  drifted by more than sixty.
    Assert-True ([Math]::Abs($resized.firstVisibleBlock - $anchorBlock) -le 5) `
        "the reader stayed near block $anchorBlock (now $($resized.firstVisibleBlock))"

    #  Nothing may have thrown along the way.
    $errors = @(Get-LogLines) -match 'Unhandled|ObjectDisposed|cmd .* -> error'
    Assert-Equal 0 $errors.Count 'no exceptions in the log'
}
finally {
    Stop-MarkLite
}

Exit-WithSummary 'test-virtual'
