<#
.SYNOPSIS
    Memory profile of the published app across a fixed document workout.

.DESCRIPTION
    Opens the given files as tabs, then walks the stages that historically move
    the working set: first render, opening further tabs, scrolling each
    document end-to-end, cycling tabs twice, and a forced collect. Numbers come
    from the app itself (Process.WorkingSet64 via the dump-state command), so
    they are the same values the README quotes.

    Prints a Markdown table, which is what the plan's phase summaries record.

.PARAMETER Files
    Documents to open, in order. The first one is opened by the primary
    launch, the rest are handed over as secondary launches.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).

.PARAMETER Label
    Row label prefix for the printed table, e.g. a version or branch name.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Files,
    [string]$Exe,
    [string]$Label = ''
)

. "$PSScriptRoot/common.ps1"

#  "pwsh -File script.ps1 -Files a b" hands the script only "a" — with -File
#  every token is a separate literal argument and the array never forms. Taking
#  a comma-separated list as well makes both call styles work:
#    pwsh -File  tools/verify/measure-memory.ps1 -Files a.md,b.md
#    pwsh -Command "& tools/verify/measure-memory.ps1 -Files a.md,b.md"
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

if ($Exe) {
    Set-MarkLiteExe $Exe
}

$rows = [System.Collections.Generic.List[object]]::new()

function Add-Row {
    param([string]$Stage)
    $state = Get-State
    $rows.Add([pscustomobject]@{
        Stage      = $Stage
        Tabs       = $state.tabs.Count
        WorkingSet = $state.workingSetMb
        Private    = $state.privateMb
        Managed    = $state.managedMb
    }) | Out-Null
    Write-Host ("  {0,-34} {1,2} tabs  {2,7:N1} MB working set" -f $Stage, $state.tabs.Count, $state.workingSetMb)
}

Write-Section "measure-memory: $($Files.Count) file(s)"
$firstRenderMs = $null

try {
    [void](Start-MarkLite -File $Files[0] -LogName 'measure-memory')
    $render = Wait-Log -Pattern 'first content render (\d+) ms'
    $firstRenderMs = [int]$render.Match.Groups[1].Value
    Write-Host "  first content render: $firstRenderMs ms"
    Add-Row "first render"

    for ($i = 1; $i -lt $Files.Count; $i++) {
        [void](Open-InMarkLite -File $Files[$i])
        Add-Row "opened $($i + 1) tabs"
    }

    for ($i = 0; $i -lt $Files.Count; $i++) {
        [void](Send-Cmd "tab $i")
        [void](Send-Cmd 'scroll-end')
        [void](Send-Cmd 'scroll 0')
    }
    Add-Row 'after scroll-through'

    for ($round = 0; $round -lt 2; $round++) {
        for ($i = 0; $i -lt $Files.Count; $i++) {
            [void](Send-Cmd "tab $i")
        }
    }
    Add-Row 'after cycling tabs twice'

    [void](Send-Cmd 'gc')
    Add-Row 'after gc'
}
finally {
    Stop-MarkLite
}

Write-Host ''
Write-Host "### Memory $Label"
Write-Host ''
Write-Host ('Files: ' + (($Files | ForEach-Object { [IO.Path]::GetFileName($_) }) -join ', ') +
    "; first content render ${firstRenderMs} ms")
Write-Host ''
Write-Host '| Stage | Tabs | Working set (MB) | Private (MB) | Managed (MB) |'
Write-Host '|---|---:|---:|---:|---:|'
foreach ($row in $rows) {
    Write-Host ("| {0} | {1} | {2:N1} | {3:N1} | {4:N1} |" -f `
        $row.Stage, $row.Tabs, $row.WorkingSet, $row.Private, $row.Managed)
}
