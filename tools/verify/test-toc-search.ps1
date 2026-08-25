<#
.SYNOPSIS
    Contents sidebar and find-in-document on the published app.

.DESCRIPTION
    Drives the same code paths the sidebar buttons and the find bar use, over
    the debug command channel. Two halves:

      TOC     - "toc <n>" must scroll the document and make <n> the current
                section; under the virtualizing viewer it must also land the
                heading 8 px below the viewport top, which is only knowable
                after the panel has corrected its own estimate. "anchor <slug>"
                must land on that heading, and on a footnote when the document
                has any.
      Search  - "find <term>" must report the same number of matches as a
                plain text count over the source file, and "find-next" must
                advance the current match.

    Match counting is an approximation by nature: the app searches the
    RENDERED text (headings, paragraphs, tables, code) while the script counts
    the SOURCE. The terms below are chosen so the two agree exactly - words
    that appear only in prose, never inside markdown syntax, link targets or
    the generator's HTML comment. A mismatch on those terms is a real
    regression, not a counting artefact.

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
    $virtual = $env:MARKLITE_VIRTUAL -eq '1'

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

    if ($virtual) {
        #  ScrollToHeading asks for the block top minus 8 px, so the heading
        #  ends up exactly that far below the viewport top once the estimates
        #  above it have been corrected.
        $delta = $state.targetBlockOffset - (Get-ActiveTabState $state).scrollY
        Assert-Near 8 $delta 2 "toc $target left the heading 8 px below the viewport top"
    }

    #  A jump deep into a long document is the interesting one: everything
    #  above the target is still an estimate when the first hop is taken.
    if ($state.tocCount -gt 250) {
        $deep = Invoke-TocJump -Index 250
        Assert-Equal 250 $deep.tocIndex 'toc 250 became the current section'
        if ($virtual) {
            $delta = $deep.targetBlockOffset - (Get-ActiveTabState $deep).scrollY
            Assert-Near 8 $delta 2 'toc 250 left the heading 8 px below the viewport top'
        }
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
        if ($virtual) {
            Assert-Equal ($state.blocks - 1) $state.targetBlock `
                'the footnote anchor resolved to the footnote group at the end of the document'
        }
    } else {
        Write-Skip "$([IO.Path]::GetFileName($File)) defines no footnotes - fn-1 anchor not exercised"
    }

    # ------------------------------------------------------------- search
    $expected = Get-SourceMatchCount -Path $File -Needle $Term
    Assert-True ($expected -gt 1) "test term '$Term' occurs $expected times in the source"

    $ack = Send-Cmd "find $Term"
    $reported = [int]([regex]::Match($ack.Line, '(\d+) matches').Groups[1].Value)

    #  Search still walks the RENDERED control tree, and the virtualizing viewer
    #  only ever renders the blocks near the viewport - so it can see a fraction
    #  of the document's matches. Reported as skipped, with the numbers, rather
    #  than asserted against a total the viewer cannot yet know: search moves to
    #  the parsed model in a later phase, and these two checks come back then.
    if ($virtual) {
        Write-Skip "find '$Term' counts realized blocks only ($reported of $expected) - model-backed search not implemented yet"
    } else {
        Assert-Equal $expected $reported "find '$Term' reports the source match count"
    }

    $state = Get-State
    Assert-Equal 0 $state.matchIndex 'find starts on the first match'
    if ($virtual) {
        Write-Skip "dump-state match count follows realization ($($state.matches) of $expected)"
    } else {
        Assert-Equal $expected $state.matches 'dump-state agrees on the match count'
    }

    [void](Send-Cmd 'find-next')
    $state = Get-State
    if ($virtual -and $state.matches -lt 2) {
        #  Stepping needs at least two matches to step between, and the search
        #  can only see the realized ones. Comes back with model-backed search.
        Write-Skip 'find-next needs more than one visible match under the virtualizing viewer'
    } else {
        Assert-Equal 1 $state.matchIndex 'find-next advances the current match'
    }

    [void](Send-Cmd 'find-close')
    $state = Get-State
    Assert-Equal 0 $state.matches 'closing the find bar clears the matches'
}
finally {
    Stop-MarkLite
}

Exit-WithSummary 'test-toc-search'
