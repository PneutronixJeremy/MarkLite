<#
.SYNOPSIS
    Scans committed text files for machine- or user-identifying content.

.DESCRIPTION
    MarkLite is a public repository and every plan file under plans/ is now
    committed, so anything that names this machine, this account, or a local
    path must never reach a commit. This script scans the tracked text files
    that humans write prose in (plans/, docs/, tools/, and the top-level
    Markdown documents) and reports every hit as "file:line: match".

    build/ is deliberately NOT scanned: the publish and packaging scripts need
    functional absolute paths to the local toolchain.

.PARAMETER Staged
    Scan the staged content (what the commit would actually contain) instead of
    the working tree. Used by .githooks/pre-commit.

.OUTPUTS
    Exit code 0 when clean, 1 when anything matched.
#>
[CmdletBinding()]
param(
    [switch]$Staged
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (git rev-parse --show-toplevel 2>$null)
if (-not $repoRoot) {
    Write-Error 'scrub-check must run inside the MarkLite git repository.'
    exit 2
}
Set-Location $repoRoot

#  Roots to scan. A path qualifies when it sits under one of the directories or
#  equals one of the files. build/ is excluded by simply not listing it.
$scanDirs = @('plans/', 'docs/', 'tools/')
$scanFiles = @('README.md', 'AGENTS.md', 'CLAUDE.md', 'THIRD-PARTY-NOTICES.md')
$textExtensions = @('.md', '.ps1', '.psm1', '.txt', '.sh', '.json', '.yml', '.yaml')

function Test-Scanned {
    param([string]$Path)

    $p = $Path.Replace([char]92, [char]47)
    $ext = [IO.Path]::GetExtension($p)
    if ($textExtensions -notcontains $ext) { return $false }
    if ($scanFiles -contains $p) { return $true }
    foreach ($dir in $scanDirs) {
        if ($p.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

<#  Patterns are assembled from fragments so this script does not match itself
    when it scans tools/. Anything spelled out literally here would become a
    permanent false positive on the very file that defines it. #>
$backslash = [string][char]92 + [string][char]92   # regex source for one literal backslash
$patterns = @(
    @{ Name = 'absolute path'; Regex = '(?<![A-Za-z0-9])[A-Za-z]:' + $backslash }
    @{ Name = 'user profile path'; Regex = $backslash + 'Us' + 'ers' + $backslash }
    @{ Name = 'user profile path'; Regex = '/Us' + 'ers/' }
    @{ Name = 'user profile path'; Regex = '/ho' + 'me/[a-z]' }
    @{ Name = 'token'; Regex = 'gh' + 'p_[A-Za-z0-9]' }
    @{ Name = 'token'; Regex = 'git' + 'hub_' + 'pat_' }
    @{ Name = 'token'; Regex = 'AK' + 'IA[0-9A-Z]{4}' }
    @{ Name = 'email address'; Regex = '[A-Za-z0-9._%+-]+@(?!users\.noreply\.github\.com)[A-Za-z0-9.-]+\.[A-Za-z]{2,}' }
)

foreach ($varName in @('COMPUTERNAME', 'USERNAME', 'USERDOMAIN')) {
    $value = [Environment]::GetEnvironmentVariable($varName)
    if ($value -and $value.Length -ge 3) {
        <#  Bounded on both sides so a name that also appears inside a longer
            public identifier (the GitHub account in the clone URL, the release
            token's environment variable name) is not reported. #>
        $bounded = '(?<![A-Za-z0-9])' + [regex]::Escape($value) + '(?![A-Za-z0-9])'
        $patterns += @{ Name = "local $($varName.ToLowerInvariant())"; Regex = $bounded }
    }
}

if ($Staged) {
    $candidates = @(git diff --cached --name-only --diff-filter=ACM)
} else {
    #  Tracked plus not-yet-added files, so a new plan or script is checked
    #  before it is ever staged; ignored paths (plans/reference/) stay out.
    $candidates = @(git ls-files --cached --others --exclude-standard)
}
$candidates = @($candidates | Where-Object { Test-Scanned $_ })

$hits = 0
$skipped = @()

<#  The marker spellings are assembled at runtime for the same reason the
    patterns are: written out literally, they would match on the lines of this
    file that define and document them, quietly exempting its own source from
    its own check. Every skipped line is reported at the end, so a suppression
    can never hide anything without showing up in the output. #>
$markerBase = 'scrub-' + 'check:' + 'allow'
#  A marker counts only when it CLOSES its line (bare, or followed by a comment
#  terminator). Prose that merely mentions one - this file's own documentation,
#  the rules in AGENTS.md - continues past the token and is scanned normally.
#  Without that anchor, a sentence naming "-start" suppressed every remaining
#  line of the file it appeared in.
$markerTail = '\s*(-->|\*/|#>)?\s*$'
$markerStart = $markerBase + '-start' + $markerTail
$markerEnd = $markerBase + '-end' + $markerTail
$markerLine = $markerBase + $markerTail

foreach ($path in $candidates) {
    if ($Staged) {
        $content = git show ":$path" 2>$null
    } else {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $content = Get-Content -LiteralPath $path
    }
    if ($null -eq $content) { continue }

    <#  Suppression exists for exactly one case: documentation that has to spell
        out the patterns this script hunts for (this file's own rules, the plan
        that specifies them). Mark a single line with "scrub-check:allow", or a
        region with "scrub-check:allow-start" / "scrub-check:allow-end". Never
        use it to smuggle a real local path or credential past the hook. #>
    $suppressed = $false
    $lines = @($content)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ([string]::IsNullOrEmpty($line)) { continue }
        if ($line -match $markerStart) { $suppressed = $true; $skipped += "${path}:$($i + 1)"; continue }
        if ($line -match $markerEnd) { $suppressed = $false; $skipped += "${path}:$($i + 1)"; continue }
        if ($suppressed -or $line -match $markerLine) {
            $skipped += "${path}:$($i + 1)"
            continue
        }
        foreach ($pattern in $patterns) {
            $match = [regex]::Match($line, $pattern.Regex, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($match.Success) {
                Write-Host ("{0}:{1}: {2} -> {3}" -f $path, ($i + 1), $pattern.Name, $match.Value)
                $hits++
            }
        }
    }
}

if ($skipped.Count -gt 0) {
    Write-Host ("scrub-check: {0} line(s) skipped by a suppression marker: {1}" -f `
        $skipped.Count, ($skipped -join ', '))
}

if ($hits -gt 0) {
    Write-Host ''
    Write-Host ("scrub-check: {0} hit(s). Sensitive or machine-identifying content must not be committed." -f $hits)
    exit 1
}

Write-Host ("scrub-check: clean ({0} file(s) scanned)." -f $candidates.Count)
exit 0
