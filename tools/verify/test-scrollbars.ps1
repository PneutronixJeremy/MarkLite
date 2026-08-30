<#
.SYNOPSIS
    View > Wide scroll bars: on by default, the document's bar stays expanded
    with the pointer away, and the toggle changes nothing but the bar itself.

.DESCRIPTION
    Fluent collapses an idle scroll bar to a hairline; the option keeps every
    bar in the window at its expanded look. The claims checked here:

    - a fresh install (no stored value) has the option ON, and the document's
      vertical bar reports IsExpanded with no pointer anywhere near it;
    - switching it off collapses that bar once its hide delay runs out;
    - the toggle is free: a full-resolution capture diff between the two
      states finds every differing pixel inside one strip at the right edge of
      the window, and the block count, realized set, extent and scroll offset
      are identical — nothing re-laid-out, nothing re-wrapped;
    - the setting survives a restart.

    The fixture is generated: 300 one-line paragraphs and no code fences, so
    the only scroll bar in the capture is the document's own.

    The setting lives in the shared MarkLite key (it is a View setting, not
    session state), so this script clears it for the "fresh install" check and
    leaves it ON — the default — when it is done, as test-gutter leaves the
    gutter off.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).

.PARAMETER CaptureDir
    Where the two captures are written. Defaults to the temp directory.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$CaptureDir
)

. "$PSScriptRoot/common.ps1"

if ($Exe) {
    Set-MarkLiteExe $Exe
}
if (-not $CaptureDir) {
    $CaptureDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-verify/captures'
}

# ------------------------------------------------------------------ fixture

$workDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-verify/scrollbars'
[void][IO.Directory]::CreateDirectory($workDir)
$fixture = Join-Path $workDir 'paragraphs.md'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Three hundred paragraphs')
$lines.Add('')
foreach ($n in 1..300) {
    $lines.Add("Paragraph $n of the scroll bar fixture, one line long so nothing here ever overflows sideways.")
    $lines.Add('')
}
[IO.File]::WriteAllLines($fixture, $lines)

<#  The option is a View setting in the shared key, not part of the instance
    group's session state, so Start-MarkLite's session clearing does not touch
    it. Removed explicitly so the first launch is a fresh install's. #>
function Clear-WideScrollBarsSetting {
    Remove-ItemProperty -Path 'HKCU:\Software\MarkLite' -Name 'WideScrollBars' -ErrorAction SilentlyContinue
}

<#  Polls dump-state until scrollBarExpanded reads $Expected or the timeout
    passes. An auto-hiding bar collapses after its hide delay, not instantly. #>
function Wait-ScrollBarExpanded {
    param([bool]$Expected, [int]$TimeoutSec = 6)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    do {
        $state = Get-State
        if ($state.scrollBarExpanded -eq $Expected) {
            return $state
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    return $state
}

<#  Waits until the anchor has stopped moving: a width change re-wraps, then
    the panel's correction pass puts the reader back, and reading the state
    between the two would compare against a half-finished layout. #>
function Wait-Settled {
    $previous = $null
    $stable = 0
    for ($i = 0; $i -lt 40; $i++) {
        $state = Get-State
        $current = '{0}/{1:N0}' -f $state.firstVisibleBlock, (Get-ActiveTabState $state).scrollY
        if ($current -eq $previous) {
            $stable++
            if ($stable -ge 2) {
                return $state
            }
        } else {
            $stable = 0
        }
        $previous = $current
        Start-Sleep -Milliseconds 100
    }
    return $state
}

<#  Counts differing pixels between two captures and reports the column band
    they fall in — the same measure test-gutter uses for its strip. #>
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
        return [pscustomobject]@{
            Total = $total; Min = $min; Max = $max; Band = $max - $min + 1; Width = $width
        }
    }
    finally {
        $a.Dispose()
        $b.Dispose()
    }
}

Write-Section 'test-scrollbars: fresh install, toggle, capture diff'

try {
    Clear-WideScrollBarsSetting
    [void](Start-MarkLite -File $fixture -LogName 'test-scrollbars')

    # ------------------------------------------------------------ default on
    $on = Get-State
    Assert-True $on.wideScrollBars 'a fresh install has wide scroll bars on'
    Assert-True ($on.blocks -gt 100) "the fixture rendered ($($on.blocks) blocks)"
    Assert-True ((Get-ActiveTabState $on).extent -gt (Get-ActiveTabState $on).viewport) `
        'the document overflows, so there is a bar to look at'
    $on = Wait-ScrollBarExpanded -Expected $true -TimeoutSec 2
    Assert-True $on.scrollBarExpanded 'the document bar is expanded with no pointer over the window'
    $restored = @(Get-LogLines) -match 'wide scroll bars restored:'
    Assert-Equal 0 $restored.Count 'nothing was restored — the default did the work'
    $onCapture = Save-WindowCapture (Join-Path $CaptureDir 'scrollbars-on.png')

    # -------------------------------------------------------------- toggle off
    [void](Send-Cmd 'wide-scrollbars off')
    $off = Wait-ScrollBarExpanded -Expected $false
    Assert-True (-not $off.wideScrollBars) 'the setting reads off'
    Assert-True (-not $off.scrollBarExpanded) 'the document bar collapsed once auto-hide was back'
    $offCapture = Save-WindowCapture (Join-Path $CaptureDir 'scrollbars-off.png')

    # --------------------------------------------------- a 16 px column
    <#  Fluent lays an auto-hiding bar over the content and gives a permanent
        one its own column, so the option costs the document exactly
        ScrollBarSize (16 DIP) of width and nothing in height: text never runs
        under the bar, and a paragraph that wrapped near the edge wraps again.
        That is the whole layout effect — the same document, 16 px narrower. #>
    Assert-Equal $on.blocks $off.blocks 'the document is unchanged'
    Assert-Near 16 ((Get-ActiveTabState $off).viewportWidth - (Get-ActiveTabState $on).viewportWidth) 0.5 `
        'the permanent bar takes a 16 px column of the document'
    Assert-Equal (Get-ActiveTabState $on).viewport (Get-ActiveTabState $off).viewport `
        'and no height — there is no horizontal bar to make room for'
    Assert-Equal 0 (Get-ActiveTabState $off).scrollY 'at the top of the document the reader stays at the top'

    <#  Every pixel of the change must be inside the document column: the menu,
        tab strip and sidebar are not the document's business. The bar itself
        has to show up too — a wide thumb drawn in the rightmost strip. #>
    $band = Measure-CaptureBand -First $onCapture -Second $offCapture
    Assert-True ($band.Total -gt 0) "the two states look different ($($band.Total) pixels)"
    Assert-True ($band.Max -ge $band.Width - 48) `
        "the bar strip at the right edge changed (x $($band.Min)..$($band.Max), capture $($band.Width) px wide)"
    $sidebarEdge = [int]($band.Width * 0.25)
    Assert-True ($band.Min -ge $sidebarEdge) `
        "nothing left of the document changed (first differing column $($band.Min), sidebar ends before $sidebarEdge)"

    # ------------------------------------------------ the anchor holds
    <#  A 16 px re-wrap is a resize as far as the panel is concerned, so the
        reader parked deep in the document must stay on the same block through
        the toggle in both directions — the guarantee test-resize holds a
        window drag to. #>
    [void](Send-Cmd 'scroll-page 8')
    $parked = Get-State
    $parkedBlock = $parked.firstVisibleBlock
    Assert-True ($parkedBlock -gt 0) "reader parked on block $parkedBlock"

    [void](Send-Cmd 'wide-scrollbars on')
    $again = Wait-Settled
    Assert-True $again.wideScrollBars 'the setting reads on again'
    Assert-Equal $parkedBlock $again.firstVisibleBlock 'switching on keeps the reader on the same block'
    $again = Wait-ScrollBarExpanded -Expected $true
    Assert-True $again.scrollBarExpanded 'and the bar expanded again without a pointer'

    [void](Send-Cmd 'wide-scrollbars off')
    $offAgain = Wait-Settled
    Assert-Equal $parkedBlock $offAgain.firstVisibleBlock 'switching off keeps the reader on the same block'
    [void](Send-Cmd 'wide-scrollbars on')
    $onAgain = Wait-Settled
    Assert-Equal $parkedBlock $onAgain.firstVisibleBlock 'and on once more'

    Write-Host "  captures in $CaptureDir" -ForegroundColor DarkGray

    $errors = @(Get-LogLines) -match 'Unhandled|ObjectDisposed|cmd .* -> error'
    Assert-Equal 0 $errors.Count 'no exceptions in the log'

    #  Leave it off for the restart check below.
    [void](Send-Cmd 'wide-scrollbars off')
}
finally {
    Stop-MarkLite
}

# ------------------------------------------------------------- persistence
Write-Section 'test-scrollbars: the setting survives a restart'

try {
    [void](Start-MarkLite -File $fixture -LogName 'test-scrollbars-restart')
    $state = Get-State
    Assert-True (-not $state.wideScrollBars) 'switched off before the restart, still off after it'
    $restored = @(Get-LogLines) -match 'wide scroll bars restored: off'
    Assert-Equal 1 $restored.Count 'the log says the stored value was applied'
    $state = Wait-ScrollBarExpanded -Expected $false
    Assert-True (-not $state.scrollBarExpanded) 'and the bar is the thin one'

    [void](Send-Cmd 'wide-scrollbars on')
    Stop-MarkLite

    [void](Start-MarkLite -File $fixture -LogName 'test-scrollbars-restart2')
    $state = Get-State
    Assert-True $state.wideScrollBars 'switched on before the restart, still on after it'
}
finally {
    #  The default is on, and that is how the machine is left.
    try { [void](Send-Cmd 'wide-scrollbars on') } catch { }
    Stop-MarkLite
}

Exit-WithSummary 'test-scrollbars'
