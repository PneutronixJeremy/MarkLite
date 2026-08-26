<#
.SYNOPSIS
    Reopen last session: the tabs that were open come back, on the same block.

.DESCRIPTION
    Closes the window with WM_CLOSE and relaunches it repeatedly, asserting
    what comes back each time: the same files in the same order, the same
    active tab, the same reading position, a file argument opening on top of
    the session, deleted files dropped silently, and the setting turning the
    whole thing off.

    The documents are copies in the temp directory, because one of them gets
    deleted mid-run. The session store is scoped to this instance group
    (MARKLITE_INSTANCE), so nothing here can reach the user's own tabs, and
    -KeepSession is what stops Start-MarkLite from clearing it between the
    relaunches this check is built out of.

    Position is compared as firstVisibleBlock, never as a pixel offset: an
    offset above the viewport is an estimate and legitimately differs between
    two renders of the same document.

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

$repo = (git rev-parse --show-toplevel)
$workDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-session'
[void][IO.Directory]::CreateDirectory($workDir)

#  Copies, not the fixtures themselves: the "file went away" case deletes one.
$files = @('sample.md', 'sample-plan.md', 'stress-large.md') | ForEach-Object {
    $target = Join-Path $workDir $_
    Copy-Item -LiteralPath (Join-Path $repo "testdata/$_") -Destination $target -Force
    $target
}
#  Not one of the three copies: the argument has to be a file the session does
#  not already contain, or it would focus an existing tab instead of adding one.
$argumentFile = Join-Path $repo 'testdata/sample-html.md'

Write-Section 'test-session'

function Get-TabPaths {
    param($State)
    return @($State.tabs | ForEach-Object { $_.path })
}

try {
    #  --------------------------------------------------------- record a session
    [void](Start-MarkLite -File $files[0] -LogName 'test-session')
    foreach ($file in $files[1..2]) {
        $before = Get-LogCount
        [void](Open-InMarkLite -File $file)
        [void](Wait-Log -Pattern 'handoff received' -Since $before)
    }

    $state = Get-State
    Assert-Equal 3 $state.tabs.Count 'three tabs open before the close'
    Assert-True $state.restoreSession 'reopen last session defaults to on'

    #  Park the active tab well inside its document; the block it lands on is
    #  what the relaunch has to reproduce.
    $before = Get-LogCount
    [void](Send-Cmd 'scroll-page 3')
    #  The store is written behind a debounce, so wait for the write rather
    #  than for a guess at how long it takes.
    [void](Wait-Log -Pattern 'session saved' -Since $before)
    $state = Get-State
    $parkedBlock = $state.firstVisibleBlock
    $parkedPaths = Get-TabPaths $state
    $parkedActive = $state.activeTab
    Assert-True ($parkedBlock -gt 0) "active tab parked on block $parkedBlock"
    Assert-Equal 3 $state.sessionCount 'the session store holds three tabs'

    Stop-MarkLite

    #  --------------------------------------------------------- plain relaunch
    [void](Start-MarkLite -KeepSession -LogName 'test-session-restore')
    #  Putting the reader back takes a few layout passes after the window is
    #  up, so the state read has to wait for the app to say it landed.
    [void](Wait-Log -Pattern 'session scroll restored')
    $state = Get-State
    Assert-Equal 3 $state.tabs.Count 'relaunch reopens three tabs'
    Assert-Equal ($parkedPaths -join '|') ((Get-TabPaths $state) -join '|') 'same files in the same order'
    Assert-Equal $parkedActive $state.activeTab 'the same tab is active'
    Assert-Equal $parkedBlock $state.firstVisibleBlock 'the active tab is back on the same block'

    Stop-MarkLite

    #  --------------------------------------------- relaunch with an argument
    [void](Start-MarkLite -KeepSession -File $argumentFile -LogName 'test-session-argument')
    $state = Get-State
    Assert-Equal 4 $state.tabs.Count 'a file argument opens on top of the session'
    Assert-Equal 3 $state.activeTab 'the argument is the active tab'
    Assert-Equal ([IO.Path]::GetFullPath($argumentFile)) (Get-ActiveTabState $state).path `
        'the active tab is the file that was passed'

    Stop-MarkLite

    #  ------------------------------------------------------ a file went away
    Remove-Item -LiteralPath $files[1] -Force
    [void](Start-MarkLite -KeepSession -LogName 'test-session-missing')
    $state = Get-State
    Assert-Equal 3 $state.tabs.Count 'the deleted file is dropped, the rest come back'
    $skipped = @(Get-LogLines) -match ('session: skipped missing ' + [regex]::Escape($files[1]))
    Assert-True ($skipped.Count -ge 1) 'the skipped file is named in the log'
    $errorTabs = @($state.tabs | Where-Object { -not $_.path })
    Assert-Equal 0 $errorTabs.Count 'no "cannot open file" tab was restored'
    Assert-Equal 3 $state.sessionCount 'the store was rewritten without the missing file'

    #  ------------------------------------------------------ turning it off
    [void](Send-Cmd 'session off')
    $state = Get-State
    Assert-True (-not $state.restoreSession) 'the setting reports off'
    Assert-Equal 0 $state.sessionCount 'turning it off clears the stored session'

    Stop-MarkLite

    [void](Start-MarkLite -KeepSession -LogName 'test-session-off')
    $state = Get-State
    Assert-Equal 0 $state.tabs.Count 'with the setting off, nothing is reopened'
    $welcome = @(Get-LogLines) -match 'welcome state'
    Assert-True ($welcome.Count -ge 1) 'the welcome page is what comes back'
    Assert-Equal 0 $state.sessionCount 'and nothing is being stored'
}
finally {
    Stop-MarkLite
    Clear-MarkLiteSession
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

Exit-WithSummary 'test-session'
