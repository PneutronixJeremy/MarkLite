<#
.SYNOPSIS
    Live reload: the reader stays put, and only
    what actually changed is rebuilt.

.DESCRIPTION
    A reload re-parses the document into a brand new block list, so the block
    the reader was on has a different index afterwards. The viewer aligns the
    old block list against the new one and carries over the controls of every
    realized block whose text did not change.

    The document is generated here rather than taken from testdata/, because
    the assertions need a block list whose numbering is knowable from the
    outside: every block is exactly one line, blank-line separated, so block k
    is line 2k and no two blocks have the same text. The stress fixture repeats
    itself on purpose and cannot answer "which block is this line".

    Three edits, each made while the reader sits in the middle of the document:

      insert  - 50 paragraphs added at the very top. Every block shifts by 50,
                and the reader must end up on the same paragraph, further down
                the document in pixels, with every realized container carried
                over untouched.
      edit    - the paragraph the reader is ON is rewritten. Exactly one
                container must be rebuilt, and the reader must not be moved.
      delete  - that same paragraph removed. Its text is gone, so there is
                nothing to find by content; the check is that the fallback puts
                the reader at the same index without an exception.

    Nothing is typed or clicked: edits are file writes, and the app's own file
    watcher is what notices them.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).
#>
[CmdletBinding()]
param(
    [string]$Exe
)

. "$PSScriptRoot/common.ps1"

if ($Exe) {
    Set-MarkLiteExe $Exe
}

#  A private working copy: this script rewrites the file it opens three times.
$workDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-verify/reload'
[void][IO.Directory]::CreateDirectory($workDir)
$file = Join-Path $workDir 'reload-fixture.md'

<#  200 sections of one heading and five paragraphs: 1200 top-level blocks,
    each one line, each line unique, blank line between. Fictional filler in
    the same spirit as testdata/. #>
$lines = [System.Collections.Generic.List[string]]::new()
foreach ($section in 1..200) {
    $lines.Add("## Section $section")
    $lines.Add('')
    foreach ($paragraph in 1..5) {
        $lines.Add("Paragraph $section.$paragraph records the calibration note for bay $section, run $paragraph.")
        $lines.Add('')
    }
}
[IO.File]::WriteAllLines($file, $lines)

<#  A jump aims partly at estimated block heights and re-aims once the blocks
    it realized have measured, so a state read straight afterwards catches the
    view mid-flight. Sample until it stops moving. #>
function Get-SettledState {
    $state = Get-State
    foreach ($attempt in 1..8) {
        $next = Get-State
        if ((Get-ActiveTabState $next).scrollY -eq (Get-ActiveTabState $state).scrollY) {
            return $next
        }
        $state = $next
    }
    return $state
}

<#  Writes the file and waits for the app to finish reloading it. The watcher
    debounces for 150 ms and the render posts its scroll restore, so the
    settled point is the "scroll restored" line, not the write returning. #>
function Set-FixtureText {
    param([string[]]$Text, [string]$What)

    $before = Get-LogCount
    [IO.File]::WriteAllLines($file, $Text)
    [void](Wait-Log -Pattern 'reload triggered' -TimeoutSec 30 -Since $before)
    $restored = Wait-Log -Pattern 'scroll restored' -TimeoutSec 30 -Since $before
    Write-Host "  $What -> $($restored.Line)" -ForegroundColor DarkGray
    return $before
}

<#  "reload: reused <n> of <m> containers, <a> of <b> blocks aligned". #>
function Get-ReuseCounts {
    param([int]$Since)

    $pattern = 'reload: reused (\d+) of (\d+) containers, (\d+) of (\d+) blocks aligned'
    $line = Wait-Log -Pattern $pattern -TimeoutSec 30 -Since $Since
    return [pscustomobject]@{
        Reused  = [int]$line.Match.Groups[1].Value
        Total   = [int]$line.Match.Groups[2].Value
        Aligned = [int]$line.Match.Groups[3].Value
        Blocks  = [int]$line.Match.Groups[4].Value
    }
}

Write-Section 'test-reload: generated document, edited under the reader'

try {
    [void](Start-MarkLite -File $file -LogName 'test-reload')

    $state = Get-State
    Assert-Equal 1200 $state.blocks 'the generated document is 1200 blocks'

    #  Well into the document, so an edit at the top is outside the
    #  realization window and the reader has somewhere to be moved from.
    [void](Send-Cmd 'toc 60')
    $state = Get-SettledState
    $anchorBlock = $state.firstVisibleBlock
    $scrollBefore = (Get-ActiveTabState $state).scrollY
    Assert-True ($anchorBlock -gt 300) "reader parked on block $anchorBlock"

    # --------------------------------------------------------------- insert
    $probe = [System.Collections.Generic.List[string]]::new()
    foreach ($n in 1..50) {
        $probe.Add("Reload probe paragraph $n, inserted above everything else.")
        $probe.Add('')
    }
    $inserted = @($probe) + @($lines)
    $since = Set-FixtureText -Text $inserted -What 'insert 50 paragraphs at the top'

    $state = Get-SettledState
    Assert-Equal 1250 $state.blocks '50 paragraphs became 50 new blocks'
    Assert-Equal ($anchorBlock + 50) $state.firstVisibleBlock `
        'the reader stayed on the same paragraph, renumbered by the insert'
    Assert-True ((Get-ActiveTabState $state).scrollY -gt $scrollBefore) `
        "the anchor moved down the document ($([int]$scrollBefore) -> $([int](Get-ActiveTabState $state).scrollY) px)"

    $reuse = Get-ReuseCounts -Since $since
    Assert-Equal 1200 $reuse.Aligned 'every block of the old document was found in the new one'
    Assert-Equal $reuse.Total $reuse.Reused `
        "an edit outside the viewport rebuilt nothing ($($reuse.Reused) of $($reuse.Total) containers carried over)"

    # ----------------------------------------------------------------- edit
    #  Block k is line 2k by construction, so the block the reader is on can be
    #  edited without guessing.
    $anchorNow = $anchorBlock + 50
    $anchorLine = 2 * $anchorNow
    $original = $inserted[$anchorLine]
    Assert-True ($original -match '^Paragraph ') `
        "block $anchorNow is source line $($anchorLine + 1): '$original'"

    $edited = @($inserted)
    $edited[$anchorLine] = 'Rewritten by test-reload: the only block in the document that changed.'
    $since = Set-FixtureText -Text $edited -What 'rewrite the paragraph under the reader'

    $reuse = Get-ReuseCounts -Since $since
    Assert-Equal 1249 $reuse.Aligned 'exactly one block failed to align'
    Assert-Equal ($reuse.Total - 1) $reuse.Reused `
        "only the edited block was rebuilt ($($reuse.Reused) of $($reuse.Total) containers carried over)"

    $state = Get-SettledState
    Assert-Equal $anchorNow $state.firstVisibleBlock `
        'rewriting the anchor block itself did not move the reader'

    # --------------------------------------------------------------- delete
    #  The anchor's text no longer exists anywhere, so there is nothing to find
    #  by content: the fallback is the block's old index.
    $deleted = @($edited[0..($anchorLine - 1)]) + @($edited[($anchorLine + 2)..($edited.Count - 1)])
    [void](Set-FixtureText -Text $deleted -What 'delete that paragraph')

    $state = Get-SettledState
    Assert-Equal 1249 $state.blocks 'the deleted paragraph is gone from the model'
    Assert-True ([Math]::Abs($state.firstVisibleBlock - $anchorNow) -le 1) `
        "the old index was used as the fallback ($($state.firstVisibleBlock))"
    Assert-True ((Get-ActiveTabState $state).scrollY -gt 0) `
        "the reader kept a place in the document ($([int](Get-ActiveTabState $state).scrollY) px)"

    #  The contents sidebar is model-backed, so it must have survived all three.
    Assert-Equal 200 $state.tocCount 'contents still complete'

    $errors = @(Get-LogLines) -match 'Unhandled|ObjectDisposed|cmd .* -> error'
    Assert-Equal 0 $errors.Count 'no exceptions in the log'
}
finally {
    Stop-MarkLite
}

Exit-WithSummary 'test-reload'
