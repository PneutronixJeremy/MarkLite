<#
.SYNOPSIS
    Live reload survives a file watcher that stopped delivering.

.DESCRIPTION
    After sleep and resume a FileSystemWatcher can go quiet without raising an
    error, and the open document then never reloads again. The app no longer
    relies on the watcher alone: a periodic stamp check (every 10 s) catches a
    change the watcher missed and rebuilds it, and a resume rebuilds every
    watcher outright.

    Sleep cannot be scripted, so the debug command "watcher-mute" stands in for
    it: the active tab's watcher stays in place but stops raising events.

      safety net - mute, write: the reload must still arrive, reported by the
                   stamp check ("watcher missed change"), within one tick.
      rebuilt    - write again: the rebuilt watcher reports it itself, quickly,
                   with no stamp-check line.
      rewatch    - mute, write, then "rewatch" (what a detected resume runs):
                   the change made while deaf is picked up at once.

    Nothing is typed or clicked: edits are file writes.

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

$workDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-verify/watcher-recovery'
[void][IO.Directory]::CreateDirectory($workDir)
$file = Join-Path $workDir 'watcher-fixture.md'

<#  N one-line paragraphs, blank-line separated: N blocks, so the block count
    says which version of the file is on screen. #>
function Write-Fixture {
    param([int]$Paragraphs)
    $lines = foreach ($n in 1..$Paragraphs) {
        "Paragraph $n of $Paragraphs in the watcher fixture."
        ''
    }
    [IO.File]::WriteAllLines($file, [string[]]$lines)
}

<#  Waits for a reload after $Since; returns the seconds it took, or $null on
    timeout (a missed reload is a FAIL, not an exception). #>
function Wait-Reload {
    param([int]$Since, [int]$TimeoutSec)
    $started = [DateTime]::UtcNow
    try {
        [void](Wait-Log -Pattern 'reload triggered' -TimeoutSec $TimeoutSec -Since $Since)
        return ([DateTime]::UtcNow - $started).TotalSeconds
    }
    catch {
        return $null
    }
}

function Test-LogSince {
    param([int]$Since, [string]$Pattern)
    $lines = @(Get-LogLines)
    for ($i = $Since; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $Pattern) {
            return $true
        }
    }
    return $false
}

Write-Section 'test-watcher-recovery: reload after the watcher goes deaf'

try {
    Write-Fixture 10
    [void](Start-MarkLite -File $file -LogName 'test-watcher-recovery')
    Assert-Equal 10 (Get-State).blocks 'fixture opened'

    # ---------------------------------------------------------- safety net
    [void](Send-Cmd 'watcher-mute')
    $since = Get-LogCount
    Write-Fixture 20
    $seconds = Wait-Reload -Since $since -TimeoutSec 25
    Assert-True ($null -ne $seconds) "muted watcher: change still reloaded ($([math]::Round([double]$seconds, 1)) s)"
    Assert-True (Test-LogSince $since 'watcher missed change') 'the stamp check is what caught it'
    Assert-Equal 20 (Get-State).blocks 'new content on screen'

    # ------------------------------------------------------------- rebuilt
    $since = Get-LogCount
    Write-Fixture 30
    $seconds = Wait-Reload -Since $since -TimeoutSec 5
    Assert-True ($null -ne $seconds) "rebuilt watcher reports on its own ($([math]::Round([double]$seconds, 1)) s)"
    Assert-True (-not (Test-LogSince $since 'watcher missed change')) 'no stamp-check rescue needed'
    Assert-Equal 30 (Get-State).blocks 'new content on screen'

    # ------------------------------------------------------------- rewatch
    [void](Send-Cmd 'watcher-mute')
    $since = Get-LogCount
    Write-Fixture 40
    [void](Send-Cmd 'rewatch')
    $seconds = Wait-Reload -Since $since -TimeoutSec 5
    Assert-True ($null -ne $seconds) "rewatch picked up the change made while deaf ($([math]::Round([double]$seconds, 1)) s)"
    Assert-Equal 40 (Get-State).blocks 'new content on screen'

    $errors = @(Get-LogLines) -match 'Unhandled|ObjectDisposed|cmd .* -> error|watcher error'
    Assert-Equal 0 $errors.Count 'no exceptions in the log'
}
finally {
    Stop-MarkLite
}

Exit-WithSummary 'test-watcher-recovery'
