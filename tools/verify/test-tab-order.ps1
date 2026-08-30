<#
.SYNOPSIS
    Tab reordering: the order everything reads — strip, session, close-tab's
    neighbour — moves as one, and comes back after a restart.

.DESCRIPTION
    Dragging a tab along the strip is pointer plumbing no script here may
    inject; `move-tab <from> <to>` runs the same MoveTab a pointer crossing a
    neighbour does, so everything after the pointer is covered:

    - the active tab moved to the front is still the active tab, and each
      tab's content (`chars`) travels with its name;
    - a move to the same slot changes nothing (`unchanged`); an out-of-range
      index is `ignored` and the order is intact;
    - a reorder is saved to the session at once, and a relaunch that keeps the
      session reopens the tabs in the NEW order with the same tab active;
    - close-tab picks its right-hand neighbour by the new order.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).

.PARAMETER Files
    Three documents. Defaults to sample.md, sample-plan.md, stress-large.md.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string[]]$Files
)

. "$PSScriptRoot/common.ps1"

if ($Exe) {
    Set-MarkLiteExe $Exe
}
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Files -or $Files.Count -lt 3) {
    $repo = (git rev-parse --show-toplevel)
    $Files = @(
        (Join-Path $repo 'testdata/sample.md'),
        (Join-Path $repo 'testdata/sample-plan.md'),
        (Join-Path $repo 'testdata/stress-large.md')
    )
}

function Get-TabNames {
    param($State)
    return @($State.tabs | ForEach-Object { $_.name })
}

function Get-ActiveName {
    param($State)
    return (Get-ActiveTabState $State).name
}

Write-Section 'test-tab-order: move-tab'

try {
    [void](Start-MarkLite -File $Files[0] -LogName 'test-tab-order')
    for ($i = 1; $i -lt $Files.Count; $i++) {
        $before = Get-LogCount
        [void](Open-InMarkLite -File $Files[$i])
        [void](Wait-Log -Pattern 'handoff received' -Since $before)
    }
    $state = Get-State
    $a, $b, $c = Get-TabNames $state
    Assert-Equal 3 $state.tabs.Count 'three tabs open'
    Assert-Equal 2 $state.activeTab 'the last one opened is active'
    #  Content per name, so a later state can prove the tabs moved whole.
    $chars = @{}
    foreach ($tab in $state.tabs) {
        $chars[$tab.name] = $tab.chars
    }
    Assert-True (($chars.Values | Sort-Object -Unique).Count -eq 3) 'the three documents differ in length, so chars identify them'

    # ------------------------------------------------- active tab to front
    $ack = Send-Cmd 'move-tab 2 0'
    Assert-True ($ack.Line -match "moved '$([regex]::Escape($c))' 2 -> 0") "move-tab 2 0 moved '$c' ($($ack.Line -replace '.*-> ',''))"
    $state = Get-State
    Assert-Equal "$c,$a,$b" ((Get-TabNames $state) -join ',') 'the order is c, a, b'
    Assert-Equal 0 $state.activeTab 'the moved tab is still the active one, now at 0'
    $intact = $true
    foreach ($tab in $state.tabs) {
        if ($chars[$tab.name] -ne $tab.chars) {
            $intact = $false
        }
    }
    Assert-True $intact 'each tab kept its own content'
    $moved = @(Get-LogLines) -match "tab moved '$([regex]::Escape($c))' 2 -> 0"
    Assert-Equal 1 $moved.Count 'the move is logged'

    # ---------------------------------------------------- no-ops and misses
    [void](Send-Cmd 'move-tab 0 2')
    $state = Get-State
    Assert-Equal "$a,$b,$c" ((Get-TabNames $state) -join ',') 'move-tab 0 2 puts a, b, c back'
    Assert-Equal 2 $state.activeTab 'and the active tab followed to 2'

    $ack = Send-Cmd 'move-tab 1 1'
    Assert-True ($ack.Line -match '-> unchanged$') 'a move to the same slot answers unchanged'
    $ack = Send-Cmd 'move-tab 7 0'
    Assert-True ($ack.Line -match '-> ignored \(3 tabs\)$') 'an out-of-range index is ignored'
    $ack = Send-Cmd 'move-tab 0 -1'
    Assert-True ($ack.Line -match '-> ignored \(3 tabs\)$') 'so is a negative one'
    $state = Get-State
    Assert-Equal "$a,$b,$c" ((Get-TabNames $state) -join ',') 'the order is intact after the misses'

    # ----------------------------------------------------- saved at once
    $before = Get-LogCount
    [void](Send-Cmd 'move-tab 0 2')
    $state = Get-State
    Assert-Equal "$b,$c,$a" ((Get-TabNames $state) -join ',') 'move-tab 0 2 gives b, c, a'
    Assert-Equal 1 $state.activeTab "the active tab ($c) slid to 1 as a passed it"
    Assert-Equal $c (Get-ActiveName $state) 'and it is still c'
    Assert-Equal 3 $state.sessionCount 'the session store holds three tabs'
    $saved = @(Get-LogLines | Select-Object -Skip $before) -match 'session saved: 3 tabs, active 1'
    Assert-True ($saved.Count -ge 1) 'the reorder was saved with the active index that matches'
}
finally {
    Stop-MarkLite
}

# ------------------------------------------------------------ persistence
Write-Section 'test-tab-order: the order survives a restart'

try {
    [void](Start-MarkLite -KeepSession -LogName 'test-tab-order-restore')
    $state = Get-State
    Assert-Equal 3 $state.tabs.Count 'relaunch reopens three tabs'
    Assert-Equal "$b,$c,$a" ((Get-TabNames $state) -join ',') 'in the reordered sequence b, c, a'
    Assert-Equal $c (Get-ActiveName $state) 'with c active, as it was'
    Assert-Equal 1 $state.activeTab 'at index 1'

    # ------------------------------------------ close-tab by the new order
    <#  Closing the first tab must land on what is now to its right — c —
        not on whatever used to sit beside it before the reorder. #>
    [void](Send-Cmd 'tab 0')
    $state = Get-State
    Assert-Equal $b (Get-ActiveName $state) "tab 0 is $b"
    [void](Send-Cmd 'close-tab')
    $state = Get-State
    Assert-Equal 2 $state.tabs.Count 'two tabs left'
    Assert-Equal "$c,$a" ((Get-TabNames $state) -join ',') 'c, a remain in order'
    Assert-Equal $c (Get-ActiveName $state) 'close-tab landed on the right-hand neighbour by the new order'

    $errors = @(Get-LogLines) -match 'Unhandled|ObjectDisposed|cmd .* -> error'
    Assert-Equal 0 $errors.Count 'no exceptions in the log'
}
finally {
    Stop-MarkLite
}

Exit-WithSummary 'test-tab-order'
