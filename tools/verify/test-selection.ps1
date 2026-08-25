<#
.SYNOPSIS
    Selection, copy and link clicks over the rendered document.

.DESCRIPTION
    Copy hands back the MARKDOWN SOURCE the selection covers, and the selection
    is addressed by block and character rather than by control — so it can cover
    parts of the document that were never rendered. Both claims are checked
    against the file itself:

      exact slice    - a selection between two known offsets must copy exactly
                       the characters those offsets name in the file. The
                       document is generated here (as test-reload does) so the
                       mapping is knowable from outside the app: every block is
                       one plain line, so block k's text offset o is a source
                       offset this script can compute.
      whole document - select-all then copy must give back the file, character
                       for character.
      unrealized     - a selection spanning hundreds of blocks that have no
                       controls must copy correctly and must not realize them.
      links          - "click-link <block>" follows a link from the middle of
                       the rectangle its own text layout reports, through the
                       same hit test a mouse click uses. An in-document anchor
                       must scroll; an external https link must be resolved and
                       logged and NOT launched (MARKLITE_DEBUG suppresses the
                       browser, so a verification run never opens one).
      highlight      - a capture with a selection over three blocks must show
                       the selection colour, and none of it once cleared.

    Nothing is typed or clicked: selections and clicks travel over the debug
    command channel.

    NOTE: checking copy means using the clipboard. The script saves its text
    contents at the start and puts them back at the end.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).
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

$repo = (git rev-parse --show-toplevel)
$stress = Join-Path $repo 'testdata/stress-large.md'

#  A private fixture whose block numbering is knowable from outside: 400 blocks,
#  one plain line each, blank line between, no inline markup at all - so a block
#  offset IS a source offset and the expected slice can be computed here.
$workDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-verify/selection'
[void][IO.Directory]::CreateDirectory($workDir)
$file = Join-Path $workDir 'selection-fixture.md'

$lines = [System.Collections.Generic.List[string]]::new()
foreach ($block in 0..399) {
    $lines.Add("Paragraph $block records the calibration note for bay $block of the station.")
    $lines.Add('')
}
#  WriteAllLines uses the platform newline, so the file is CRLF here; every
#  offset below is computed from the file's own bytes, never assumed.
[IO.File]::WriteAllLines($file, $lines)
$fixtureText = [IO.File]::ReadAllText($file)

#  Where block k's line starts in the file. Block k is line 2k.
$fixtureLines = $fixtureText -split "`r`n", 0
function Get-BlockStart {
    param([int]$Block)
    $offset = 0
    for ($i = 0; $i -lt (2 * $Block); $i++) {
        $offset += $fixtureLines[$i].Length + 2
    }
    return $offset
}

$savedClipboard = ''
try {
    $savedClipboard = Get-Clipboard -Raw -ErrorAction SilentlyContinue
} catch {
    $savedClipboard = ''
}

<#  A one-line verdict on two long strings: equal, or the lengths and the first
    offset at which they diverge. Printing half a megabyte of markdown into a
    PASS line helps nobody. #>
function Compare-Text {
    param([string]$Expected, [string]$Actual)
    if ($null -eq $Actual) { return '(nothing was copied)' }
    if ($Expected -ceq $Actual) { return "($($Expected.Length) chars)" }
    $limit = [Math]::Min($Expected.Length, $Actual.Length)
    $at = $limit
    for ($i = 0; $i -lt $limit; $i++) {
        if ($Expected[$i] -cne $Actual[$i]) { $at = $i; break }
    }
    return "(expected $($Expected.Length) chars, got $($Actual.Length); first difference at $at)"
}

function Get-Copied {
    param([string]$Command)
    Set-Clipboard -Value 'marklite-verify-sentinel'
    [void](Send-Cmd $Command)
    [void](Send-Cmd 'copy')
    <#  The clipboard is written asynchronously by the app, so the sentinel is
        what says "not yet" rather than a sleep. #>
    foreach ($attempt in 1..40) {
        $text = Get-Clipboard -Raw
        if ($text -ne 'marklite-verify-sentinel') {
            return $text
        }
        Start-Sleep -Milliseconds 50
    }
    return $null
}

Write-Section 'test-selection: generated 400-block fixture'

try {
    [void](Start-MarkLite -File $file -LogName 'test-selection')

    # ------------------------------------------------- an exact slice
    $before = Get-State
    Assert-Equal 400 $before.blocks 'the generated document is 400 blocks'
    Assert-Equal '' $before.selection 'nothing is selected to begin with'

    $from = (Get-BlockStart 10) + 5
    $to = (Get-BlockStart 12) + 20
    $expected = $fixtureText.Substring($from, $to - $from)

    $copied = Get-Copied 'select 10 5 12 20'
    Assert-True ($null -ne $copied) 'copy reached the clipboard'
    Assert-True ($copied -ceq $expected) `
        ("select 10 5 12 20 copied exactly the source those offsets name " + (Compare-Text $expected $copied))

    $state = Get-State
    Assert-Equal "10:5-12:20 ($($expected.Length) chars)" $state.selection `
        'dump-state reports the range and the length of the markdown it covers'

    # --------------------------------- a range over unrealized blocks
    <#  Blocks 300..380 are nowhere near the viewport, so they have no controls
        at all. Selecting through them must copy correctly and must not drag
        them into existence - that is the whole point of addressing a selection
        by block and character. #>
    $realizedBefore = $state.realizedBlocks
    $from = (Get-BlockStart 300) + 0
    $to = (Get-BlockStart 380) + 10
    $expected = $fixtureText.Substring($from, $to - $from)
    $copied = Get-Copied 'select 300 0 380 10'
    Assert-True ($copied -ceq $expected) `
        ("a range over 80 unrendered blocks copied correctly " + (Compare-Text $expected $copied))
    $state = Get-State
    Assert-Equal $realizedBefore $state.realizedBlocks `
        'selecting through unrendered blocks realized none of them'
    Assert-True ($state.realizedBlocks -lt 40) `
        "only $($state.realizedBlocks) of 400 blocks have controls"

    # ------------------------------------------------ the whole file
    <#  Compared with Assert-True, not Assert-Equal: a mismatch on half a
        megabyte of markdown is not something to print. The report is the two
        lengths and the first offset that differs. #>
    $copied = Get-Copied 'select-all'
    Assert-True ($copied -ceq $fixtureText) `
        ("select-all copied the file, character for character " + (Compare-Text $fixtureText $copied))
    $state = Get-State
    Assert-True ($state.selection -like '0:0-399:*') "select-all spans the document ($($state.selection))"

    [void](Send-Cmd 'select-none')
    $state = Get-State
    Assert-Equal '' $state.selection 'select-none clears the selection'
}
finally {
    Stop-MarkLite
}

Write-Section "test-selection: $([IO.Path]::GetFileName($stress))"

try {
    [void](Start-MarkLite -File $stress -LogName 'test-selection-stress')
    $stressText = [IO.File]::ReadAllText($stress)

    # ---------------------------------- the whole file, 2074 blocks
    $state = Get-State
    Assert-Equal 2074 $state.blocks 'the stress fixture is 2074 blocks'
    $copied = Get-Copied 'select-all'
    Assert-True ($copied -ceq $stressText) `
        ("select-all copied every byte of the 530 KB file " + (Compare-Text $stressText $copied))
    $state = Get-State
    Assert-True ($state.realizedBlocks -lt 40) `
        "copying the whole document realized nothing extra ($($state.realizedBlocks) blocks)"

    # --------------------------------------- a slice must be verbatim
    <#  This fixture is full of markup, so the script cannot predict the offsets
        - but it CAN insist that whatever comes back is a verbatim stretch of the
        file, which is the property that matters: copy never invents text. #>
    [void](Send-Cmd 'select-none')
    $copied = Get-Copied 'select 4 10 8 30'
    Assert-True ($copied.Length -gt 20) "a mid-document range copied $($copied.Length) chars"
    Assert-True ($stressText.Contains($copied)) 'the copied markdown appears verbatim in the file'
    $state = Get-State
    Assert-Equal "4:10-8:30 ($($copied.Length) chars)" $state.selection `
        'dump-state agrees on the range and its length'

    # ---------------------------------------------- the highlight
    [void](Send-Cmd 'select-none')
    $clean = Save-WindowCapture (Join-Path $CaptureDir 'selection-none.png')
    [void](Send-Cmd 'select 4 0 8 20')
    $marked = Save-WindowCapture (Join-Path $CaptureDir 'selection-three-blocks.png')

    Add-Type -AssemblyName System.Drawing
    function Measure-SelectionPixels {
        param([string]$Path)
        $bitmap = [System.Drawing.Bitmap]::FromFile($Path)
        $rows = [System.Collections.Generic.HashSet[int]]::new()
        $count = 0
        #  Every second pixel: the highlight is a solid band, not a hairline.
        for ($y = 0; $y -lt $bitmap.Height; $y += 2) {
            for ($x = 0; $x -lt $bitmap.Width; $x += 2) {
                $c = $bitmap.GetPixel($x, $y)
                <#  MdSelectionBackground blended over the page: dim, blue
                    dominant, and darker in red and green than link text is -
                    which is the one other blue on the page. #>
                if ($c.B -gt 85 -and $c.R -lt 75 -and $c.G -lt 110 `
                        -and ($c.B - $c.G) -gt 20 -and ($c.B - $c.R) -gt 40) {
                    $count++
                    [void]$rows.Add($y)
                }
            }
        }
        $bitmap.Dispose()
        return [pscustomobject]@{ Pixels = $count; Rows = $rows.Count }
    }

    $withNone = Measure-SelectionPixels $clean
    $withSome = Measure-SelectionPixels $marked
    Assert-True ($withSome.Pixels -gt ($withNone.Pixels + 500)) `
        "the selection is painted ($($withSome.Pixels) matching pixels against $($withNone.Pixels) with none selected)"
    Assert-True ($withSome.Rows -gt ($withNone.Rows + 20)) `
        "the highlight covers many rows of text ($($withSome.Rows) against $($withNone.Rows) with none selected)"
    Write-Host "  captures in $CaptureDir"

    # ---------------------------------------------------- links
    <#  Walk the realized blocks and click the first link in each. The block
        indices are not knowable from outside, so the check is on what comes
        back: at least one in-document anchor, which must scroll, and at least
        one external link, which must be resolved and logged without a browser. #>
    [void](Send-Cmd 'select-none')
    [void](Send-Cmd 'scroll 0')
    $state = Get-State
    $anchorClicked = $null
    $externalClicked = $null
    foreach ($block in 0..([Math]::Min(30, $state.lastRealized))) {
        $ack = Send-Cmd "click-link $block"
        if ($ack.Line -match 'cmd click-link .* -> (#\S+)$' -and -not $anchorClicked) {
            $anchorClicked = @{ Block = $block; Url = $Matches[1]; State = (Get-State) }
        }
        elseif ($ack.Line -match 'cmd click-link .* -> (https?://\S+)$' -and -not $externalClicked) {
            $externalClicked = @{ Block = $block; Url = $Matches[1] }
        }
        if ($anchorClicked -and $externalClicked) {
            break
        }
        #  Keep the reader at the top so the next block is still realized.
        [void](Send-Cmd 'scroll 0')
    }

    if ($anchorClicked) {
        Assert-True (((Get-ActiveTabState $anchorClicked.State).scrollY) -gt 0) `
            "clicking $($anchorClicked.Url) in block $($anchorClicked.Block) scrolled the document"
        $resolved = @(Get-LogLines) -match 'anchor link: '
        Assert-True ($resolved.Count -ge 1) 'the anchor resolved through the model'
    } else {
        Write-Skip 'no in-document anchor link among the realized blocks'
    }

    if ($externalClicked) {
        $would = @(Get-LogLines) -match 'link would open externally: '
        Assert-True ($would.Count -ge 1) `
            "the external link $($externalClicked.Url) was resolved and logged"
        $launched = @(Get-LogLines) -match 'link opened externally: '
        Assert-Equal 0 $launched.Count 'no browser was launched by a verification run'
    } else {
        Write-Skip 'no external link among the realized blocks'
    }

    #  Clicking where there is no link must not be reported as a click.
    $ack = Send-Cmd 'click-link 2000'
    Assert-True ($ack.Line -match 'no link 0 in block 2000') `
        'a block with no controls reports no link rather than throwing'

    $errors = @(Get-LogLines) -match 'Unhandled|ObjectDisposed|cmd .* -> error'
    Assert-Equal 0 $errors.Count 'no exceptions in the log'
}
finally {
    Stop-MarkLite
    if ($savedClipboard) {
        Set-Clipboard -Value $savedClipboard
    }
}

Exit-WithSummary 'test-selection'
