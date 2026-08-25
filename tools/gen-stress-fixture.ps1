<#
.SYNOPSIS
    Generates testdata/stress-large.md, the large-document stress fixture.

.DESCRIPTION
    Writes a ~500 KB operations manual for an invented orbital platform. The
    content is fictional; the point is the shape, not the prose: ~300 ATX
    headings, 1500 paragraphs, 150 lists (40 of them task lists), 40 pipe
    tables, 60 fenced code blocks, 10 blockquotes, 2 mermaid fences, a math
    block, 3 footnotes and 5 thematic breaks, with in-document #anchor links
    pointing back at earlier headings.

    Deterministic: one fixed seed, no timestamps, no machine data, so two runs
    produce a byte-identical file and the fixture can be committed.

.PARAMETER OutFile
    Destination path, relative to the repository root by default.
#>
[CmdletBinding()]
param(
    [string]$OutFile = 'testdata/stress-large.md'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (git rev-parse --show-toplevel 2>$null)
if ($repoRoot) {
    Set-Location $repoRoot
}

$rng = [Random]::new(20260824)

function Pick {
    param([object[]]$Items)
    return $Items[$rng.Next($Items.Count)]
}

function Get-Slug {
    param([string]$Text)
    $s = $Text.ToLowerInvariant()
    $s = [regex]::Replace($s, '[^a-z0-9 -]', '')
    $s = $s.Trim() -replace ' +', '-'
    return $s
}

# ---------------------------------------------------------------- word pools

$systems = @(
    'atmospheric scrubber', 'reaction wheel', 'docking clamp', 'coolant loop',
    'solar wing', 'radiator panel', 'water reclaimer', 'airlock seal',
    'attitude thruster', 'battery string', 'telemetry bus', 'cargo manipulator',
    'pressure bulkhead', 'gyro package', 'antenna boom', 'particle shield',
    'hydroponics rack', 'waste processor', 'fuel bladder', 'star tracker'
)
$adjectives = @(
    'primary', 'secondary', 'redundant', 'nominal', 'degraded', 'isolated',
    'forward', 'aft', 'starboard', 'port', 'inboard', 'outboard'
)
$topics = @(
    'Power Budget', 'Thermal Balance', 'Consumables', 'Attitude Control',
    'Docking Sequence', 'Contingency Handling', 'Crew Rotation', 'Inspection',
    'Calibration', 'Spares Inventory', 'Telemetry Review', 'Depressurisation',
    'Software Load', 'Sample Handling', 'Waste Cycle', 'Comms Blackout',
    'Debris Avoidance', 'Airlock Prep', 'Water Recovery', 'Micrometeoroid Watch'
)
$aspects = @(
    'Procedure', 'Checklist', 'Limits', 'Notes', 'Overview', 'Rationale',
    'Failure Modes', 'Timing', 'Roles', 'Escalation'
)
$chapters = @(
    'Station Overview', 'Life Support', 'Power and Thermal',
    'Propulsion and Attitude', 'Docking Operations', 'Communications',
    'Crew Systems', 'Maintenance Program', 'Emergency Procedures',
    'Records and Appendices'
)
$roles = @('the flight engineer', 'the duty operator', 'the science lead',
    'ground control', 'the maintenance crew', 'the commander')
$verbs = @('verifies', 'logs', 'isolates', 'cross-checks', 'reseats',
    'purges', 'recalibrates', 'annotates', 'schedules', 'inhibits')
$nouns = @('the manifest', 'the caution list', 'the daily log',
    'the deviation report', 'the handover note', 'the trend plot')
$units = @('kPa', 'A', 'K', 'm/s', 'litres per hour', 'per cent of budget')

$sentenceTemplates = @(
    'The {ADJ} {SYS} holds {NUM} {UNIT} while {ROLE} {VERB} {NOUN}.',
    'When the {SYS} drops below {NUM} {UNIT}, {ROLE} {VERB} {NOUN} before the next pass.',
    'Kestrel Station treats the {ADJ} {SYS} as a single fault container, so {ROLE} {VERB} {NOUN} on every shift.',
    'Readings from the {ADJ} {SYS} are averaged over {NUM} minutes; anything outside that window is discarded.',
    'A {ADJ} {SYS} that has been power-cycled twice in one orbit is quarantined until {ROLE} {VERB} {NOUN}.',
    'The margin quoted here assumes the {SYS} runs at {NUM} {UNIT} with the {ADJ} loop carrying the remainder.',
    'Nothing in this section overrides the caution placard on the {ADJ} {SYS}.',
    'Between orbits {NUM} and {NUM2} the {SYS} sees the deepest thermal swing of the day.'
)

# --------------------------------------------------------- element emitters

$script:linkPool = [System.Collections.Generic.List[string]]::new()
$script:inlineMathUsed = 0
$script:footnotesUsed = 0

function New-Sentence {
    $t = Pick $sentenceTemplates
    $t = $t.Replace('{ADJ}', (Pick $adjectives))
    $t = $t.Replace('{SYS}', (Pick $systems))
    $t = $t.Replace('{ROLE}', (Pick $roles))
    $t = $t.Replace('{VERB}', (Pick $verbs))
    $t = $t.Replace('{NOUN}', (Pick $nouns))
    $t = $t.Replace('{UNIT}', (Pick $units))
    $t = $t.Replace('{NUM2}', [string]$rng.Next(40, 99))
    while ($t.Contains('{NUM}')) {
        $t = [regex]::Replace($t, '\{NUM\}', [string]$rng.Next(3, 240), 1)
    }
    return $t
}

function New-Paragraph {
    #  Mean of ~2.2 sentences keeps the fixture near 500 KB at 1500 paragraphs.
    $count = if ($rng.Next(0, 5) -eq 0) { 3 } else { 2 }
    $parts = for ($i = 0; $i -lt $count; $i++) { New-Sentence }
    $text = ($parts -join ' ')

    switch ($rng.Next(0, 5)) {
        0 { $text += ' The **' + (Pick $adjectives) + ' ' + (Pick $systems) + '** owns this limit.' }
        1 { $text += ' Treat *' + (Pick $nouns) + '* as authoritative.' }
        2 { $text += ' Command form: `' + (Pick $verbs) + ' --' + ((Pick $systems).Replace(' ', '-')) + '`.' }
        default { }
    }

    if ($rng.Next(0, 6) -eq 0 -and $script:linkPool.Count -gt 0) {
        $slug = $script:linkPool[$rng.Next($script:linkPool.Count)]
        $text += ' See [' + (Pick $topics) + '](#' + $slug + ') for the matching limits.'
    } elseif ($rng.Next(0, 12) -eq 0) {
        $text += ' Background: [the platform handbook](https://example.invalid/kestrel/handbook).'
    }

    if ($script:inlineMathUsed -lt 6 -and $rng.Next(0, 40) -eq 0) {
        $script:inlineMathUsed++
        $text += ' The burn budget follows $\Delta v = g_0 I_{sp} \ln(m_0/m_1)$ exactly.'
    }

    if ($script:footnotesUsed -lt 3 -and $rng.Next(0, 90) -eq 0) {
        $script:footnotesUsed++
        $text += ' The tolerance is inherited from the original spec[^spec-' + $script:footnotesUsed + '].'
    }

    return @($text, '')
}

function New-List {
    param([switch]$Tasks)

    $lines = [System.Collections.Generic.List[string]]::new()
    $ordered = (-not $Tasks) -and ($rng.Next(0, 2) -eq 0)
    $count = $rng.Next(3, 7)
    for ($i = 0; $i -lt $count; $i++) {
        $body = (Pick $verbs) + ' the ' + (Pick $adjectives) + ' ' + (Pick $systems) +
                ' and record ' + (Pick $nouns)
        if ($Tasks) {
            $mark = if ($rng.Next(0, 3) -eq 0) { 'x' } else { ' ' }
            $lines.Add('- [' + $mark + '] ' + $body)
        } elseif ($ordered) {
            $lines.Add([string]($i + 1) + '. ' + $body)
        } else {
            $lines.Add('- ' + $body)
        }

        if ($rng.Next(0, 5) -eq 0) {
            $subCount = $rng.Next(2, 4)
            for ($j = 0; $j -lt $subCount; $j++) {
                $sub = 'confirm ' + (Pick $systems) + ' at ' + $rng.Next(5, 200) + ' ' + (Pick $units)
                if ($Tasks) {
                    $lines.Add('  - [ ] ' + $sub)
                } elseif ($ordered) {
                    $lines.Add('   - ' + $sub)
                } else {
                    $lines.Add('  - ' + $sub)
                }
            }
        }
    }
    $lines.Add('')
    return $lines.ToArray()
}

function New-Table {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('| Subsystem | Nominal | Caution | Owner |')
    $alignment = Pick @(
        '|---|---:|---:|---|',
        '|:---|---:|:---:|:---|',
        '|---|---|---|---|'
    )
    $lines.Add($alignment)
    $rows = $rng.Next(4, 9)
    for ($i = 0; $i -lt $rows; $i++) {
        $lines.Add('| ' + (Pick $adjectives) + ' ' + (Pick $systems) +
            ' | ' + $rng.Next(10, 400) + ' ' + (Pick $units) +
            ' | ' + $rng.Next(401, 900) + ' ' + (Pick $units) +
            ' | ' + (Pick $roles) + ' |')
    }
    $lines.Add('')
    return $lines.ToArray()
}

function New-CodeBlock {
    $lang = Pick @('csharp', 'json', 'powershell', '')
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('```' + $lang)
    switch ($lang) {
        'csharp' {
            $name = (Get-Slug (Pick $systems)).Replace('-', '')
            $lines.Add('public sealed class ' + $name + 'Monitor')
            $lines.Add('{')
            $lines.Add('    private readonly double _cautionLimit = ' + $rng.Next(20, 400) + '.0;')
            $lines.Add('')
            $lines.Add('    public bool IsNominal(double reading)')
            $lines.Add('    {')
            $lines.Add('        if (reading > _cautionLimit)')
            $lines.Add('        {')
            $lines.Add('            return false;')
            $lines.Add('        }')
            $lines.Add('')
            $lines.Add('        return reading > 0.0;')
            $lines.Add('    }')
            $lines.Add('}')
        }
        'json' {
            $lines.Add('{')
            $lines.Add('  "subsystem": "' + (Get-Slug (Pick $systems)) + '",')
            $lines.Add('  "nominal": ' + $rng.Next(10, 400) + ',')
            $lines.Add('  "caution": ' + $rng.Next(401, 900) + ',')
            $lines.Add('  "owner": "' + (Pick $roles) + '",')
            $lines.Add('  "sampled": ["orbit-' + $rng.Next(1, 99) + '", "orbit-' + $rng.Next(1, 99) + '"]')
            $lines.Add('}')
        }
        'powershell' {
            $lines.Add('$reading = Get-Telemetry -Channel ' + $rng.Next(100, 999))
            $lines.Add('if ($reading.Value -gt ' + $rng.Next(20, 400) + ') {')
            $lines.Add('    Write-Warning "caution band"')
            $lines.Add('}')
            $lines.Add('$reading | Format-Table Channel, Value, Stamp')
        }
        default {
            $lines.Add('CH' + $rng.Next(100, 999) + '  ' + $rng.Next(10, 400) + ' ' + (Pick $units))
            $lines.Add('CH' + $rng.Next(100, 999) + '  ' + $rng.Next(10, 400) + ' ' + (Pick $units))
            $lines.Add('STATUS ' + (Pick @('NOMINAL', 'CAUTION', 'INHIBITED')))
        }
    }
    $lines.Add('```')
    $lines.Add('')
    return $lines.ToArray()
}

function New-Quote {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('> ' + (New-Sentence))
    $lines.Add('>')
    $lines.Add('> ' + (New-Sentence))
    $lines.Add('')
    return $lines.ToArray()
}

function New-Mermaid {
    return @(
        '```mermaid',
        'flowchart TD',
        '    A[Caution raised] --> B{Within limits?}',
        '    B -- yes --> C[Log and continue]',
        '    B -- no --> D[Isolate subsystem]',
        '    D --> E[Notify ground control]',
        '    E --> F[Schedule inspection]',
        '```',
        ''
    )
}

function New-MathBlock {
    return @(
        '$$',
        'Q_{net} = \sum_{i=1}^{n} \left( \alpha_i A_i S - \epsilon_i A_i \sigma T_i^4 \right)',
        '$$',
        ''
    )
}

function New-Rule {
    return @('---', '')
}

function New-Setext {
    $text = (Pick $topics) + ' ' + (Pick $aspects)
    $underline = if ($rng.Next(0, 2) -eq 0) { '=' } else { '-' }
    return @($text, ($underline * [Math]::Max(8, $text.Length)), '')
}

# ------------------------------------------------------- heading skeleton

$headings = [System.Collections.Generic.List[object]]::new()
$slugCounts = @{}

function Get-Capitalized {
    param([string]$Text)
    return $Text.Substring(0, 1).ToUpperInvariant() + $Text.Substring(1)
}

function Add-Heading {
    param([int]$Level, [string]$Text)

    $slug = Get-Slug $Text
    $isFirst = -not $slugCounts.ContainsKey($slug)
    if ($isFirst) {
        $slugCounts[$slug] = 0
    } else {
        $slugCounts[$slug] = $slugCounts[$slug] + 1
    }
    #  Only first occurrences become anchor-link targets: repeats get a
    #  "-1"/"-2" suffix from the renderer and this script does not try to
    #  reproduce that numbering.
    $headings.Add([pscustomobject]@{
        Level     = $Level
        Text      = $Text
        FirstSlug = if ($isFirst) { $slug } else { $null }
    }) | Out-Null
}

foreach ($chapter in $chapters) {
    Add-Heading 1 $chapter
    for ($s = 0; $s -lt 6; $s++) {
        Add-Heading 2 ((Pick $topics) + ' ' + (Pick $aspects))
        for ($t = 0; $t -lt 3; $t++) {
            Add-Heading 3 ((Get-Capitalized (Pick $adjectives)) + ' ' + (Pick $topics))
            if ($rng.Next(0, 4) -eq 0) {
                Add-Heading 4 ((Get-Capitalized (Pick $systems)) + ' ' + (Pick $aspects))
            }
        }
    }
}

# ------------------------------------------------- element quotas + layout

$queue = [System.Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt 1500; $i++) { $queue.Add('paragraph') | Out-Null }
for ($i = 0; $i -lt 110; $i++) { $queue.Add('list') | Out-Null }
for ($i = 0; $i -lt 40; $i++) { $queue.Add('tasklist') | Out-Null }
for ($i = 0; $i -lt 40; $i++) { $queue.Add('table') | Out-Null }
for ($i = 0; $i -lt 60; $i++) { $queue.Add('code') | Out-Null }
for ($i = 0; $i -lt 10; $i++) { $queue.Add('quote') | Out-Null }
for ($i = 0; $i -lt 2; $i++) { $queue.Add('mermaid') | Out-Null }
for ($i = 0; $i -lt 5; $i++) { $queue.Add('rule') | Out-Null }
for ($i = 0; $i -lt 8; $i++) { $queue.Add('setext') | Out-Null }
$queue.Add('math') | Out-Null

#  Fisher-Yates with the same seeded generator, so the mix is shuffled but
#  reproducible.
for ($i = $queue.Count - 1; $i -gt 0; $i--) {
    $j = $rng.Next($i + 1)
    $tmp = $queue[$i]
    $queue[$i] = $queue[$j]
    $queue[$j] = $tmp
}

#  Spread the queue across the headings: every heading gets at least the base
#  share, the first (count % headings) get one extra, then the per-heading
#  counts themselves are shuffled so the document does not front-load.
$sectionCount = $headings.Count
$base = [Math]::Floor($queue.Count / $sectionCount)
$extra = $queue.Count % $sectionCount
$takes = @(for ($i = 0; $i -lt $sectionCount; $i++) { if ($i -lt $extra) { $base + 1 } else { $base } })
for ($i = $takes.Count - 1; $i -gt 0; $i--) {
    $j = $rng.Next($i + 1)
    $tmp = $takes[$i]
    $takes[$i] = $takes[$j]
    $takes[$j] = $tmp
}

# ------------------------------------------------------------- emit document

$out = [System.Collections.Generic.List[string]]::new()
$out.Add('<!-- Generated by tools/gen-stress-fixture.ps1 - do not hand-edit; regenerate instead. -->') | Out-Null
$out.Add('') | Out-Null
$out.Add('# Kestrel Station Operations Manual') | Out-Null
$out.Add('') | Out-Null
$out.Add('Fictional reference material used as MarkLite''s large-document stress fixture. Kestrel Station, its subsystems, crew roles and numbers are invented; nothing here describes a real vehicle or a real procedure.') | Out-Null
$out.Add('') | Out-Null

$cursor = 0
$blockCount = 2   # the intro heading and paragraph
for ($h = 0; $h -lt $sectionCount; $h++) {
    $heading = $headings[$h]
    if ($heading.FirstSlug) {
        #  Added as the heading is emitted, so every #anchor in the body points
        #  back at a heading that already exists above it.
        $script:linkPool.Add($heading.FirstSlug) | Out-Null
    }
    $out.Add(('#' * $heading.Level) + ' ' + $heading.Text) | Out-Null
    $out.Add('') | Out-Null
    $blockCount++

    $take = $takes[$h]
    for ($k = 0; $k -lt $take -and $cursor -lt $queue.Count; $k++) {
        $kind = $queue[$cursor]
        $cursor++
        $blockCount++
        switch ($kind) {
            'paragraph' { $out.AddRange([string[]](New-Paragraph)) }
            'list'      { $out.AddRange([string[]](New-List)) }
            'tasklist'  { $out.AddRange([string[]](New-List -Tasks)) }
            'table'     { $out.AddRange([string[]](New-Table)) }
            'code'      { $out.AddRange([string[]](New-CodeBlock)) }
            'quote'     { $out.AddRange([string[]](New-Quote)) }
            'mermaid'   { $out.AddRange([string[]](New-Mermaid)) }
            'math'      { $out.AddRange([string[]](New-MathBlock)) }
            'rule'      { $out.AddRange([string[]](New-Rule)) }
            'setext'    { $out.AddRange([string[]](New-Setext)) }
        }
    }
}

#  Any footnote reference that never got emitted still needs a definition, and
#  a definition without a reference is harmless, so all three are always written.
$out.Add('## Notes') | Out-Null
$out.Add('') | Out-Null
for ($i = 1; $i -le 3; $i++) {
    $out.Add('[^spec-' + $i + ']: ' + (New-Sentence)) | Out-Null
}
$out.Add('') | Out-Null
$blockCount += 2

$text = ($out -join "`r`n") + "`r`n"
$utf8 = [Text.UTF8Encoding]::new($false)
$fullPath = [IO.Path]::GetFullPath($OutFile)
[IO.File]::WriteAllText($fullPath, $text, $utf8)

$bytes = [IO.FileInfo]::new($fullPath).Length
$atxHeadings = ($out | Where-Object { $_ -match '^#{1,4} ' }).Count
Write-Host ("{0}: {1:N0} bytes, {2:N0} top-level blocks, {3} ATX headings, {4:N0} lines" -f `
    $OutFile, $bytes, $blockCount, $atxHeadings, $out.Count)
