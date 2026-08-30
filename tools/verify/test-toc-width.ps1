<#
.SYNOPSIS
    Resizable contents sidebar: default 250, clamped 140-600, remembered across
    a restart, and the document takes every pixel the sidebar gives up.

.DESCRIPTION
    The splitter drag itself is pointer plumbing no script here may inject; the
    `toc-width` command runs the same method a splitter release does (clamp,
    apply to the column, persist), so everything after the pointer is covered:

    - a fresh install opens the sidebar at 250;
    - a new width shows up in the sidebar's laid-out bounds AND as the same
      number of pixels lost by the document's viewport, with the reader still
      on the same block (a width change re-wraps; the anchor holds, as
      test-resize demands of a window drag);
    - widths outside 140-600 are clamped, not applied;
    - the width survives a restart;
    - hiding the sidebar (Ctrl+T, here `toc-toggle`) collapses its column to
      zero — the document gains the width plus the 5 px splitter — and showing
      it again brings the remembered width back, not the default.

    The width lives in the shared MarkLite key (a View setting, not session
    state), so this script clears it for the fresh-install check and leaves it
    at 250, the default, when done.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).

.PARAMETER File
    Document to open; needs headings. Defaults to testdata/sample-plan.md.

.PARAMETER CaptureDir
    Where the captures at 400 and 250 are written. Defaults to the temp directory.
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

$SplitterWidth = 5

function Clear-TocWidthSetting {
    Remove-ItemProperty -Path 'HKCU:\Software\MarkLite' -Name 'TocWidth' -ErrorAction SilentlyContinue
}

<#  Waits until the layout has settled after a width change: the sidebar's
    bounds and the document's viewport both come from the next layout pass,
    and the panel's anchor correction runs after that. #>
function Wait-Settled {
    $previous = $null
    $stable = 0
    for ($i = 0; $i -lt 40; $i++) {
        $state = Get-State
        $tab = Get-ActiveTabState $state
        $current = '{0}/{1}/{2}/{3:N0}' -f $state.tocWidth, $tab.viewportWidth, $state.firstVisibleBlock, $tab.scrollY
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

Write-Section "test-toc-width: $([IO.Path]::GetFileName($File))"

try {
    Clear-TocWidthSetting
    [void](Start-MarkLite -File $File -LogName 'test-toc-width')

    # ----------------------------------------------------------- default
    $start = Wait-Settled
    Assert-True ($start.tocCount -gt 0) "the document has headings ($($start.tocCount)), so the sidebar shows"
    Assert-True $start.tocVisible 'the sidebar is visible'
    Assert-Near 250 $start.tocWidth 1 'a fresh install opens the sidebar at the default width'
    $restored = @(Get-LogLines) -match 'toc width restored:'
    Assert-Equal 0 $restored.Count 'nothing was restored — the default did the work'

    #  Park the reader a little way in so "the anchor held" means something.
    [void](Send-Cmd 'scroll-page 2')
    $parked = Wait-Settled
    $parkedBlock = $parked.firstVisibleBlock
    $startViewport = (Get-ActiveTabState $parked).viewportWidth

    # ------------------------------------------------------------- wider
    [void](Send-Cmd 'toc-width 400')
    $wide = Wait-Settled
    Assert-Near 400 $wide.tocWidth 1 'toc-width 400 lays the sidebar out at 400'
    Assert-Near 150 ($startViewport - (Get-ActiveTabState $wide).viewportWidth) 2 `
        'and the document gave up exactly those 150 px'
    Assert-Equal $parkedBlock $wide.firstVisibleBlock 'the reader is still on the same block'
    $wideCapture = Save-WindowCapture (Join-Path $CaptureDir 'toc-width-400.png')

    # ------------------------------------------------------------ clamps
    [void](Send-Cmd 'toc-width 50')
    $narrow = Wait-Settled
    Assert-Near 140 $narrow.tocWidth 1 'toc-width 50 is clamped up to 140'
    [void](Send-Cmd 'toc-width 900')
    $huge = Wait-Settled
    Assert-Near 600 $huge.tocWidth 1 'toc-width 900 is clamped down to 600'
    Assert-True ((Get-ActiveTabState $huge).viewportWidth -gt 200) `
        "the document still has room at the widest sidebar ($((Get-ActiveTabState $huge).viewportWidth) px)"

    # -------------------------------------------------------- hide / show
    [void](Send-Cmd 'toc-width 400')
    $before = Wait-Settled
    [void](Send-Cmd 'toc-toggle')
    $hidden = Wait-Settled
    Assert-True (-not $hidden.tocVisible) 'toc-toggle hides the sidebar'
    Assert-Equal 0 $hidden.tocWidth 'and its column collapses to zero'
    Assert-Near (400 + $SplitterWidth) ((Get-ActiveTabState $hidden).viewportWidth - (Get-ActiveTabState $before).viewportWidth) 2 `
        'the document gains the sidebar and the splitter'
    Assert-Equal $parkedBlock $hidden.firstVisibleBlock 'the reader is still on the same block'

    [void](Send-Cmd 'toc-toggle')
    $shown = Wait-Settled
    Assert-True $shown.tocVisible 'toc-toggle shows it again'
    Assert-Near 400 $shown.tocWidth 1 'at the remembered width, not the default'
    Assert-Near (Get-ActiveTabState $before).viewportWidth (Get-ActiveTabState $shown).viewportWidth 1 `
        'and the document is back to its previous width'

    $errors = @(Get-LogLines) -match 'Unhandled|ObjectDisposed|cmd .* -> error'
    Assert-Equal 0 $errors.Count 'no exceptions in the log'
}
finally {
    Stop-MarkLite
}

# ------------------------------------------------------------- persistence
Write-Section 'test-toc-width: the width survives a restart'

try {
    [void](Start-MarkLite -File $File -LogName 'test-toc-width-restart')
    $state = Wait-Settled
    Assert-Near 400 $state.tocWidth 1 'the sidebar comes back at 400'
    $restored = @(Get-LogLines) -match 'toc width restored: 400'
    Assert-Equal 1 $restored.Count 'the log says the stored width was applied'

    [void](Send-Cmd 'toc-width 250')
    $reset = Wait-Settled
    Assert-Near 250 $reset.tocWidth 1 'back to the default'
    $resetCapture = Save-WindowCapture (Join-Path $CaptureDir 'toc-width-250.png')
    Write-Host "  captures in $CaptureDir" -ForegroundColor DarkGray
}
finally {
    #  The default width is what every other script, and the docs screenshots,
    #  expect to find.
    try { [void](Send-Cmd 'toc-width 250') } catch { }
    Stop-MarkLite
}

Exit-WithSummary 'test-toc-width'
