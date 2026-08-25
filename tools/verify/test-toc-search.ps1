<#
.SYNOPSIS
    Contents sidebar and find-in-document on the published app.

.DESCRIPTION
    Drives the same code paths the sidebar buttons and the find bar use, over
    the debug command channel. Two halves:

      TOC     - "toc <n>" must scroll the document and make <n> the current
                section; "anchor <slug>" must land on that heading.
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
    $state = Get-State
    Assert-True ($state.tocCount -gt 0) "contents built ($($state.tocCount) headings)"

    $target = [Math]::Min(5, $state.tocCount - 1)
    [void](Send-Cmd "toc $target")
    $state = Get-State
    Assert-True ((Get-ActiveTabState $state).scrollY -gt 0) "toc $target scrolled the document"
    Assert-Equal $target $state.tocIndex "toc $target became the current section"

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

    # ------------------------------------------------------------- search
    $expected = Get-SourceMatchCount -Path $File -Needle $Term
    Assert-True ($expected -gt 1) "test term '$Term' occurs $expected times in the source"

    $ack = Send-Cmd "find $Term"
    $reported = [int]([regex]::Match($ack.Line, '(\d+) matches').Groups[1].Value)
    Assert-Equal $expected $reported "find '$Term' reports the source match count"

    $state = Get-State
    Assert-Equal 0 $state.matchIndex 'find starts on the first match'
    Assert-Equal $expected $state.matches 'dump-state agrees on the match count'

    [void](Send-Cmd 'find-next')
    $state = Get-State
    Assert-Equal 1 $state.matchIndex 'find-next advances the current match'

    [void](Send-Cmd 'find-close')
    $state = Get-State
    Assert-Equal 0 $state.matches 'closing the find bar clears the matches'
}
finally {
    Stop-MarkLite
}

Exit-WithSummary 'test-toc-search'
