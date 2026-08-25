<#
.SYNOPSIS
    Tab behaviour on the published app: handoff, independent scroll, closing.

.DESCRIPTION
    Opens three documents (the first as the primary launch, the rest as
    secondary launches that hand their path over the single-instance pipe),
    then asserts that each tab keeps its own scroll position across switches
    and that closing every tab lands back on the welcome page.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).
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
#  Comma-separated as well as space-separated: see the note in
#  measure-memory.ps1 about "pwsh -File" argument binding.
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Files -or $Files.Count -lt 3) {
    $repo = (git rev-parse --show-toplevel)
    $Files = @(
        (Join-Path $repo 'testdata/sample.md'),
        (Join-Path $repo 'testdata/sample-plan.md'),
        (Join-Path $repo 'testdata/stress-large.md')
    )
}

Write-Section 'test-tabs'

try {
    [void](Start-MarkLite -File $Files[0] -LogName 'test-tabs')

    $state = Get-State
    Assert-Equal 1 $state.tabs.Count 'primary launch opens one tab'

    for ($i = 1; $i -lt $Files.Count; $i++) {
        $before = Get-LogCount
        [void](Open-InMarkLite -File $Files[$i])
        [void](Wait-Log -Pattern 'handoff received' -Since $before)
        Write-Pass "secondary launch handed off $([IO.Path]::GetFileName($Files[$i]))"
    }

    $state = Get-State
    Assert-Equal $Files.Count $state.tabs.Count 'every file has a tab'
    Assert-Equal ($Files.Count - 1) $state.activeTab 'the newest tab is active'

    #  Independent scroll: park tab 1 in the middle of its document, visit
    #  another tab, come back, and the offset must survive.
    [void](Send-Cmd 'tab 1')
    [void](Send-Cmd 'scroll-page 2')
    $parked = (Get-ActiveTabState (Get-State)).scrollY
    Assert-True ($parked -gt 0) "tab 1 scrolled away from the top (offset $parked)"

    [void](Send-Cmd 'tab 0')
    $other = (Get-ActiveTabState (Get-State)).scrollY
    Assert-Equal 0 $other 'tab 0 still sits at its own offset 0'

    [void](Send-Cmd 'tab 1')
    $restored = (Get-ActiveTabState (Get-State)).scrollY
    Assert-Near $parked $restored 1 'tab 1 offset restored after switching away and back'

    #  Closing: every tab goes, then the welcome page comes back.
    for ($i = 0; $i -lt $Files.Count; $i++) {
        [void](Send-Cmd 'close-tab')
    }
    $before = Get-LogCount
    $state = Get-State
    Assert-Equal 0 $state.tabs.Count 'all tabs closed'
    Assert-Equal (-1) $state.activeTab 'no active tab after closing all'
    $welcome = @(Get-LogLines) -match 'welcome state'
    Assert-True ($welcome.Count -ge 1) 'welcome state restored'
}
finally {
    Stop-MarkLite
}

Exit-WithSummary 'test-tabs'
