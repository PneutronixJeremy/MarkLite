<#
.SYNOPSIS
    Resizing the window keeps the reader on the same place in the document.

.DESCRIPTION
    A different window width wraps every paragraph differently, so every height
    in the document changes and the scroll offset the reader was at stops
    pointing at their text. The panel holds them on the block they were on
    instead; this asserts that, for a narrower window, a wider one, and a
    height-only change (which re-wraps nothing and must therefore move nothing).

    Resizing is SetWindowPos with SWP_NOACTIVATE - a window-geometry call, not
    injected input, and it does not take focus. Position is compared as
    firstVisibleBlock: the pixel offset legitimately differs at a different
    width, which is the whole point.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).

.PARAMETER File
    Document to read. Defaults to the large stress fixture, where a lost anchor
    lands thousands of pixels away rather than a few.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$File,
    [string]$SecondFile
)

. "$PSScriptRoot/common.ps1"

if ($Exe) {
    Set-MarkLiteExe $Exe
}
$repo = (git rev-parse --show-toplevel)
if (-not $File) {
    $File = Join-Path $repo 'testdata/stress-large.md'
}
if (-not $SecondFile) {
    #  Prose with real paragraphs: it re-wraps far more per pixel of width than
    #  the stress fixture's one-line blocks, which is where drift shows up.
    $SecondFile = Join-Path $repo 'testdata/sample-plan.md'
}

$SWP_NOACTIVATE = 0x0010
$SWP_NOZORDER = 0x0004
$SWP_NOMOVE = 0x0002

Write-Section 'test-resize'

<#  Resizes the window and waits for the layout to settle. "Settled" is the
    panel's own reported state repeating: a width change re-measures over
    several passes, and reading the block index in the middle of that would be
    reading a number still on its way. #>
function Resize-Window {
    param([int]$Width, [int]$Height)

    $handle = Get-MarkLiteWindow
    [void][MarkLite.Native]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, $Width, $Height,
        $SWP_NOACTIVATE -bor $SWP_NOZORDER -bor $SWP_NOMOVE)

    $previous = $null
    $stable = 0
    for ($i = 0; $i -lt 40; $i++) {
        $state = Get-State
        #  scrollY lives on the tab, not on the top-level state object.
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
    throw 'The layout never settled after the resize.'
}

try {
    [void](Start-MarkLite -File $File -LogName 'test-resize')

    $rect = New-Object MarkLite.Native+RECT
    [void][MarkLite.Native]::GetWindowRect((Get-MarkLiteWindow), [ref]$rect)
    $startWidth = $rect.Right - $rect.Left
    $startHeight = $rect.Bottom - $rect.Top

    #  Park the reader well inside the document: an anchor that is lost lands at
    #  a wildly different block, which is what this has to be able to see.
    [void](Send-Cmd 'scroll-page 6')
    $state = Get-State
    $parkedBlock = $state.firstVisibleBlock
    #  As a FRACTION of the block: the pixel offset into a paragraph means
    #  nothing once the paragraph wraps to a different height.
    $parkedFraction = if ($state.anchorBlockHeight -gt 0) {
        $state.anchorWithin / $state.anchorBlockHeight
    } else { 0 }
    Assert-True ($parkedBlock -gt 0) `
        "reader parked on block $parkedBlock (offset $([int](Get-ActiveTabState $state).scrollY))"

    #  ------------------------------------------------------------ narrower
    $narrow = Resize-Window -Width ([int]($startWidth * 0.55)) -Height $startHeight
    Assert-Equal $parkedBlock $narrow.firstVisibleBlock 'a narrower window keeps the reader on the same block'
    $narrowFraction = if ($narrow.anchorBlockHeight -gt 0) {
        $narrow.anchorWithin / $narrow.anchorBlockHeight
    } else { 0 }
    Assert-Near $parkedFraction $narrowFraction 0.15 'and the same way into that block, proportionally'

    #  -------------------------------------------------------------- wider
    #  Widening back up rather than past the starting width: the window may
    #  already be as wide as the monitor allows, and a resize the shell clamps
    #  would assert nothing.
    $wide = Resize-Window -Width ([int]($startWidth * 0.75)) -Height $startHeight
    Assert-Equal $parkedBlock $wide.firstVisibleBlock 'a wider window keeps the reader on the same block'

    #  ------------------------------------------------------- back to the start
    $restored = Resize-Window -Width $startWidth -Height $startHeight
    Assert-Equal $parkedBlock $restored.firstVisibleBlock 'and so does going back to the original width'

    #  -------------------------------------------------------- height only
    #  Nothing re-wraps, so the offset itself must be left alone - re-anchoring
    #  here would move the document under someone who only made the window
    #  taller.
    $before = (Get-ActiveTabState (Get-State)).scrollY
    $shorter = Resize-Window -Width $startWidth -Height ([int]($startHeight * 0.6))
    Assert-Near $before (Get-ActiveTabState $shorter).scrollY 2 `
        'a height-only change leaves the scroll offset alone'
    Assert-Equal $parkedBlock $shorter.firstVisibleBlock 'and the reader is still on the same block'

    #  ------------------------------------------------ a stepped drag, not a jump
    #  Dragging a window edge is dozens of small width changes, and each one
    #  used to re-read the anchor before the previous correction had landed:
    #  every step drifted a little and a drag ended pages away.
    for ($step = 1; $step -le 12; $step++) {
        $handle = Get-MarkLiteWindow
        [void][MarkLite.Native]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0,
            [int]($startWidth * (1 - (0.03 * $step))), $startHeight,
            $SWP_NOACTIVATE -bor $SWP_NOZORDER -bor $SWP_NOMOVE)
        Start-Sleep -Milliseconds 60
    }
    $dragged = Resize-Window -Width ([int]($startWidth * 0.64)) -Height $startHeight
    Assert-Equal $parkedBlock $dragged.firstVisibleBlock 'a stepped drag keeps the reader on the same block'
    [void](Resize-Window -Width $startWidth -Height $startHeight)

    #  ------------------------------------------------------- a second tab
    #  Only the active tab is laid out, so an inactive tab meets the new width
    #  when it is switched to. Its position is stored as a block anchor for
    #  exactly this reason, and this is the check that it survives.
    $before = Get-LogCount
    [void](Open-InMarkLite -File $SecondFile)
    [void](Wait-Log -Pattern 'handoff received' -Since $before)
    [void](Send-Cmd 'scroll-page 3')
    $secondParked = (Get-State).firstVisibleBlock
    Assert-True ($secondParked -gt 0) "second tab parked on block $secondParked"

    [void](Send-Cmd 'tab 0')
    [void](Resize-Window -Width ([int]($startWidth * 0.6)) -Height $startHeight)
    $back = Get-LogCount
    [void](Send-Cmd 'tab 1')
    [void](Wait-Log -Pattern 'tab switched' -Since $back)
    $secondAfter = Resize-Window -Width ([int]($startWidth * 0.6)) -Height $startHeight
    Assert-Equal $secondParked $secondAfter.firstVisibleBlock `
        'a tab resized while it was inactive comes back to the same block'

    [void](Send-Cmd 'close-tab')
    [void](Resize-Window -Width $startWidth -Height $startHeight)

    #  ------------------------------------------------- a search match stays put
    #  Re-anchoring must not throw away what the reader was looking at: the
    #  match count is the document's, but the highlighted subset is what is on
    #  screen.
    [void](Send-Cmd 'find Kestrel')
    $found = Get-State
    if ($found.matches -gt 0) {
        $matchBlock = $found.firstVisibleBlock
        $narrowAgain = Resize-Window -Width ([int]($startWidth * 0.7)) -Height $startHeight
        Assert-Equal $matchBlock $narrowAgain.firstVisibleBlock 'the current match is still on screen after a resize'
        Assert-True ($narrowAgain.highlighted -gt 0) 'and it is still highlighted'
    } else {
        Write-Skip 'no match to check the find bar against in this document'
    }
    [void](Send-Cmd 'find-close')

    [void](Resize-Window -Width $startWidth -Height $startHeight)
}
finally {
    Stop-MarkLite
    Clear-MarkLiteSession
}

Exit-WithSummary 'test-resize'
