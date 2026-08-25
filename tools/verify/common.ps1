<#
    Shared helpers for the MarkLite verification scripts.

    Everything here drives the app the way a user's keyboard and mouse would,
    but WITHOUT touching either: commands travel over the single-instance pipe
    ("MarkLite.exe --cmd <text>", answered only when MARKLITE_DEBUG=1), state
    comes back as a JSON line on stderr, and screenshots are taken with
    PrintWindow, which renders a window that is neither focused nor on top.

    Dot-source it:  . "$PSScriptRoot/common.ps1"
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = (git rev-parse --show-toplevel)
$script:Exe = Join-Path $script:RepoRoot 'publish/MarkLite.exe'
$script:App = $null
$script:LogPath = $null
$script:Failures = 0
$script:Checks = 0
$script:Skips = 0

# ------------------------------------------------------------------ native

if (-not ('MarkLite.Native' -as [type])) {
    Add-Type -Namespace MarkLite -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
}

#  Per-monitor-aware v2 (-4): without it the captures come back scaled and the
#  window rectangle is reported in virtualized coordinates.
[void][MarkLite.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))

# ------------------------------------------------------------------ output

function Write-Section {
    param([string]$Text)
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Write-Pass {
    param([string]$Text)
    $script:Checks++
    Write-Host "PASS  $Text" -ForegroundColor Green
}

function Write-Fail {
    param([string]$Text)
    $script:Checks++
    $script:Failures++
    Write-Host "FAIL  $Text" -ForegroundColor Red
}

<#  A check that cannot be run in this configuration. Counted so the totals
    still add up, but never a failure - the reason is printed so a SKIP can
    never quietly become "we tested that". #>
function Write-Skip {
    param([string]$Text)
    $script:Checks++
    $script:Skips++
    Write-Host "SKIP  $Text" -ForegroundColor Yellow
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Pass $Message
    } else {
        Write-Fail $Message
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -eq $Actual) {
        Write-Pass "$Message (= $Actual)"
    } else {
        Write-Fail "$Message (expected $Expected, got $Actual)"
    }
}

function Assert-Near {
    param([double]$Expected, [double]$Actual, [double]$Tolerance, [string]$Message)
    $delta = [Math]::Abs($Expected - $Actual)
    if ($delta -le $Tolerance) {
        Write-Pass "$Message ($Actual, within $Tolerance of $Expected)"
    } else {
        Write-Fail "$Message (expected $Expected +/- $Tolerance, got $Actual)"
    }
}

function Exit-WithSummary {
    param([string]$Name)
    Write-Host ''
    if ($script:Failures -eq 0) {
        $suffix = if ($script:Skips -gt 0) { ", $script:Skips skipped" } else { '' }
        Write-Host "${Name}: ALL PASS ($script:Checks checks$suffix)" -ForegroundColor Green
        exit 0
    }
    Write-Host "${Name}: $script:Failures of $script:Checks checks FAILED" -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------------------- launch

function Set-MarkLiteExe {
    param([string]$Path)
    $script:Exe = (Resolve-Path -LiteralPath $Path).Path
}

function Get-MarkLiteExe {
    return $script:Exe
}

<#  Handle of the running app's main window, for the few checks that have to
    act on the window itself (capture, resize). #>
function Get-MarkLiteWindow {
    if (-not $script:App -or $script:App.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'No MarkLite window.'
    }
    return $script:App.MainWindowHandle
}

function Get-VerifyLogPath {
    return $script:LogPath
}

<#  Moves the window onto the non-primary display when there is one, so a run
    does not land on top of whatever the user is working on. SWP_NOACTIVATE +
    SWP_NOZORDER: the window is repositioned without being focused or raised.
    Deliberately generic — no monitor coordinates are hard-coded. #>
function Move-ToSecondaryScreen {
    param([IntPtr]$Handle)

    Add-Type -AssemblyName System.Windows.Forms
    $screen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
    if (-not $screen) {
        return
    }
    #  Fixed window size, not the launch default: under software rendering the
    #  framebuffer is part of the working set, so a run on a large or
    #  high-DPI display would otherwise report memory that says more about the
    #  monitor than about the app.
    $width = [Math]::Min(1400, $screen.WorkingArea.Width - 80)
    $height = [Math]::Min(1000, $screen.WorkingArea.Height - 80)
    $x = $screen.WorkingArea.X + 40
    $y = $screen.WorkingArea.Y + 40
    $SWP_NOACTIVATE = 0x0010
    $SWP_NOZORDER = 0x0004
    [void][MarkLite.Native]::SetWindowPos($Handle, [IntPtr]::Zero, $x, $y, $width, $height,
        $SWP_NOACTIVATE -bor $SWP_NOZORDER)
}

<#  Starts the primary instance with debug logging on and waits until it has
    rendered content. Returns the process object; the stderr log path is
    available from Get-VerifyLogPath. #>
function Start-MarkLite {
    param(
        [string]$File,
        [int]$TimeoutSec = 60,
        [string]$LogName = 'marklite'
    )

    if (-not (Test-Path -LiteralPath $script:Exe)) {
        throw "Published exe not found at $script:Exe - run build/publish.ps1 first."
    }

    $logDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-verify'
    [void][IO.Directory]::CreateDirectory($logDir)
    $script:LogPath = Join-Path $logDir "$LogName.log"
    if (Test-Path -LiteralPath $script:LogPath) {
        Remove-Item -LiteralPath $script:LogPath -Force
    }

    $env:MARKLITE_DEBUG = '1'
    #  Own single-instance group: a MarkLite the user already has open must
    #  never receive the test documents, and must never answer debug commands.
    $env:MARKLITE_INSTANCE = 'verify'
    Remove-Item Env:MARKLITE_STANDALONE -ErrorAction SilentlyContinue

    foreach ($stray in @(Get-Process -Name 'MarkLite' -ErrorAction SilentlyContinue)) {
        if ($stray.Path -eq $script:Exe) {
            #  Left over from an interrupted run of these scripts - the user's
            #  installed copy lives elsewhere and is never touched.
            Write-Host "  stopping leftover verification instance (PID $($stray.Id))" -ForegroundColor Yellow
            $stray.Kill()
            [void]$stray.WaitForExit(5000)
        }
    }

    $arguments = @()
    if ($File) {
        $arguments += (Resolve-Path -LiteralPath $File).Path
    }
    $script:App = Start-Process -FilePath $script:Exe -ArgumentList $arguments -PassThru `
        -RedirectStandardError $script:LogPath

    if ($File) {
        [void](Wait-Log -Pattern 'first content render' -TimeoutSec $TimeoutSec)
    } else {
        [void](Wait-Log -Pattern 'welcome state' -TimeoutSec $TimeoutSec)
    }

    #  MainWindowHandle is populated a moment after the first render.
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        $script:App.Refresh()
        if ($script:App.MainWindowHandle -ne [IntPtr]::Zero) {
            break
        }
        Start-Sleep -Milliseconds 50
    }
    if ($script:App.MainWindowHandle -ne [IntPtr]::Zero) {
        Move-ToSecondaryScreen -Handle $script:App.MainWindowHandle
    }
    return $script:App
}

<#  Secondary launch: hands a file to the running primary over the pipe and
    exits, which is what a second double-click does. #>
function Open-InMarkLite {
    param([string]$File, [int]$TimeoutSec = 60)

    $full = (Resolve-Path -LiteralPath $File).Path
    $before = Get-LogCount
    $process = Start-Process -FilePath $script:Exe -ArgumentList @($full) -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Secondary launch exited with $($process.ExitCode)"
    }
    return (Wait-Log -Pattern 'first content render|toc built' -TimeoutSec $TimeoutSec -Since $before)
}

function Stop-MarkLite {
    #  Runs from a finally block, so it must survive being called after a
    #  failed launch or a process that already went away - a teardown error
    #  would otherwise mask the real failure.
    if (-not $script:App -or $script:App -isnot [System.Diagnostics.Process]) {
        $script:App = $null
        return
    }
    $WM_CLOSE = 0x0010
    try {
        if (-not $script:App.HasExited -and $script:App.MainWindowHandle -ne [IntPtr]::Zero) {
            #  Graceful close without focus or input: the window gets the same
            #  message the title-bar X sends.
            [void][MarkLite.Native]::PostMessage($script:App.MainWindowHandle, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
            [void]$script:App.WaitForExit(5000)
        }
        if (-not $script:App.HasExited) {
            $script:App.Kill()
            [void]$script:App.WaitForExit(5000)
        }
    }
    catch {
        Write-Host "  (teardown: $($_.Exception.Message))" -ForegroundColor Yellow
    }
    $script:App = $null
}

# ---------------------------------------------------------------- log + cmd

function Get-LogLines {
    if (-not $script:LogPath -or -not (Test-Path -LiteralPath $script:LogPath)) {
        return @()
    }
    return @(Get-Content -LiteralPath $script:LogPath -ErrorAction SilentlyContinue)
}

function Get-LogCount {
    return @(Get-LogLines).Count
}

<#  Waits for a log line matching $Pattern, ignoring everything before line
    index $Since. Returns an object with the line, its index, and the regex
    match, so callers can pull captured groups out. #>
function Wait-Log {
    param(
        [Parameter(Mandatory)][string]$Pattern,
        [int]$TimeoutSec = 20,
        [int]$Since = 0
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ($true) {
        $lines = @(Get-LogLines)
        for ($i = $Since; $i -lt $lines.Count; $i++) {
            $match = [regex]::Match($lines[$i], $Pattern)
            if ($match.Success) {
                return [pscustomobject]@{ Line = $lines[$i]; Index = $i; Match = $match }
            }
        }
        if ([DateTime]::UtcNow -gt $deadline) {
            throw "Timed out after ${TimeoutSec}s waiting for log line matching /$Pattern/"
        }
        Start-Sleep -Milliseconds 40
    }
}

<#  Sends one debug command and waits for the app's acknowledgement, so the
    caller never has to sleep to let an action land. #>
function Send-Cmd {
    param(
        [Parameter(Mandatory)][string]$Command,
        [int]$TimeoutSec = 30
    )

    $before = Get-LogCount
    $process = Start-Process -FilePath $script:Exe -ArgumentList (@('--cmd') + $Command.Split(' ')) `
        -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Command '$Command' could not be delivered (exit $($process.ExitCode))"
    }
    $escaped = [regex]::Escape($Command)
    return (Wait-Log -Pattern "cmd $escaped -> " -TimeoutSec $TimeoutSec -Since $before)
}

<#  dump-state as an object: tabs, toc, search, and the app's own memory
    numbers (Process.WorkingSet64 etc., measured inside the process). #>
function Get-State {
    param([int]$TimeoutSec = 30)

    $before = Get-LogCount
    [void](Send-Cmd -Command 'dump-state' -TimeoutSec $TimeoutSec)
    $line = Wait-Log -Pattern '\[marklite\] state (\{.*\})$' -TimeoutSec $TimeoutSec -Since $before
    return ($line.Match.Groups[1].Value | ConvertFrom-Json)
}

function Get-ActiveTabState {
    param($State)
    return $State.tabs | Where-Object { $_.active } | Select-Object -First 1
}

# ---------------------------------------------------------------- capturing

<#  PrintWindow with PW_RENDERFULLCONTENT (2): captures the window's own
    rendering even when it is behind other windows and unfocused. #>
function Save-WindowCapture {
    param([string]$Path)

    if (-not $script:App -or $script:App.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'No MarkLite window to capture.'
    }
    Add-Type -AssemblyName System.Drawing

    $handle = $script:App.MainWindowHandle
    $rect = New-Object MarkLite.Native+RECT
    [void][MarkLite.Native]::GetWindowRect($handle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $ok = [MarkLite.Native]::PrintWindow($handle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()

    $full = [IO.Path]::GetFullPath($Path)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
    $bitmap.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    if (-not $ok) {
        Write-Host "  (PrintWindow reported failure for $Path)" -ForegroundColor Yellow
    }
    return $full
}
