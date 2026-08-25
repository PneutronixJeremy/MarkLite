<#
.SYNOPSIS
    View > Show HTML comments: comments render, other raw HTML stays dropped.

.DESCRIPTION
    Uses find-in-document as the probe, because it searches the RENDERED text:
    a string that the search can find is on screen, one it cannot find is not.
    With the toggle on, the fixture's comment markers must be findable; with it
    off, they must vanish. An `img` tag must be unfindable either way - showing
    comments must not turn ordinary HTML markup into visible prose.

    Also captures both states with PrintWindow for eyeballing.

.PARAMETER Exe
    Alternative MarkLite.exe (e.g. an unzipped portable build).
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$File,
    [string]$CaptureDir
)

. "$PSScriptRoot/common.ps1"

if ($Exe) {
    Set-MarkLiteExe $Exe
}
$repo = (git rev-parse --show-toplevel)
if (-not $File) {
    $File = Join-Path $repo 'testdata/sample-html.md'
}
if (-not $CaptureDir) {
    $CaptureDir = Join-Path ([IO.Path]::GetTempPath()) 'marklite-verify/captures'
}

#  The fixture's comment markers, and one string that only exists inside the
#  raw <img> tag.
$commentTerm = 'verify-against'
$commentCount = 2
$blockCommentTerm = 'kestrel-doc-id'
$htmlOnlyTerm = 'does-not-exist.svg'

function Get-MatchCount {
    param([string]$Term)
    $ack = Send-Cmd "find $Term"
    return [int]([regex]::Match($ack.Line, '(\d+) matches').Groups[1].Value)
}

Write-Section "test-html-comments: $([IO.Path]::GetFileName($File))"

try {
    [void](Start-MarkLite -File $File -LogName 'test-html-comments')

    # ------------------------------------------------------- toggle ON
    [void](Send-Cmd 'html-comments on')
    Assert-Equal $commentCount (Get-MatchCount $commentTerm) `
        "inline comments are rendered ('$commentTerm')"
    Assert-Equal 1 (Get-MatchCount $blockCommentTerm) `
        "block comments are rendered ('$blockCommentTerm')"
    Assert-Equal 0 (Get-MatchCount $htmlOnlyTerm) `
        "raw HTML markup stays invisible ('$htmlOnlyTerm')"
    [void](Send-Cmd 'find-close')
    [void](Save-WindowCapture (Join-Path $CaptureDir 'html-comments-on.png'))

    # ------------------------------------------------------ toggle OFF
    [void](Send-Cmd 'html-comments off')
    Assert-Equal 0 (Get-MatchCount $commentTerm) `
        "inline comments disappear when the toggle is off"
    Assert-Equal 0 (Get-MatchCount $blockCommentTerm) `
        "block comments disappear when the toggle is off"
    Assert-Equal 0 (Get-MatchCount $htmlOnlyTerm) `
        "raw HTML markup is still invisible with the toggle off"
    [void](Send-Cmd 'find-close')
    [void](Save-WindowCapture (Join-Path $CaptureDir 'html-comments-off.png'))

    #  Ordinary prose is unaffected by either state.
    [void](Send-Cmd 'html-comments on')
    Assert-True ((Get-MatchCount 'scrubber') -ge 1) 'document prose still renders normally'
    [void](Send-Cmd 'find-close')

    Write-Host "  captures in $CaptureDir"

    # -------------------------------------------------- survives a restart
    #  The toggle is a user setting, so it has to outlive the process. Left OFF
    #  on purpose before the restart, then restored to the default at the end -
    #  a verification run must not change how the user's own copy behaves.
    [void](Send-Cmd 'html-comments off')
    Stop-MarkLite
    [void](Start-MarkLite -File $File -LogName 'test-html-comments-restart')
    $restored = @(Get-LogLines) -match 'html comments restored: hidden'
    Assert-True ($restored.Count -ge 1) 'the toggle state is restored on the next launch'
    Assert-Equal 0 (Get-MatchCount $commentTerm) 'comments stay hidden after the restart'
    [void](Send-Cmd 'find-close')
    [void](Send-Cmd 'html-comments on')
}
finally {
    Stop-MarkLite
}

Exit-WithSummary 'test-html-comments'
