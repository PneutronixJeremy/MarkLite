<#
.SYNOPSIS
    Contents sidebar and find-in-document on the published app.

.DESCRIPTION
    Drives the same code paths the sidebar buttons and the find bar use, over
    the debug command channel. Two halves:

      TOC     - "toc <n>" must scroll the document, make <n> the current
                section, and land the heading 8 px below the viewport top,
                which is only knowable after the panel has corrected its own
                estimate. "anchor <slug>" must land on that heading, and on a
                footnote when the document has any.
      Search  - "find <term>" must report the same number of matches as a
                plain text count over the source file, "find-next" must advance
                the current match, and each match stepped to must end up
                realized, highlighted and on screen - the count comes from the
                parsed document, so it does not care what is rendered, but the
                HIGHLIGHT does.

    Two counts are compared, and they mean different things. The count over the
    SOURCE file is an approximation by nature - the app searches what a reader
    can see, and the script strips the markup that is never drawn. The terms
    below are chosen so the two agree exactly: words that appear only in prose,
    never inside markdown syntax, link targets or the generator's HTML comment.
    The count over the app's own text projection ("dump-text") is not an
    approximation and is asserted as exact equality: it is the very text the
    search ran over.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$File,
    [string]$Term = 'phase'
)

. "$PSScriptRoot/common.ps1"

if ($Exe) {
    Set-MarkLiteExe $Exe
}
if (-not $File) {
    $File = Join-Path (git rev-parse --show-toplevel) 'testdata/sample-plan.md'
}

function Get-SourceMatchCount {
    param([string]$Path, [string]$Needle)

    $text = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).Path)
    #  HTML comments never reach the rendered text.
    $text = [regex]::Replace($text, '(?s)<!--.*?-->', '')
    #  Neither do link and image destinations - only their label is drawn. This
    #  matters: an anchor like "(#station-overview)" would otherwise be counted
    #  as a match for "station" that no viewer could ever highlight.
    $text = [regex]::Replace($text, '\]\([^)]*\)', ']')
    return ([regex]::Matches($text, [regex]::Escape($Needle), 'IgnoreCase')).Count
}

Write-Section "test-toc-search: $([IO.Path]::GetFileName($File))"

try {
    [void](Start-MarkLite -File $File -LogName 'test-toc-search')

    # ---------------------------------------------------------------- TOC
    $state = Get-State
    Assert-True ($state.tocCount -gt 0) "contents built ($($state.tocCount) headings)"

    <#  A jump aims at an offset built partly from estimated block heights, and
        the panel re-aims once the blocks it realized have measured. Sampling
        until the landing stops moving is what a reader's eye does too; the
        assertion is on where it settles. #>
    function Invoke-TocJump {
        param([int]$Index)

        [void](Send-Cmd "toc $Index")
        $s = Get-State
        foreach ($attempt in 1..5) {
            $next = Get-State
            if ((Get-ActiveTabState $next).scrollY -eq (Get-ActiveTabState $s).scrollY) {
                break
            }
            $s = $next
        }
        return $s
    }

    $target = [Math]::Min(5, $state.tocCount - 1)
    $state = Invoke-TocJump -Index $target
    Assert-True ((Get-ActiveTabState $state).scrollY -gt 0) "toc $target scrolled the document"
    Assert-Equal $target $state.tocIndex "toc $target became the current section"

    #  ScrollToHeading asks for the block top minus 8 px, so the heading ends
    #  up exactly that far below the viewport top once the estimates above it
    #  have been corrected.
    $delta = $state.targetBlockOffset - (Get-ActiveTabState $state).scrollY
    Assert-Near 8 $delta 2 "toc $target left the heading 8 px below the viewport top"

    #  A jump deep into a long document is the interesting one: everything
    #  above the target is still an estimate when the first hop is taken.
    if ($state.tocCount -gt 250) {
        $deep = Invoke-TocJump -Index 250
        Assert-Equal 250 $deep.tocIndex 'toc 250 became the current section'
        $delta = $deep.targetBlockOffset - (Get-ActiveTabState $deep).scrollY
        Assert-Near 8 $delta 2 'toc 250 left the heading 8 px below the viewport top'
    }

    [void](Send-Cmd 'scroll 0')
    $state = Get-State
    Assert-Equal 0 $state.tocIndex 'scrolling back to the top resets the current section'

    #  Anchors go through the slug table rather than the sidebar index.
    $tocLine = Wait-Log -Pattern "scroll-to-heading #$target '([^']*)'"
    $headingText = $tocLine.Match.Groups[1].Value
    $slug = ($headingText.ToLowerInvariant() -replace '[^a-z0-9 -]', '').Trim() -replace ' +', '-'
    $before = Get-LogCount
    [void](Send-Cmd "anchor $slug")
    $state = Get-State
    Assert-True ((Get-ActiveTabState $state).scrollY -gt 0) "anchor #$slug scrolled the document"
    $anchorLines = @(Get-LogLines)[$before..((Get-LogCount) - 1)] -match 'anchor link:'
    Assert-True ($anchorLines.Count -ge 1) "anchor #$slug resolved to a heading"

    <#  Footnotes are anchors too, and not heading ones: "fn-1" resolves
        through the model's anchor table to the footnote group at the very end
        of the document, which the panel then has to realize. Only checked on
        documents that define one. #>
    if ([IO.File]::ReadAllText((Resolve-Path -LiteralPath $File).Path) -match '(?m)^\[\^') {
        [void](Send-Cmd 'scroll 0')
        [void](Send-Cmd 'anchor fn-1')
        $state = Get-State
        $tab = Get-ActiveTabState $state
        Assert-True ($tab.scrollY -gt 0) "anchor #fn-1 scrolled to the footnotes ($([int]$tab.scrollY) px)"
        Assert-Equal ($state.blocks - 1) $state.targetBlock `
            'the footnote anchor resolved to the footnote group at the end of the document'
    } else {
        Write-Skip "$([IO.Path]::GetFileName($File)) defines no footnotes - fn-1 anchor not exercised"
    }

    # ------------------------------------------------------------- search
    #  Baseline for the memory check below: collected first, so it is compared
    #  against a like-for-like figure rather than against the garbage the TOC
    #  exercise above happened to leave behind.
    [void](Send-Cmd 'scroll 0')
    [void](Send-Cmd 'gc')
    $idle = Get-State

    $expected = Get-SourceMatchCount -Path $File -Needle $Term
    Assert-True ($expected -gt 1) "test term '$Term' occurs $expected times in the source"

    $ack = Send-Cmd "find $Term"
    $reported = [int]([regex]::Match($ack.Line, '(\d+) matches').Groups[1].Value)
    Assert-Equal $expected $reported "find '$Term' reports the source match count"

    $state = Get-State
    Assert-Equal 0 $state.matchIndex 'find starts on the first match'
    Assert-Equal $expected $state.matches 'dump-state agrees on the match count'

    <#  Exact equality, not an approximation: dump-text writes the model's own
        plain-text projection - the very text the search matched against - so a
        difference here is the search and the projection disagreeing, not
        markdown syntax getting in the way. #>
    [void](Send-Cmd 'dump-text')
    $dump = Join-Path ([IO.Path]::GetTempPath()) 'marklite-blocktext.txt'
    Assert-True (Test-Path -LiteralPath $dump) 'dump-text wrote the block projection'
    $projected = ([regex]::Matches(
        [IO.File]::ReadAllText($dump), [regex]::Escape($Term), 'IgnoreCase')).Count
    Assert-Equal $projected $reported "find '$Term' equals the projection's own count"

    Assert-True ($state.highlighted -gt 0) `
        "the current match is highlighted ($($state.highlighted) of $($state.matches) on screen)"
    if ($state.realizedBlocks -lt $state.blocks) {
        #  The count is the document's; the highlight can only be the realized
        #  part of it. Both at once is the whole point.
        Assert-True ($state.highlighted -lt $state.matches) `
            'matches outside the realized window are counted but not highlighted'
    } else {
        Write-Skip "$([IO.Path]::GetFileName($File)) is small enough to realize whole - no unhighlighted matches to check"
    }

    #  Search state is a match list plus split runs in the realized blocks;
    #  neither should register on the working set.
    [void](Send-Cmd 'gc')
    $searching = Get-State
    Assert-True (($searching.workingSetMb - $idle.workingSetMb) -lt 8) `
        ("search adds under 8 MB ({0:N1} -> {1:N1} MB)" -f $idle.workingSetMb, $searching.workingSetMb)

    <#  Stepping. Each step must advance the ordinal, and must also leave the
        target block realized and within the viewport - a match the reader
        cannot see has not been found for them. #>
    $steps = [Math]::Min(5, $expected - 1)
    foreach ($step in 1..$steps) {
        [void](Send-Cmd 'find-next')
        $state = Get-State
        Assert-Equal $step $state.matchIndex "find-next advanced to match $($step + 1)"
        Assert-True ($state.highlighted -gt 0) "match $($step + 1) is highlighted"
        Assert-True ($state.targetBlock -ge $state.firstRealized `
                -and $state.targetBlock -le $state.lastRealized) `
            "match $($step + 1) realized block $($state.targetBlock) (window $($state.firstRealized)..$($state.lastRealized))"
        <#  The jump aims 100 px above the match, so the target block's top sits
            at most that far below the viewport top; a match deep inside a tall
            code fence puts it above instead, never a whole viewport above. #>
        $tab = Get-ActiveTabState $state
        $delta = $state.targetBlockOffset - $tab.scrollY
        Assert-True ($delta -le 101 -and $delta -gt -$tab.viewport) `
            ("match $($step + 1) is inside the viewport (block top {0:N0} px from the top)" -f $delta)
    }

    [void](Send-Cmd 'find-close')
    $state = Get-State
    Assert-Equal 0 $state.matches 'closing the find bar clears the matches'
    Assert-Equal 0 $state.highlighted 'closing the find bar removes every highlight'
}
finally {
    Stop-MarkLite
}

Exit-WithSummary 'test-toc-search'
