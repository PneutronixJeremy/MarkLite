<#
.SYNOPSIS
    Tab behaviour on the published app: handoff, independent scroll, closing.

.DESCRIPTION
    Opens three documents (the first as the primary launch, the rest as
    secondary launches that hand their path over the single-instance pipe),
    then asserts that each tab keeps its own scroll position across switches
    and that closing every tab lands back on the welcome page.

    Only the active tab holds a rendered tree, so a switch is a re-render:
    every "tab <n>" here waits for the render to finish before reading state,
    and the mermaid document is visited repeatedly on purpose — dropping and
    rebuilding a diagram is what used to throw ObjectDisposedException.

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

<#  Switches tabs and waits until the activation render has settled: the
    command acknowledgement only says the switch started. "tab switched" is
    written last on purpose (see ActivateTab), so it is the finish line.
    Returns the log index the switch started at, so a caller can scan just the
    lines it produced. #>
function Switch-Tab {
    param([int]$Index)

    $before = Get-LogCount
    [void](Send-Cmd "tab $Index")
    [void](Wait-Log -Pattern 'tab switched' -Since $before)
    return $before
}

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
    [void](Switch-Tab 1)
    [void](Send-Cmd 'scroll-page 2')
    $parked = (Get-ActiveTabState (Get-State)).scrollY
    Assert-True ($parked -gt 0) "tab 1 scrolled away from the top (offset $parked)"

    [void](Switch-Tab 0)
    $other = (Get-ActiveTabState (Get-State)).scrollY
    Assert-Equal 0 $other 'tab 0 still sits at its own offset 0'

    #  One live document per window: leaving a tab gives its control tree back.
    #  (A hidden viewer never lays out, so its stale ScrollViewer extent proves
    #  nothing — the deactivation log line is the assertable signal.)
    $dropped = @(Get-LogLines) -match 'tree dropped'
    Assert-True ($dropped.Count -ge 1) 'leaving a tab drops its rendered tree'

    [void](Switch-Tab 1)
    $restored = (Get-ActiveTabState (Get-State)).scrollY
    Assert-Near $parked $restored 1 'tab 1 offset restored after switching away and back'

    #  Mermaid round-trips: tab 0's document holds a diagram, so each visit
    #  builds one and each departure detaches it. The packaged renderer threw
    #  ObjectDisposedException on the second detach; five round-trips would hit
    #  that four times over.
    $before = Get-LogCount
    for ($i = 0; $i -lt 5; $i++) {
        [void](Switch-Tab 0)
        [void](Switch-Tab 1)
    }
    $errors = @(Get-LogLines | Select-Object -Skip $before) -match 'ObjectDisposed|Unhandled|error '
    Assert-Equal 0 $errors.Count 'five mermaid round-trips without an exception'
    Assert-Equal 3 (Get-State).tabs.Count 'all tabs still open after the round-trips'

    #  Switch cost is the price of holding one document at a time; keep it
    #  visible rather than implicit.
    $switches = @(Get-LogLines | Select-Object -Skip $before) -match 'tab switched .*render (\d+) ms'
    if ($switches.Count -gt 0) {
        $slowest = ($switches | ForEach-Object {
            [int][regex]::Match($_, 'render (\d+) ms').Groups[1].Value
        } | Measure-Object -Maximum).Maximum
        Write-Host "  slowest switch render: $slowest ms" -ForegroundColor DarkGray
    }

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
