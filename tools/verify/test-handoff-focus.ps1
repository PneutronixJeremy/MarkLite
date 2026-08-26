<#
.SYNOPSIS
    A file handoff raises the primary window — the one check here that takes
    focus, so it never runs without -TakeFocus and is not part of run-all.

.DESCRIPTION
    Opening a second document from Explorer hands the path to the running
    MarkLite over the single-instance pipe. Windows will not let a process that
    does not own the foreground take it, so the secondary launch grants its own
    right away (AllowSetForegroundWindow) before writing to the pipe, and the
    primary claims it. This asserts the outcome the user actually cares about:
    the window comes to the front, restored if it was minimized.

    Nothing is injected: the window is minimized with ShowWindow, the handoff is
    a real secondary launch, and the result is read with GetForegroundWindow.
    But the window WILL take focus from whatever you are doing — that is the
    behaviour being checked — which is why the switch is mandatory.

.PARAMETER TakeFocus
    Required. Confirms you are willing to have the desktop's focus moved.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).
#>
[CmdletBinding()]
param(
    [switch]$TakeFocus,
    [string]$Exe
)

. "$PSScriptRoot/common.ps1"

if (-not $TakeFocus) {
    Write-Host ''
    Write-Host 'test-handoff-focus: skipped - this check moves the desktop focus.' -ForegroundColor Yellow
    Write-Host '  Re-run with -TakeFocus when you are not in the middle of something.' -ForegroundColor Yellow
    exit 0
}

if ($Exe) {
    Set-MarkLiteExe $Exe
}

if (-not ('MarkLite.Focus' -as [type])) {
    Add-Type -Namespace MarkLite -Name Focus -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
'@
}
$SW_MINIMIZE = 6

$repo = (git rev-parse --show-toplevel)
$first = Join-Path $repo 'testdata/sample.md'
$second = Join-Path $repo 'testdata/sample-plan.md'

Write-Section 'test-handoff-focus'

<#  The foreground does not change the instant a window is raised; give the
    shell a moment before reading it, rather than asserting on a race. #>
function Wait-Foreground {
    param([IntPtr]$Handle, [int]$TimeoutMs = 3000)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([MarkLite.Focus]::GetForegroundWindow() -eq $Handle) {
            return $true
        }
        Start-Sleep -Milliseconds 50
    }
    return $false
}

try {
    $app = Start-MarkLite -File $first -LogName 'test-handoff-focus'
    $window = $app.MainWindowHandle
    Assert-True ($window -ne [IntPtr]::Zero) 'the primary window has a handle'

    #  ---------------------------------------------- occluded, not minimized
    #  Minimizing and restoring is the cheapest way to hand the foreground to
    #  whatever was behind MarkLite without touching keyboard or mouse.
    [void][MarkLite.Focus]::ShowWindow($window, $SW_MINIMIZE)
    Start-Sleep -Milliseconds 300
    Assert-True ([MarkLite.Focus]::GetForegroundWindow() -ne $window) 'another window holds the foreground'

    $before = Get-LogCount
    [void](Open-InMarkLite -File $second)
    [void](Wait-Log -Pattern 'handoff raise' -Since $before)

    Assert-True (Wait-Foreground -Handle $window) 'the handoff brought MarkLite to the foreground'
    Assert-True (-not [MarkLite.Focus]::IsIconic($window)) 'the minimized window was restored, not just raised'

    #  Joined into one string: "-match" over an array answers with the matching
    #  elements, which Assert-True cannot read as a condition.
    $raise = @(Get-LogLines | Select-Object -Skip $before | Select-String 'handoff raise') -join "`n"
    Assert-True ($raise -match 'SetForegroundWindow succeeded') 'the app logs the raise as successful'
    Assert-True ($raise -match 'restored from minimized') 'the app logs the restore'

    $state = Get-State
    Assert-Equal 2 $state.tabs.Count 'the handed-over file opened as a second tab'
    Assert-Equal 1 $state.activeTab 'and it is the active tab'

    #  ------------------------------------- a debug command must NOT take focus
    #  Same pipe, different message type: verification scripts run while the
    #  user works, so only a file handoff is allowed to raise the window.
    [void][MarkLite.Focus]::ShowWindow($window, $SW_MINIMIZE)
    Start-Sleep -Milliseconds 300
    $other = [MarkLite.Focus]::GetForegroundWindow()
    Assert-True ($other -ne $window) 'MarkLite is out of the foreground again'

    [void](Send-Cmd 'tab 0')
    Start-Sleep -Milliseconds 500
    Assert-True ([MarkLite.Focus]::GetForegroundWindow() -ne $window) 'a debug command did not steal focus'
    Assert-True ([MarkLite.Focus]::IsIconic($window)) 'and did not un-minimize the window'
}
finally {
    Stop-MarkLite
    Clear-MarkLiteSession
}

Exit-WithSummary 'test-handoff-focus'
