# MarkLite — Native Windows Markdown Viewer

Build a genuinely native Windows Markdown viewer (no web engine) in this
repository. Avalonia 11, C#, .NET 10, NativeAOT + trimming,
Skia rendering, single self-contained .exe. Goal: render Markdown *correctly*
(which Notepad does not) in under 100 MB working set, with zero
`msedgewebview2.exe` children, cold start under 500 ms.

**Product intent (user, 2026-08-23): lightweight PLAN VIEWER.** Read-only —
editing stays in Markdown Monster. Plan-style documents (task-list checkboxes,
tables, nested lists) are the primary content; never add editing features.

## For Future Agents
Execute **one phase per turn — never more**. As work proceeds: mark checkboxes
`- [x]` as items complete; when a phase is done, set its status to `Complete` and
write its **Phase Summary** (what was done, key decisions, anything needed to
continue with zero context); run the phase's **Verification Plan** and record the
result. Then **stop**: do not start the next phase, and do not run `git commit`.
Instead, suggest a commit message for the completed phase and wait for the user to
either approve the commit or commit it themselves. Only continue to the next phase
after the user says to. Exception: if the user explicitly grants permission to run
multiple phases and/or commit per phase, follow that grant exactly as scoped — but
never assume it. When all phases are done, fill in **Final Recap** and
**Deployment Plan**. Never reference this plan in code: the plan file is deleted
when the work is done, so comments like "Phase 2 of the plan" or "see
plans/foo.md" become dead references — code comments must stand on their own.

### Project constraints (binding)
- **No web engine, ever.** No WebView2, no Chromium, no Node. Verify with the
  attribution script below.
- **NativeAOT + PublishTrimmed is the target.** If a dependency breaks AOT, first
  try trimming/AOT feature switches and TrimmerRootDescriptor; only if that
  fails, fall back to trimmed self-contained JIT and *state the fallback
  explicitly* in the Phase Summary — never silently drop the requirement.
- Markdown pipeline: prefer built-in `Avalonia.Controls.Markdown` (Markdig-based);
  fall back to `Markdown.Avalonia` (whistyun) if it falls short. Record which won
  and why in the Phase 0 summary.
- ~~**Skip Mermaid rendering.**~~ Lifted by the Phase 10 ADOPT verdict: Mermaider
  (pure .NET, AOT-proven in the spike) renders mermaid natively on the MarkView
  stack. The underlying rule stands: no Node, no JS runtime, ever.
- Commit messages follow `Area > Subarea > Description. [w/ Claude]` format
  (see user's org instructions). Suggest, never run `git commit` / `git push`.
- Every phase's verification re-runs the AOT publish. AOT regressions must be
  caught in the phase that introduces them, not at the end.

### House style (enforce in all C#)
- Always brace control-flow bodies, even single-statement. No `if (x) return;`.
- Comments of ~4+ lines use `/*  ... */` block form (open `/*` + two spaces,
  later lines align with first word, close `*/` on trailing content line).
  Short notes stay `//`.
- Write "namespace" in full, never abbreviated.

### Environment facts (verified 2026-08-22)
- .NET SDKs installed: 6.0.428, **10.0.400**. Target `net10.0`.
- Repo directory exists, empty, not a git repo (Phase 0 inits it).
- The user already has a personal `.md` default handler (a per-user viewer
  shim). **Do NOT clobber it.** Association phase registers "Open with" only;
  making MarkLite the default requires a fresh explicit yes from the user.
- Machine: Windows 11 Pro, 32 GB RAM (tight — RAM numbers matter).

### WebView2 attribution check (used in verifications)
```powershell
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | ForEach-Object {
  $h = if ($_.CommandLine -match '--webview-exe-name=([^\s"]+)') { $matches[1] } else { '(none)' }
  [PSCustomObject]@{ Host=$h; MB=[math]::Round((Get-Process -Id $_.ProcessId).WorkingSet64/1MB) }
} | Group-Object Host | Select-Object Name, Count, @{n='MB';e={($_.Group|Measure-Object MB -Sum).Sum}}
```
Expected for MarkLite: no group named MarkLite (or marklite.exe) — ever.

## Phase 0: De-risk — prove Markdown rendering survives NativeAOT + trim
Status: Complete

Biggest risk first: Markdig and document controls lean on reflection; the whole
stack choice hinges on this spike.

- [x] `git init` at the repo root; add .NET `.gitignore` (bin/, obj/, publish artifacts). _(done 2026-08-22, branch `main`)_
- [x] Install/verify Avalonia templates: `dotnet new install Avalonia.Templates` (or confirm already installed via `dotnet new list avalonia`). _(installed Avalonia.Templates 12.1.1)_
- [x] Create spike project `spike/AotSpike` (kept in repo until MVP done, then deleted): Avalonia 11 app, `net10.0`, single window. _(template emitted Avalonia 12 csproj; pinned back to Avalonia 11.3.9)_
- [x] Determine whether `Avalonia.Controls.Markdown` exists on NuGet for Avalonia 11 (`dotnet package search`); add it, else add `Markdown.Avalonia`. Record choice. _(exists but is a paid Accelerate component — rejected; using `Markdown.Avalonia.Tight` 11.0.3, see summary)_
- [x] Render a hardcoded Markdown string containing: heading, paragraph, fenced C# block, GFM table, nested list.
- [x] Publish: `dotnet publish -c Release -r win-x64 -p:PublishAot=true -p:PublishTrimmed=true --self-contained`. Fix trim/AOT warnings via feature switches / TrimmerRootDescriptor as needed. _(AOT props live in csproj; publish needs env-var toolchain setup, see summary)_
- [x] Run the *published* exe (not `dotnet run`); confirm window shows rendered Markdown — code fence styled (no literal fence markers), table drawn as table. Screenshot for the record. _(`spike/aot-spike-render.png`)_
- [x] Record: publish success, binary size (MB), working set (MB), any trim warnings suppressed and why.
- [x] **Decision gate:** AOT works → proceed as planned. _(AOT works — no fallback needed)_

### Verification Plan
- `dotnet publish` exits 0 (AOT path, or documented JIT fallback path).
- Published exe launches, stays alive 5 s, no crash: `Start-Process` + `Get-Process` check.
- Working set of spike printed: `(Get-Process AotSpike).WorkingSet64/1MB`.
- WebView2 attribution script shows nothing for the spike.
- Visual check via screenshot: fenced block rendered styled, table rendered as grid.

**Verification results (2026-08-22):**
- Publish exits 0 on the NativeAOT path (no JIT fallback). Remaining warning: one
  IL2104 from `Markdown.Avalonia` (assembly-level trim notice) — not suppressed,
  runtime behavior verified correct instead.
- Published exe launched via `Start-Process`, alive after 5 s.
- Working set: **91 MB**. Binary: **21.4 MB** exe + native Skia/HarfBuzz/ANGLE dlls
  (libSkiaSharp 9 MB, av_libglesv2 5.2 MB, libHarfBuzzSharp 1.7 MB).
- WebView2 attribution: no group for AotSpike (only MarkdownMonster/olk/SearchHost
  from unrelated apps).
- Screenshot `spike/aot-spike-render.png`: fenced C# block in styled panel with
  syntax coloring, no literal fence markers, GFM table drawn as grid with
  right-aligned numeric column, 3-level nested list, inline-code chip. Pass.

### Phase Summary
**AOT is viable — proceed as planned.** Stack: Avalonia **11.3.9** + `Markdown.Avalonia.Tight` **11.0.3** (whistyun), `net10.0`, NativeAOT + trimmed, published exe renders all five risk constructs correctly at 91 MB working set.

Key decisions:
- **Markdown control: `Markdown.Avalonia.Tight`, not `Avalonia.Controls.Markdown`.**
  The built-in package exists for Avalonia 11 (v11.3.5, requires core ≥ 11.3.9) but
  depends on `AvaloniaUI.Licensing` — it is a commercial Avalonia Accelerate
  component requiring a paid license key. Rejected. Fallback per plan: whistyun.
- **Full `Markdown.Avalonia` crashes under AOT** — it drags in
  `Markdown.Avalonia.SyntaxHigh` → AvaloniaEdit, whose theme XAML is not
  precompiled; runtime XAML loading dies under AOT (`XamlLoadException: No
  precompiled XAML found for avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml`,
  exit 0xc0000409). `Markdown.Avalonia.Tight` (core renderer, same assembly name
  `Markdown.Avalonia.dll`, no AvaloniaEdit/Html/Svg) publishes and runs clean. It
  even does basic C# colorization on its own. Phase 2 must evaluate highlighting
  libs against this same AOT constraint (AvaloniaEdit-based options are suspect).
- **`x:Name` on the markdown control in XAML fails to compile with Tight**
  (`CS0103: Viewer does not exist` — Avalonia name generator does not emit the
  field). Workaround: instantiate `MarkdownScrollViewer` in code-behind and assign
  `Window.Content`. Carry this pattern into MarkLite.
- **The machine cannot run a stock NativeAOT publish** (no Windows SDK installed;
  VS 18 Pro has MSVC 14.51 toolset but no vcvarsall.bat and no VC.Tools component
  registration, so ilcompiler's linker autodetect fails). Solved WITHOUT admin
  rights: SDK import libs fetched from official NuGet `Microsoft.Windows.SDK.CPP.x64`
  10.0.28000.2526 and staged to `D:\packages\WinSDK-CPP\{um,ucrt}\x64`. Publish
  requires this environment (Phase 1 must bake it into `build/publish.ps1`):
  ```powershell
  $env:PATH = "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64;$env:PATH"
  $env:LIB  = "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Tools\MSVC\14.51.36231\lib\onecore\x64;D:\packages\WinSDK-CPP\um\x64;D:\packages\WinSDK-CPP\ucrt\x64"
  dotnet publish -c Release -r win-x64 --self-contained -p:IlcUseEnvironmentalTools=true
  ```
  (`IlcUseEnvironmentalTools=true` makes ilcompiler use link.exe from PATH + LIB
  env var instead of vswhere. MSVC CRT libs come from the `lib\onecore\x64`
  variant — the desktop `lib\x64` set is absent; onecore linked fine.)
- csproj AOT settings proven: `PublishAot`, `PublishTrimmed`, `SelfContained`,
  `RuntimeIdentifier win-x64`, `InvariantGlobalization`, `TrimMode=link`,
  `BuiltInComInteropSupport=true`, `AvaloniaUseCompiledBindingsByDefault=true`.
- Template packages `Avalonia.Fonts.Inter` / `AvaloniaUI.DiagnosticsSupport` and
  `.WithInterFont()` / `.WithDeveloperTools()` are Avalonia-12-era — removed for 11.
- Known cosmetic restore warning: NU1903 (Tmds.DBus.Protocol 0.21.2 vulnerability
  advisory) — transitive, Linux-only DBus client, not shipped code paths on Windows.

## Phase 1: Project skeleton + test corpus
Status: Complete

- [x] Create `MarkLite.sln` + `src/MarkLite/MarkLite.csproj` (Avalonia 11, `net10.0`, root namespace `MarkLite`), carrying over the exact AOT/trim csproj settings proven in Phase 0. _(SDK 10 emits `MarkLite.slnx` — new XML solution format, works with `dotnet build`)_
- [x] App shell: main window titled "MarkLite", menu bar (File > Open, File > Exit stub), content area hosting the Markdown control chosen in Phase 0.
- [x] Create `testdata/sample.md` containing: h1–h3 headings, prose paragraphs, nested (3-level) bullet + ordered lists, GFM table, fenced C# block, inline code, links, blockquote, and a mermaid fence (expects plain code-block fallback).
- [x] Second fixture `testdata/sample-plan.md` already exists (created 2026-08-22, plan-style document): GFM task-list checkboxes (checked + unchecked), right-aligned table columns, nested ordered lists, fenced C# + PowerShell, code block inside a list item. Keep it in the corpus; do not regenerate. _(kept untouched)_
- [x] Hardwire startup to load `testdata/sample.md` when no CLI arg given (temporary, replaced in Phase 3).
- [x] `MARKLITE_DEBUG=1` env var enables stderr diagnostics: startup timing, file load events (foundation for later autonomous verification).
- [x] Publish script `build/publish.ps1` wrapping the proven publish command; outputs to `publish/`.

### Verification Plan
- `dotnet build -c Release` exits 0 with zero warnings.
- `build/publish.ps1` exits 0; published exe launches and renders `sample.md`.
- `MARKLITE_DEBUG=1` run prints startup timing line to stderr.
- WebView2 attribution script: nothing for MarkLite.

**Verification results (2026-08-22):**
- `dotnet build MarkLite.slnx -c Release`: exit 0, **0 warnings** (fixed AVLN3001
  by adding a parameterless `MainWindow` ctor; NU1903 suppressed in csproj with
  justification comment).
- `build/publish.ps1`: exit 0 (known IL2104 from Markdown.Avalonia only).
- Published exe with `MARKLITE_DEBUG=1`, stderr captured:
  `[marklite] loaded …\testdata\sample.md (1623 chars)` and
  `[marklite] startup: window opened 186 ms after process start`. Alive after 5 s,
  working set **89 MB**.
- WebView2 attribution: no MarkLite group.
- Screenshot check: menu bar + rendered sample.md (headings, prose, styled links,
  inline-code chip, nested lists).

### Phase Summary
Skeleton up and AOT-publishing: `MarkLite.slnx` + `src/MarkLite/` (Avalonia 11.3.9,
`net10.0`, root namespace `MarkLite`, AOT/trim settings carried from Phase 0),
`build/publish.ps1` (bakes in the VS-less linker env from Phase 0, outputs to
`publish/`), `testdata/sample.md` fixture. Window: menu bar (File > Open logs a
stub line, File > Exit closes), `MarkdownScrollViewer` created in code-behind
(x:Name codegen limitation from Phase 0) hosted in a named `ContentControl`.
No CLI arg → loads `testdata\sample.md` relative to working directory (temporary
until Phase 3). `DebugLog` writes `[marklite] …` lines to stderr when
`MARKLITE_DEBUG=1` — startup timing logged on window `Opened`, file loads and
failures logged in `LoadFile`. Gotcha fixed: the startup `Stopwatch` must be
started in `Main`, not via field initializer — beforefieldinit made it start
lazily and report 0 ms. Numbers: 89 MB working set, 186 ms window-open, exe
21.5 MB — all healthy vs targets.

## Phase 2: Rendering correctness — beat Notepad
Status: Complete

The core value: render what Notepad renders badly.

**Style reference (user, 2026-08-23): Markdown Monster's dark rendering** —
screenshots saved at `plans/reference/markdown-monster-style-{1,2}.png`. Match
its overall look: near-black page background; bold white headings with a thin
horizontal rule under h1/h2; comfortable line height and paragraph spacing;
inline code as subtle gray monospace chips; code blocks as darker rounded panels
with syntax colors and a language label in the corner; light-blue underlined
links; round list markers. **Deviation requested: GFM task-list checkboxes must
be much more prominent than Markdown Monster's** — checked vs unchecked state
obvious at a glance (e.g. filled accent-colored box with a clear check mark vs
empty outlined box), not a small dim glyph.

- [x] Proportional body font (Segoe UI Variable or Segoe UI), real paragraph spacing, comfortable line height and max content width; headings scaled + weighted. _(MaxWidth 1100, body 14.5px)_
- [x] Fenced code blocks: monospace (Cascadia Mono, fallback Consolas), background panel, padding, rounded corners, horizontal scroll for long lines. No literal fence markers visible. _(plus fence-language label in the panel corner, MM-style)_
- [x] Syntax highlighting for fenced blocks: evaluate `TextMateSharp` vs `ColorCode` for AOT-safety and weight; wire the winner for at least C#, JS/TS, PowerShell, JSON, XML. Unknown languages render as plain styled code block. _(ColorCode won — see summary; JSON maps to the JavaScript grammar, ColorCode has none)_
- [x] Mermaid fences render as plain styled code block — no crash, no special handling.
- [x] GFM tables: pipe-table extension enabled, cell padding, header emphasis, row separators.
- [x] Inline code styled (monospace, tinted background chip).
- [x] Blockquotes: left rule + indent + muted tone.
- [x] Links clickable: absolute http/https/mailto → default browser (`Process.Start` with `UseShellExecute`). Relative paths and `#anchors` are logged no-ops for now (no crash, no shell-executing arbitrary strings) — in-app handling comes in Phases 3/6 (user request 2026-08-23).
- [x] Nested lists indent correctly with proper markers per level.
- [x] GFM task lists (`- [x]` / `- [ ]`) render as read-only checkboxes, not literal brackets — and prominent per the style reference: checked/unchecked distinguishable at a glance (filled accent box + check vs empty outline).

### Verification Plan
- Publish (AOT) exits 0 — highlighting library must not break trim/AOT.
- Run published exe on `testdata/sample.md`; screenshot full window.
- Screenshot inspection checklist: C# fence colorized, no literal fence markers anywhere, table drawn as grid with borders, blockquote ruled, mermaid block shown as plain code, body font proportional.
- Run published exe on `testdata/sample-plan.md`; screenshot: task-list items show as checkboxes (checked and unchecked), right-aligned numeric table columns align right, code block nested in list item renders indented within the item.
- Working set with sample.md open: record MB (target trajectory < 100 MB).

**Verification results (2026-08-23):**
- AOT publish exit 0 with ColorCode.Core included — only the known IL2104.
- `sample.md` screenshots: C# fence colorized (keywords/types/strings distinct),
  `csharp` label in panel corner, no literal fence markers, table drawn as grid
  with header band + row separators + right-aligned numeric column, blockquote
  left-ruled + muted, mermaid fence rendered as plain labeled code panel,
  proportional body font, styled underlined links, inline-code chips.
- `sample-plan.md` screenshots: checked items = filled accent-blue box with white
  check, unchecked = clear outlined box — distinguishable at a glance; list
  bullets hidden on task items; right-aligned table columns correct; PowerShell
  fence highlighted; code block nested in a list item renders indented within
  the item; nested ordered lists numbered per level.
- Working set: sample.md **89 MB**, sample-plan.md **92 MB**. Exe 21.7 MB.

### Phase Summary
MM-style dark rendering delivered on the stack from Phases 0–1, all styling in
`src/MarkLite/Rendering/`:
- **`MarkdownTheme.axaml`** (+`.cs`, a `Styles` subclass) fully REPLACES the
  engine's builtin style via `MarkdownScrollViewer.MarkdownStyle` — necessary
  because the builtin attaches to the control itself and would beat app-level
  styles. It must therefore cover every engine class (headings, code, tables,
  lists, blockquote, links, rules). Colors come from `DynamicResource` tokens
  defined in `App.axaml` ThemeDictionaries (`Default`=light + `Dark` palettes
  both already populated — Phase 4 mostly needs verification, not new work).
- **Syntax highlighting: ColorCode.Core 2.0.15** (pure managed regex, AOT-clean).
  TextMateSharp rejected: native Oniguruma dependency + heavyweight grammar
  assets. `CodeHighlighter` maps fence tags → ColorCode languages (JSON→JS
  grammar) and walks ColorCode's scope tree into colored `Run`s;
  `StyleDictionary.DefaultDark/DefaultLight` picked by `ActualThemeVariant`
  at render time (a live theme switch needs a re-render — Phase 4 note).
- **`MarkLitePlugin` (IMdAvPlugin)** registers: `CodeBlockOverride`
  (`BlockOverride2("CodeBlocksWithLangEvaluator")`) rendering Border>DockPanel
  [lang label + ScrollViewer>SelectableTextBlock] — the builtin renderer drops
  the fence language entirely; and an inline parser for task checkboxes.
- **Task lists**: the engine has NO GFM task-list support. Pipeline:
  `TaskListPreprocessor` rewrites `- [x]`/`- [ ]` at list-item starts into
  U+E000/U+E001 sentinels (private-use chars; prose immune) → inline parser in
  the plugin emits `CInlineUIContainer` with a custom Border-based box (filled
  accent + white ✓ / outlined empty — deliberately NOT a themed CheckBox, so
  the look is deterministic) → `TaskListMarkerHider` walks the rendered visual
  tree post-layout (Dispatcher.Post at Loaded priority) and hides the list
  bullet in task-item rows, since the engine renders task items as ordinary
  bullets.
- **Links**: `MarkLiteHyperlinkCommand` set via `MdAvPlugins.HyperlinkCommand` —
  absolute http/https/mailto shell-open in the browser; anything else logs
  `link ignored` (relative/.md + #anchor handling scheduled Phases 3/6).
- Text selection enabled (`SelectionEnabled=true`) — read-only viewer, copy works.
- Deviation from the MM reference, recorded: no underline rule below h1/h2 —
  headings are plain `CTextBlock`s; injecting a Border would break the
  `HeaderElement` machinery Phase 6's TOC needs. Hierarchy is carried by
  size/weight instead.
- Gotcha for future phases: `Add-Type` state does not persist between
  PowerShell tool calls; window captures live in the session scratchpad
  `capture.ps1` (PrintWindow + optional wheel-scroll via mouse_event).

## Phase 3: File opening + live reload
Status: Complete

- [x] CLI arg: `MarkLite.exe path\to\file.md` opens that file; bad/missing path shows in-window error message (no dialog spam, no crash).
- [x] File > Open via `IStorageProvider`, filtered to .md/.markdown/.txt, remembers last directory for the session.
- [x] Drag-drop a file onto the window opens it. _(new Avalonia 11.3 `DataTransfer` API — old `e.Data`/`DataFormats.Files` are CS0618)_
- [x] `FileSystemWatcher` on the open file: reload on change with ~150 ms debounce (editors fire multiple events per save); handle atomic-save rename patterns (watch directory, match filename).
- [x] Scroll position preserved across reload (capture ScrollViewer offset before re-render, restore after layout). _(control's own `SaveScrollValueWhenContentUpdated=true` does the work; verified via logged offsets)_
- [x] File deleted/locked mid-session: keep last rendered content, show non-blocking stale indicator; recover when file reappears. _(amber banner under menu bar; themed for both variants)_
- [x] Relative links to `.md`/`.markdown` files (user request 2026-08-23): resolve against the open document's directory and open in MarkLite (replacing current document until tabs exist). Other relative targets: shell-open only if the resolved file exists; else stay a logged no-op.
- [x] `MARKLITE_DEBUG=1` logs: file opened, reload triggered, scroll offset saved/restored values.

### Verification Plan
- Publish (AOT) exits 0.
- Launch published exe with `testdata/sample.md` as CLI arg + `MARKLITE_DEBUG=1`; confirm "opened" log line.
- Autonomous reload test: script appends a line to a temp copy of sample.md, wait 1 s, assert "reload" log line appears and logged restored scroll offset equals saved offset.
- Delete the temp file while open: process stays alive, no crash.

**Verification results (2026-08-23, scripted `test-reload.ps1`):**
- Publish (AOT) exit 0, only known IL2104. Build: 0 warnings.
- Launch with temp copy as CLI arg + `MARKLITE_DEBUG=1`: `opened … (1623 chars)` logged.
- Scrolled to offset 400, appended a line: `reload triggered … scroll saved 400.0`
  then `scroll restored 400.0` — saved == restored, nonzero.
- Deleted file while open: process alive, `file missing` logged (stale banner path).
- Recreated file: auto-reload fired, process alive, scroll again preserved.
- Not autonomously testable, verified by code review + debug logs wired: File>Open
  dialog, drag-drop, relative-link clicks (all log their actions).

### Phase Summary
File handling complete; single-document viewer now behaves like a real one:
- `DocumentWatcher` (src/MarkLite/DocumentWatcher.cs): watches the containing
  DIRECTORY filtered to the file name (atomic-save rename patterns arrive as
  Created/Renamed), debounces every event through a 150 ms `DispatcherTimer`,
  fires a single `ChangeSettled` on the UI thread. Consumer (`MainWindow`)
  decides: file exists → reload; missing → stale banner (kept content); locked
  (`IOException`) → stale banner, next event retries; reappears → reload clears
  banner.
- Scroll preservation: the control's builtin `SaveScrollValueWhenContentUpdated`
  =true handles capture/restore; MarkLite logs `ScrollValue.Y` before/after for
  verification.
- Stale indicator: amber banner (`StaleBanner` in MainWindow.axaml) docked under
  the menu; `MdStaleBanner*` tokens in both theme dictionaries.
- File > Open: `StorageProvider.OpenFilePickerAsync`, .md/.markdown/.txt filter +
  All, `SuggestedStartLocation` remembered per session via the picked file's
  parent folder.
- Drag-drop: `DragDrop.AllowDrop` on the Window; Avalonia 11.3's NEW
  `e.DataTransfer` + `DataFormat.File` + `TryGetFiles()` (the old
  `e.Data`/`DataFormats.Files` API is obsolete → CS0618 breaks the
  zero-warnings gate).
- `MarkLiteHyperlinkCommand` now routes: http/https/mailto → browser;
  `#anchor` → logged no-op (Phase 6); rooted or relative paths resolved against
  the open document's directory (URL-unescape + slash normalization) —
  .md/.markdown/.txt open in MarkLite via `LoadFile` callback, other EXISTING
  files shell-open, non-existent targets are logged no-ops.
- `LoadFile` is the single entry point (CLI arg, File>Open, drag-drop, links,
  reload) — preprocesses task lists, re-applies the marker-hider post-layout
  (`PostRenderPass`), rewires the watcher, resets stale state, retitles window.

## Phase 4: Theme — follow system dark/light
Status: Complete

- [x] App follows Windows app theme via Avalonia `ThemeVariant` (leave `RequestedThemeVariant` unset / Default so system setting flows through); verify live switch without restart.
- [x] Markdown styles themed for both variants: body/background, code block panel, inline code chip, blockquote, table borders, link color — all legible in dark AND light. _(palettes existed since Phase 2; verified now)_
- [x] Syntax highlighting theme pair (dark + light) switches with the variant. _(re-render on `ActualThemeVariantChanged` — highlight Runs are baked at render time)_
- [x] No hardcoded colors in axaml/code that break either variant (audit pass). _(grep audit: only tokens in App.axaml + deliberate white check glyph on accent box)_

### Verification Plan
- Publish (AOT) exits 0.
- Flip `HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` `AppsUseLightTheme` 0↔1 while exe running; screenshot both states; confirm colors switch live and both are legible.
- Restore user's original theme value afterwards.

**Verification results (2026-08-23, scripted `test-theme.ps1`):**
- Registry flip `AppsUseLightTheme` 0→1→0 with `WM_SETTINGCHANGE`
  "ImmersiveColorSet" broadcast (flip alone does not notify running apps).
- Debug log: `theme changed to Light; re-rendering` then `theme changed to
  Dark; re-rendering` — live switch without restart, process alive throughout,
  scroll position survived both re-renders.
- Screenshots: dark and light both legible — page/body, table grid, checked +
  unchecked task boxes, chips, code panels with variant-appropriate highlight
  colors. Original theme value (0) restored after test.
- Publish (AOT) exit 0; build 0 warnings.

### Phase Summary
Theme following was mostly pre-paid in Phase 2 (all colors are DynamicResource
tokens with `Default`(light) + `Dark` dictionaries; `RequestedThemeVariant` was
already Default). This phase added the missing piece and verified:
- `MainWindow.ActualThemeVariantChanged` → re-render current document, because
  syntax-highlight colors are baked into `Run.Foreground` at render time
  (styles themselves flip automatically via DynamicResource). Raw markdown text
  cached in `_currentText`; render funneled through single `RenderMarkdown`
  helper (preprocess → assign → post-layout marker hide + callback).
- Legibility fix found in light-variant screenshot: ColorCode's
  `StyleDictionary.DefaultLight` keeps PowerShell command names YELLOW —
  unreadable on the light panel. Overridden to `#795E26` in `CodeHighlighter`
  for the light dictionary only.
- Verification machinery: theme flip must broadcast `WM_SETTINGCHANGE`
  (`SendMessageTimeout` to HWND_BROADCAST) or running apps never notice the
  registry change; script restores the user's original value in `finally`.

## Phase 5: Measure against success criteria + MVP wrap
Status: Complete

Measured, not claimed. Record every number in the Phase Summary.

- [x] Working set with a real-world README open (use a large real README, e.g. dotnet/runtime's): assert **< 100 MB**. Record exact MB (context: glow 20–40, Notepad 160, Markpad 375, Markdown Monster 402). _(dotnet/runtime README is only 4.7 KB — used sindresorhus/awesome, 77 KB, thousands of links, as the hard case)_
- [x] WebView2 attribution script: assert **zero** processes attributed to MarkLite.
- [x] Cold start **< 500 ms**: measure via `MARKLITE_DEBUG=1` first-frame timing over 5 runs post-warm; record median. Also record true cold (first-run) time.
- [x] Record published binary size on disk.
- [x] Renders-what-Notepad-cannot check: side-by-side screenshots of `testdata/sample.md` in Notepad vs MarkLite (fenced C# block + GFM table). _(`plans/reference/compare-notepad-raw.png` vs `compare-marklite-rendered.png`)_
- [x] Delete `spike/AotSpike` (Phase 0 spike no longer needed).
- [x] Write README.md: what it is, the RAM comparison table, build instructions, usage.
- [x] If any criterion missed: do not rationalize — profile, fix or document the gap honestly, re-measure. _(initial RAM miss: 155 MB on the hard case — fixed, see summary)_

### Verification Plan
- All four success-criteria numbers captured by scripted measurement (commands + raw output pasted into Phase Summary).
- `git status` clean except intended files; spike directory gone.

**Verification results (2026-08-23, scripted `measure.ps1` — final build):**
```
Working set, sindresorhus/awesome README (77 KB, worst case):  97 MB  ✓ < 100
Working set, typical docs: sample.md 54 MB · powertoys README 57 MB ·
  this plan file (34 KB) 74 MB                                        ✓
Cold start, first content render, 5 warm runs: 48/50/48/48/49 → median 48 ms ✓ < 500
WebView2 processes attributed to MarkLite: 0                          ✓
Binary on disk: MarkLite.exe 21.8 MB + libSkiaSharp 9.0 + libHarfBuzzSharp 1.7
  = 32.5 MB total (av_libglesv2 no longer shipped; pdb not shipped)
```
True reboot-cold start not measurable in-session; first run after a fresh
publish also measured ~48 ms (disk cache warm). All numbers beat targets.

### Phase Summary
**All success criteria met — after an honest miss and a real fix.**
- First measurement FAILED the RAM target: 155 MB on the hard-case README
  (77 KB, thousands of hyperlinks), 106 MB even on this plan file. Fixes, in
  order of impact:
  1. **Software rendering** (`Win32RenderingMode.Software` in Program.cs) —
     a text viewer gains nothing from ANGLE/GL; dropped every measurement by
     ~30–40 MB AND cut first-render from ~175 ms to ~48 ms (no GL context
     init). Skia CPU raster scrolls text effortlessly. Bonus: av_libglesv2.dll
     (5.2 MB) no longer loads — publish.ps1 deletes it from output.
  2. **GC tuning**: `GCConserveMemory=9` + `ConcurrentGarbageCollection=false`
     in csproj, plus one aggressive compacting `GC.Collect` posted at
     Background priority after each render (parse + control-tree construction
     is a one-shot garbage spike and the app idles right after).
  Result: 54 MB typical, 97 MB pathological worst case.
- Measurement infra: `measure.ps1` (WS + 5-run cold start + WebView2 + sizes),
  fixture READMEs in session scratchpad. First-content-render timing added to
  MARKLITE_DEBUG (logged once, at the post-layout pass).
- Notepad comparison saved: `plans/reference/compare-notepad-raw.png` (literal
  `###`/`-` text) vs `compare-marklite-rendered.png` (styled + highlighted).
  Notepad's working set with the same file: 127 MB — more than MarkLite's
  pathological worst case, for rendering nothing.
- Spike deleted (`git rm spike/`), README.md written (comparison table, build
  via publish.ps1, VS-less toolchain note, usage, deployment = exe + 2 dlls).
- MVP is done: Phases 6–9 are quality-of-life on top.

## Phase 6: TOC sidebar
Status: Complete

- [x] Walk the Markdig AST (HeadingBlock nodes) to build a heading tree; refresh on reload. _(engine is whistyun, NOT Markdig — no public AST. `HeadingParser` parses raw markdown: ATX headings, fence-aware, inline markup stripped, GitHub-style slugs. Setext headings unsupported — rare, recorded.)_
- [x] Collapsible sidebar (toggle via View menu + Ctrl+T), tree indented by heading level.
- [x] Click heading → scroll document to that heading.
- [x] Current-section tracking: highlight the TOC entry for the heading nearest the viewport top on scroll. _(triggered by the engine's `HeaderScrolled` event; position math done against our own heading controls — the event's `Header` equality is Level+Text, ambiguous for repeated headings like "Verification Plan")_
- [x] Sidebar state (open/closed) persists across reload; hidden entirely when the document has no headings.
- [x] In-document `#anchor` links (user request 2026-08-23): clicking a `[text](#heading)` link scrolls to the matching heading (GitHub-style slug match against the heading tree).

### Verification Plan
- Publish (AOT) exits 0.
- Open `sample.md`; screenshot: TOC lists exactly the h1–h3 set from the file, indented by level.
- `MARKLITE_DEBUG=1` logs scroll-to-heading target on TOC click; assert offset changed after click.

**Verification results (2026-08-23, scripted `test-toc.ps1` via UI Automation):**
- Publish (AOT) exit 0; build 0 warnings.
- `sample.md`: log `toc built: 8 headings` — exactly the h1–h3 set; screenshot
  shows sidebar indented by level, View menu present.
- UIA `InvokePattern` click on TOC entry "Blockquote": log
  `scroll-to-heading #6 'Blockquote' offset 1367.3` — offset changed from 0,
  scroll clamped at document end (correct), current-section highlight showed
  "Table" which IS the section at the clamped viewport top (correct semantics).
- Headingless document: `toc built: 0 headings`, sidebar fully hidden
  (screenshot).

### Phase Summary
TOC sidebar delivered without Markdig (plan's AST assumption didn't hold — the
whistyun engine has no public AST):
- `HeadingParser` (src/MarkLite/HeadingParser.cs): parses RAW markdown — ATX
  `#`–`######` headings, skips fenced code, strips inline markup
  (links/images/`*_~`` ` ``), builds GitHub-style slugs with duplicate `-n`
  suffixes. Setext headings unsupported (recorded limitation).
- Heading positions come from the rendered controls: after each render,
  `CTextBlock`s with `Heading*` classes are collected in visual order and
  matched to parsed entries BY ORDER (count mismatch logged). Scroll-to =
  `TranslatePoint` to the viewer + set `ScrollValue`; anchor links resolve
  slug → index → same path (`ScrollToAnchor` wired as third callback into
  `MarkLiteHyperlinkCommand`, replacing the Phase-3 no-op).
- Current-section tracking: the engine's `HeaderScrolled` event is used ONLY
  as a scroll trigger; the nearest-above-viewport-top computation runs against
  our own controls because the event's `Header` type equates by Level+Text —
  ambiguous for repeated section names (this plan repeats "Verification Plan"
  eight times).
- Sidebar: fixed 250 px `Border` + `StackPanel` of flat `Button`s (real buttons
  → UIA-automatable verification), indent 12 px/level, hover + current styles
  in Window.Styles, colors from existing theme tokens. Ctrl+T (`OnKeyDown`)
  and View > Contents sidebar toggle `_tocVisible`, which persists across
  reloads; panel force-hides when the document has no headings.
- TOC rebuilds inside `RenderMarkdown`'s post-layout pass — same hook as the
  task-list marker hider, so reload and theme re-render refresh it for free.

## Phase 7: In-document search
Status: Complete

- [x] Ctrl+F opens find bar (Esc closes); case-insensitive substring search over the rendered document text. _(also Edit > Find… menu entry — gives UIA/menu access)_
- [x] Match count display ("3 of 17"), F3 / Shift+F3 (and Enter/Shift+Enter) next/previous with wraparound.
- [x] Current match highlighted distinctly from other matches; view scrolls to current match. _(current = solid orange + dark text; others = translucent gold keeping original foreground)_
- [x] Search state survives live reload (re-run search after re-render).
- [x] Zero matches: show "0 results", no crash, no beep loop.

### Verification Plan
- Publish (AOT) exits 0.
- Screenshot: find bar open, matches highlighted, count correct for a known term in `sample.md` (pre-count occurrences with `Select-String`).
- `MARKLITE_DEBUG=1` logs match count; assert equals `Select-String` count.

**Verification results (2026-08-23, scripted `test-search.ps1` via SendKeys, all PASS):**
- Publish (AOT) exit 0 (known IL2104 only); build 0 warnings.
- Ctrl+F + typed `line` in sample.md: log `search 'line': 8 matches` — equals raw
  regex count 8 (term chosen to appear only where raw == rendered text).
- Screenshots: find bar with "1 of 8", current match solid orange with dark text,
  other matches translucent gold; code-block matches keep syntax colors on
  non-current pieces; scroll landed inside the fenced block for a code match
  (line-proportional anchor).
- F3/Shift+F3: logs `search current 2/3/2 of 8`, backward wraparound to `8 of 8`.
- Zero matches (`zzqqxxplugh`): "0 results" label, `0 matches` logged, alive.
- Live reload: appended a line containing the term twice → `reload triggered` then
  `search 'line': 10 matches` re-applied automatically; alive.
- Esc: `search closed` logged, highlights fully reverted (screenshot), alive.
- Memory (SendKeys-driven, `measure-search.ps1`): search active costs +4–12 MB
  (awesome-README 78 KB / 95 matches: 103→107 MB; plan file 34 KB / 196 matches:
  81→93 MB); F3 navigation ~free; right after Esc WS sits a few MB higher
  (garbage from restore) until the 30 s idle trim collects — measured at +3 s
  only. Awesome baseline 103 vs Phase 5's 97 MB is fixture drift (newer, bigger
  revision), not a regression signal.

### Phase Summary
Find-in-document delivered with highlighting done IN the text layout, not as an
overlay: `DocumentSearch` (src/MarkLite/DocumentSearch.cs) splits text runs at
match boundaries and sets `Background` on the match pieces, so highlights wrap,
scroll and reflow with the document — no overlay geometry, no scroll sync.
- Why splitting: the engine's `TextPointer` API exposes no public index→pixel
  mapping (`Geometry`/`Distance` are internal), so overlay rectangles were out
  without reflection. `CInline.Background` exists, inherits, and repaints.
- Two inline systems handled: prose = `CTextBlock` trees of `CInline` (split
  `CRun`s, recurse `CSpan`s, replace the top-level `Content` list — the ONLY
  public way to force CTextBlock to rebuild text geometry; plain
  `InvalidateMeasure()` is a no-op when width is unchanged); code blocks =
  `SelectableTextBlock` with Avalonia `Run`s (split + `Inlines` Clear/AddRange).
  `CodeBlockOverride` now always emits `Inlines` (plain code as a single Run)
  so search has one code path.
- `CLineBreak` derives from `CRun` and must be matched BEFORE it in the type
  switch (contributes "\n", never split). `CImage`/`CInlineUIContainer`
  contribute their `AsString()` lengths (" $$Image$$ " / "") to keep offsets
  aligned with `CTextBlock.Text`; a match falling inside one is counted but has
  no visible highlight (recorded limitation). List/ordered markers are
  CTextBlocks too, so digit searches can match "1." markers (browsers don't —
  minor deviation).
- Undo model: every split records the pre-split list (block Content / span
  Content / code Inlines snapshot); `Clear()` restores them, `Detach()` forgets
  without undoing — called at the top of `RenderMarkdown` because re-render
  discards the recorded controls; the post-layout pass then re-applies the
  active search (this is what makes search survive reload and theme switch).
- Current-match move swaps Background/Foreground on already-split pieces only —
  `TextLineGeometry.Render` re-reads foreground at draw time, so recolor
  repaints without re-measure. Piece records remember the original foreground
  (syntax-colored code runs) for restore.
- Scroll-to-match is block-level (`TranslatePoint` + `ScrollValue`, same as
  TOC); for code blocks a line-proportional offset (match line / total lines ×
  block height) lands inside tall blocks. Posted at Loaded priority so split
  blocks re-measure first.
- UI: find bar docked under the menu (FindBar/FindBox/FindCountText in
  MainWindow.axaml), Edit > Find… menu, Ctrl+F/F3/Shift+F3/Esc in
  `Window.OnKeyDown`, Enter/Shift+Enter in the box's own KeyDown. 150 ms
  debounce on typing; F3 with a pending debounce flushes it instead of stepping
  stale matches. Theme tokens `MdSearchMatchBackground` /
  `MdSearchCurrentBackground` / `MdSearchCurrentForeground` in both variants;
  brushes resolved per Apply so theme switches re-resolve.
- Known cosmetic tradeoff: splitting a run can add a line-break opportunity at
  a match boundary inside a long word crossing the wrap point — text may
  re-wrap slightly while a search is active.
- Verification infra note: SendKeys-driven tests steal keyboard focus — ALWAYS
  warn the user and wait for explicit go before running them (learned the hard
  way 2026-08-23; standing rule).

## Phase 8: Tabs
Status: Complete

- [x] Tab strip: multiple documents open concurrently; File > Open and drag-drop open new tab (or focus existing tab if file already open). _(drag-drop now opens ALL dropped files; strip visible from 1 tab — the ✕ button is the only mouse-only close for a lone tab)_
- [x] Per-tab state: scroll position, TOC selection, search state, own FileSystemWatcher.
- [x] Ctrl+W closes tab (last tab closing shows empty/welcome state, app stays open); Ctrl+Tab cycles; middle-click closes. _(plus per-tab ✕ button and File > Close tab menu item)_
- [x] Second instance launched with a file: open as new tab in existing window via single-instance named-pipe handoff (no second process lingers).
- [x] Tab shows filename; tooltip shows full path; stale indicator (from live-reload work) per tab. _(banner shows active tab's message; stale inactive tabs tint their strip label amber)_

### Verification Plan
- Publish (AOT) exits 0.
- Open two files (sample.md + README.md); screenshot tab strip; switch tabs, confirm independent scroll offsets via debug log.
- Launch second instance with a third file: assert single MarkLite process remains and log shows handoff.
- Working set with 3 tabs open: record MB, still < 100 MB.

**Verification results (2026-08-23, scripted `test-tabs.ps1`, ALL PASS):**
Input-free by design — tabs opened via pipe handoff (secondary launches), switching
and closing via UIA InvokePattern, screenshots via PrintWindow; no focus steal.
- Publish (AOT) exit 0 (known IL2104 only); build 0 warnings.
- 3 tabs opened (sample.md, README.md, plan-file copies): secondary processes
  exited after `handed off to primary`, primary logged `handoff received` +
  `tab opened … (N tabs)`; exactly 1 MarkLite process throughout.
- Re-launch with an already-open file: `tab focused existing`, no fourth tab.
- Independent scroll: doc-b scrolled via TOC to 792.7 (clamped from target
  865.3), doc-a stayed 0.0 across switches, doc-b restored to exactly 792.7.
- Closed all tabs via ✕: `(0 tabs)` + `welcome state (no tabs)`, app alive.
- Working set with 3 tabs: **94 MB** (< 100). Screenshots: strip with active-tab
  emphasis + per-tab ✕, welcome page.
- RAM re-measure on final build (`measure-tabs.ps1`): sample.md single tab
  55 MB (Phase 5: 54 — no regression); 3 tabs 91 → 85 MB after idle trim;
  worst-case awesome-README **105 MB** — over the 100 target, but the pre-tabs
  Phase 7 build measured the same fixture at 103 MB: the fixture grew since
  Phase 5 (77 → 78.8 KB, more links), tabs cost ~2 MB. Honest status: the
  pathological-document target is now marginally missed by fixture drift;
  virtualized rendering (Future work) is the real fix.
- Verified by code review only (input injection avoided): Ctrl+W, Ctrl+Tab
  cycling, middle-click close — same code path (`CloseTab`/`ActivateTab`) the
  UIA-driven ✕/name buttons exercised.

### Phase Summary
Tab model on the Phase 0–7 foundation; the window became an orchestrator over
per-tab state:
- **`DocumentTab`** (src/MarkLite/DocumentTab.cs): each tab owns its
  `MarkdownScrollViewer` (rendered tree stays alive → instant switch), its
  `DocumentWatcher`, its `DocumentSearch`, TOC data (entries + heading
  controls), stale message, search term, and saved scroll offset. MainWindow
  swaps the active tab's viewer into `ViewerHost` and mirrors its state
  (title, stale banner, TOC panel, find box text).
- **Deferred background renders**: a detached viewer never lays out, so
  post-layout passes (marker hider, TOC collection) would see an empty tree.
  All content changes for inactive tabs (reload, theme switch) park in
  `tab.PendingText` and render on activation — `RenderTab` self-defers when
  the tab isn't active. Watcher reloads on the active tab render immediately.
- **Scroll preservation across switches**: reattaching a viewer resets its
  ScrollViewer offset AND the freshly attached tree reports a smaller extent
  at Loaded priority (first restore gets clamped). `RestoreActiveTabScroll`
  therefore sets the offset twice — Loaded, then Background priority.
  Gotcha: `ScrollToHeading` logs its TARGET offset; the actual applied value
  can be clamped lower — tests must compare saved-vs-restored, not target.
- **Single instance** (src/MarkLite/SingleInstance.cs): per-user named pipe
  (`MarkLite-{user}`). Primary claims the server in `Main` before Avalonia
  starts; a secondary with a file arg sends the full path and exits (an
  unreachable primary → runs standalone rather than losing the document).
  Gotcha fixed: server must call `Disconnect()` unconditionally after each
  connection — `IsConnected` turns false when the client closes, but the pipe
  handle stays in connected state and the next `WaitForConnection` throws,
  silently killing handoff #2+.
- Welcome state: zero tabs → lazy welcome viewer, strip hidden, TOC/find
  disabled. The Phase-1 temporary "no arg → load testdata\sample.md" default
  is GONE: no argument now opens the welcome state.
- Find bar: term is per-tab; switches sync the box via `_suppressFindEvents`
  (no debounce trigger). Esc clears search in ALL tabs. Theme switch
  re-renders the active tab, defers the rest.
- Tab strip: Border per tab (name Button + ✕ Button inside — nested buttons
  avoided by making the tab a Border, which also takes the middle-click);
  active = `TabItemActive` class, stale = `TabItemStale` (amber label).
- **Avalonia 11 MenuItem automation peers support neither Invoke nor
  ExpandCollapse** — menus cannot be UIA-driven; tests must reach features
  through buttons or keyboard. (Tab ✕/name are real Buttons partly for this.)
- Verification infra: GUI-launching tests pop windows over the user's work —
  batch iterations, move the window to the second monitor immediately, warn
  the user first (rule in memory).

## Phase 9: File association — "Open with" registration
Status: **Dropped (2026-08-23, user decision)** — folded into Phase 11.

Phase 11 already registers the HKCU ProgID + `OpenWithProgids` from inside the
app, against the installed exe, and undoes it on uninstall. The only thing this
phase added was a dev-machine script pair pointing at `publish\MarkLite.exe` —
not worth maintaining a second implementation. Its two guardrails moved into
Phase 11's checklist: back up the current `.md` HKCU keys before writing, and
never touch `UserChoice` (the user's existing handler stays the double-click
default unless they give a fresh explicit yes).

## Phase 10: Markdig-stack migration spike (MarkView, Avalonia 12)
Status: Complete — verdict **ADOPT**

**Why:** the whistyun engine has no real parser (regex port of MarkdownXAML) —
Phases 2/6/7 hand-built task lists, heading parsing, and search against its
internals. Markdig (xoofx) is a real CommonMark/GFM parser the user knows and
trusts from Markdig.Wpf. MarkView.Avalonia (MIT, Markdig-based, Avalonia 12)
would DELETE maintained code (HeadingParser, TaskListPreprocessor/MarkerHider)
and unlock Mermaid via Mermaider. Rendering layer is young (~21 stars) and AOT
is undocumented — hence a spike with a hard decision gate, not a migration.

**Branch strategy (user, 2026-08-23):** run this BEFORE Velopack so packaging
happens once, on whichever stack wins. Spike lives on branch `markdig-spike`;
adopt → full migration phases happen on that branch and Velopack ships the
migrated app; reject → delete branch, Velopack ships the Avalonia 11 stack.
Do not start the migration inside the spike — evidence and a verdict only.

- [x] Branch `markdig-spike`; spike project `spike/MarkdigSpike` (Avalonia 12.1.1 + MarkView.Avalonia 12.2.1), rendering `testdata/sample.md` and `testdata/sample-plan.md`.
- [x] **Gate 1 — AOT:** publish with the Phase 0 VS-less toolchain env (note if Avalonia 12 needs a newer MSVC/SDK). Core package must survive NativeAOT + trim and render correctly. Fail → record and stop. _(PASS — same toolchain env works unchanged; publish exit 0 with ZERO trim warnings, not even the IL2104 the whistyun stack carries)_
- [x] **Gate 2 — task lists:** checkboxes render from Markdig's task-list extension; verify they can be styled to the Phase 2 prominence spec (filled accent box vs outline). Fail → check LiveMarkdown.Avalonia as alternate before rejecting (its task-list support is undocumented). _(PASS via custom renderer through the public extension API — see summary; LiveMarkdown not needed)_
- [x] **Gate 3 — footprint:** working set + cold start on sample-plan.md and the awesome-README fixture; must be in the same class as current (55–105 MB, ~50 ms) — a big regression kills the migration's point. _(PASS — parity on RAM, cold start 70–98 ms)_
- [x] **Gate 4 — search feasibility:** inspect the rendered control tree; confirm a Phase 7-style run-splitting highlight (or equivalent) is possible. Document the approach. _(feasible and SIMPLER — see summary)_
- [x] Optional-package sweep (each separately AOT-gated): syntax highlighting (TextMateSharp — native Oniguruma; if it breaks AOT or bloats, plan ColorCode injection instead), Mermaid (Mermaider), math (CSharpMath). Record which are viable. _(ALL THREE viable under AOT; TextMate costs +19 MB exe — see summary)_
- [x] Check AST access for TOC (HeadingBlock positions → rendered controls), link-click routing (`LinkClickedEvent`), scroll save/restore API. _(all built in: `HeadingEntries`/`Anchors`/`TableOfContents`/`ScrollToAnchor`, `LinkClickedEvent`; scroll offset resets per render — must save/restore manually, MarkLite's tab code already does)_
- [x] **Decision gate:** verdict ADOPT (plan full migration phases on the branch, all before Velopack) or REJECT (delete branch; Velopack ships Avalonia 11 stack). Record evidence either way in the Phase Summary. _(**ADOPT** — migration Phases 10A–10D added below)_

### Verification Plan
- Spike publish (AOT) exit 0 on the winning configuration; published exe screenshot with sample-plan.md: checkboxes, tables, highlighted fences.
- WebView2 attribution: nothing for the spike.
- Numbers table in summary: WS + cold start, spike vs current MarkLite, same fixtures.

**Verification results (2026-08-23):**
- AOT publish exit 0 at every configuration step (base → +Mermaid → +TextMate →
  +Math), zero trim/AOT warnings throughout. Same VS-less toolchain env as
  Phase 0 — Avalonia 12 needs no newer MSVC/SDK.
- Screenshots saved: `plans/reference/markdig-spike-taskboxes.png` (prominent
  checkboxes + right-aligned 4-col table, sample-plan.md),
  `markdig-spike-highlight.png` (TextMate-colorized C# fence + GFM table),
  `markdig-spike-mermaid.png` (native mermaid flowchart) — all from the
  PUBLISHED AOT exe.
- WebView2 attribution with spike running: no group for MarkdigSpike.
- Numbers (spike full config = Mermaid+TextMate+Math unless noted; MarkLite
  numbers from Phase 5/7/8 records — fresh side-by-side blocked by the user's
  live MarkLite instance, single-instance pipe would have handed off):

  | Metric | Spike (Av12+MarkView) | MarkLite (Av11+whistyun) |
  |---|---|---|
  | WS sample.md / sample-plan.md (base config) | 54 / 56 MB | 54–55 MB |
  | WS sample-plan.md (full config) | 74 MB | — |
  | WS awesome-README 78.8 KB (full config) | 101 MB | 103–105 MB |
  | Cold start median (base / full) | ~70 / 98 ms | 48 ms |
  | Exe (base / +TextMate / +Math) | 21 / 40.2 / 42.3 MB | 21.8 MB |
  | Deployment total (base / full) | ~35 / ~56 MB | 32.5 MB |

### Phase Summary
**Verdict: ADOPT.** Every gate passed; the MarkView/Markdig stack deletes
homegrown subsystems, unlocks mermaid + math natively, and costs nothing
meaningful in footprint. Migration runs as Phases 10A–10D on this branch,
Velopack (Phase 11) ships the migrated app.

Gate evidence and key findings:
- **Gate 1 (AOT): cleaner than the current stack.** Publish exit 0, zero
  warnings (whistyun carries IL2104). All satellite packages AOT-clean too,
  including TextMateSharp's native `libonigwrap.dll` (ships beside the exe;
  native interop is not a trim hazard).
- **Gate 2 (task lists): pass, by design not by accident.** Stock rendering is
  a small ☑/☐ glyph whose checked/unchecked states share identical style
  classes — NOT stylable to the prominence spec directly. But the library's
  public extension API (`IMarkViewExtension` + `AvaloniaRenderer.ReplaceOrAdd`)
  exists exactly for renderer swaps: `spike/MarkdigSpike/`
  `ProminentTaskListExtension.cs` (~130 lines) replaces `ListRenderer` with one
  emitting the Phase 2 accent-box checkbox, plus a no-op inline
  `TaskListRenderer` (the stock one re-emits the glyph otherwise —
  `SkipNextTaskList` is internal). Replaces MarkLite's 3-part homegrown
  pipeline (TaskListPreprocessor sentinels + inline parser + visual-tree
  MarkerHider).
- **Gate 3 (footprint): parity.** Base-config RAM identical to current;
  full-config worst case 101 MB vs current 103–105 on the same fixture. Cold
  start 70–98 ms vs 48 — slower but 5× under the 500 ms target. GC settings
  (`GCConserveMemory=9` etc.) carried over; spike lacks MarkLite's post-render
  GC.Collect and idle trim, so migrated numbers should land slightly lower.
- **Gate 4 (search): simpler than Phase 7.** Blocks are
  `MarkdownSelectableTextBlock` (a real `SelectableTextBlock`) with STANDARD
  Avalonia `Inlines` (Run/Span) — run-splitting works with public APIs, no
  CInline/CTextBlock special cases, one code path for prose and code. Bonus:
  the control's `DocumentSelectionLayer` already maintains an ordered index of
  every text block with plain text + absolute char offsets (`IndexEntry`) —
  the match-finding half of DocumentSearch may reduce to walking that.
- **Optional packages, each AOT-gated separately:** Mermaid (Mermaider 0.12.2)
  renders real native flowcharts, theme-aware, ~+10 MB WS on a diagram doc —
  adopt. TextMate highlighting: VS-Code-quality colors but +19 MB exe (grammar
  assets); ColorCode injection via the public `ICodeHighlighter` slot is the
  lean alternative — decide in 10A. Math (CSharpMath) typesets inline + block
  TeX, +2 MB exe — adopt (cheap, plan docs occasionally use it).
- **Free stuff the current stack hand-built:** `Pipeline` property (Markdig
  pipeline, `UseSupportedExtensions()` = task lists/tables/autolinks/emoji/
  strikethrough is even the default), `HeadingEntries` + `Anchors` +
  `TableOfContents` tree + `ScrollToAnchor()` (deletes HeadingParser),
  `LinkClickedEvent`, text selection layer with `GetSelectedText`/clipboard.
- **Quirks found (migration must handle):**
  - Viewer resets scroll offset on every `Markdown` set — no
    `SaveScrollValueWhenContentUpdated` equivalent; reuse the tab code's
    save/restore.
  - Wrap: set `ScrollViewer.HorizontalScrollBarVisibility=Disabled` attached
    property on the viewer (default lets wide content overflow).
  - Theme's default code font is "Cascadia Code" → ligatures mangle `-->` in
    mermaid-as-text/code; restyle to Cascadia Mono (one setter,
    `markdown-code-block` class).
  - Styling is class-based (`markdown-h1`, `markdown-code-block`, …) — the
    Phase 2 MM-dark theme ports as app styles targeting those classes; no
    MarkdownStyle replacement dance.
  - `x:Name` worked fine in the demo; the Phase 0 codegen workaround is
    whistyun-specific.
- **Virtualization (user question 2026-08-23):** MarkView also realizes the
  full control tree up front — no free virtualization. But it keeps the real
  Markdig AST (SourceSpan per block) and maps block → control explicitly, so
  the Future-work virtualized host is tractable there (swap RootPanel for a
  virtualizing panel + realize per block); on whistyun it would require
  writing a parser first. Migration strengthens that future option.
- Verification-infra gotcha burned an hour: on a 150 % display, a
  non-DPI-aware PowerShell + PrintWindow captures the window cropped to 2/3
  width — looks EXACTLY like a text-wrap bug in the app. Capture scripts must
  call `SetProcessDpiAwarenessContext(-4)` first. (Session `capture.ps1`
  fixed; MarkLite itself was never at fault.)
- Spike stays on the branch as evidence + API reference until 10D deletes it.

## Phase 10A: Migration — core stack swap + rendering parity
Status: Complete

Swap `src/MarkLite` to Avalonia 12.1.1 + MarkView.Avalonia 12.2.1 and reach
Phase 2 rendering parity. Keep every MarkLite feature file compiling; this
phase owns rendering only.

- [x] Bump csproj: Avalonia 12.1.1, drop `Markdown.Avalonia.Tight`, add `MarkView.Avalonia` (+`.Mermaid`, `.Math`; TextMate decision below). Fix Avalonia 11→12 API breaks outside rendering as they surface (build must stay 0-warning). _(only break: TextBox.Watermark → PlaceholderText)_
- [x] Replace `MarkdownScrollViewer` with `MarkdownViewer` in MainWindow/DocumentTab; pipeline = `UseSupportedExtensions().UseMathematics()`; `HorizontalScrollBarVisibility=Disabled`; `UseMermaid()`.
- [x] Port `ProminentTaskListExtension` from the spike (namespace MarkLite.Rendering); delete TaskListPreprocessor, task inline parser, TaskListMarkerHider. _(landed as `MarkLiteRenderExtension` — also carries the code-block renderer)_
- [x] Port MM-dark theme to class-based styles (`markdown-h1`…, code block, chips, tables, blockquote, links) over the existing App.axaml theme tokens; ~~code font Cascadia Mono~~ fonts now BUNDLED per user request (see summary); keep both variants legible. _(light variant: tokens unchanged since Phase 4; visual re-check deferred to 10D's measurement pass — flipping the system theme mid-session would disturb the user's desktop)_
- [x] Syntax highlighting decision: ColorCode retained — but NOT via `ICodeHighlighter` (per-line API, loses multi-line lexing state); the custom code-block renderer calls `CodeHighlighter.Colorize` over the whole block, exactly like the old CodeBlockOverride. TextMate package rejected (+19 MB exe).
- [x] Links: move MarkLiteHyperlinkCommand routing onto `LinkClickedEvent` (http/mailto → browser, relative .md → LoadFile, #anchor → `ScrollToAnchor`). _(#anchors never reach the event — the viewer resolves them internally via its own anchor table; MarkLiteHyperlinkCommand keeps its anchor branch as dead-code safety until 10B unifies slugs)_
- [x] Verification: AOT publish exit 0; sample.md + sample-plan.md screenshots hit the Phase 2 checklist (incl. prominent checkboxes); mermaid fence now renders a DIAGRAM (constraint lifted — plan intent updated); WebView2 attribution empty.

**Verification results (2026-08-23):**
- Build 0 warnings; AOT publish exit 0, zero trim warnings (IL2104 is GONE with
  the whistyun package).
- Screenshots (published AOT exe, dark): headings/paragraph rhythm, Roboto
  body, Fira Code Retina code with ColorCode colors + fence label + h-scroll
  panel, GFM table grid with header band + right-aligned numerics, blockquote
  rule, prominent checkboxes, TOC sidebar with current-section highlight, tab
  strip — all correct. Mermaid fence renders a native diagram.
- Scripted reload test: edit → `reload triggered` + re-render; delete →
  `file missing` (stale path); recreate → reload; process alive throughout.
- Welcome state (no args) works; WebView2 attributed to MarkLite: 0.
- Numbers (informal; full suite is 10D): sample.md/sample-plan 65–72 MB,
  awesome-README 108 MB (was 105 pre-migration), first content render
  58–79 ms, welcome 60 MB. Exe 37.8 MB + libSkiaSharp 11.6 + libHarfBuzzSharp
  2.0 = 51.4 MB deployment (was 32.5) — mermaid/math/fonts cost, see summary.

### Phase Summary
Stack swapped; the app now runs Avalonia 12.1.1 + MarkView.Avalonia 12.2.1
(Markdig) end to end. What changed and what future phases must know:
- **Deleted** (obsolete with a real parser): MarkLitePlugin, CodeBlockOverride,
  TaskListPreprocessor, TaskListMarkerHider, MarkdownTheme.axaml.cs. TOC's
  post-render pass no longer hides bullets — task items never get bullets.
- **`Rendering/MarkLiteRenderExtension.cs`** (IMarkViewExtension) is the
  renderer-swap hub: ProminentListRenderer (accent-box checkboxes; keeps stock
  list classes so the selection layer + builtin styles still key off them),
  SilentTaskListRenderer (stock inline ☑ suppressed; its skip flag is
  internal), MarkLiteCodeBlockRenderer (label + horizontal scroll +
  whole-block ColorCode; forwards mermaid fences to the Mermaid package's
  renderer). **Registration order is load-bearing** — UseMermaid() BEFORE
  Extensions.Add(RenderExtension) (front-insert lands ahead of mermaid's
  broad fence-grabber), UseMath() after (re-fronts itself). Comments in
  CreateTab and Register explain.
- **Known tradeoff:** custom code blocks (Border > DockPanel shape) fall out
  of the viewer's cross-block drag-selection index, which only registers
  Border>TextBlock code shapes. The SelectableTextBlock inside gives
  per-block selection/copy — same behavior class as the old stack. Candidate
  upstream PR: pluggable block registration.
- **Fonts bundled** (user request 2026-08-23, ~1.9 MB of AvaloniaResource
  TTFs in `Assets/Fonts/`): code = **Fira Code Retina** (ligatures active);
  body = **Roboto** default with **View > Body font** menu (Roboto / Lexend /
  Segoe UI) swapping the `MdBodyFontFamily` app resource live via
  DynamicResource. Lexend bundled (Regular/Medium/Bold) but has NO italic
  face — markdown italics under Lexend rely on synthetic oblique; Roboto
  ships real italics, hence default. Font persistence = future settings work
  (Phase 11 note). Cascadia ligature concern from the spike is moot: user
  chose a ligature font for code deliberately.
- **Theme:** MarkView's builtin theme StyleInclude'd in App.axaml, MarkLite's
  rewritten class-based `Rendering/MarkdownTheme.axaml` after it (later wins).
  Gotcha found: paragraphs inside list items keep their 8px top margin and
  push text below the marker — fixed by a list-scoped margin-0 style; marker
  LineHeight matches paragraph (22).
- **Scroll:** viewer resets offset on every content set and hides its
  ScrollViewer (template part). `DocumentTab.Scroller`/`ScrollY` wrap visual
  lookup; `RenderTab(tab, text, restoreScrollY)` does a two-pass restore
  (Loaded + Background, clamp reasons in comments); current-section tracking
  hooks ScrollChanged lazily (posted at Loaded after first attach — template
  is not applied at AttachedToVisualTree time).
- **DocumentSearch is a STUB** — find bar shows 0 results until 10C. Old
  implementation retrievable from git history.
- **`MARKLITE_STANDALONE=1`** skips single-instance handoff/claim — added
  because verification launches kept handing off into the user's live
  MarkLite session (they gained a few stray tabs; twice).
- **`publish/` still holds the OLD Avalonia-11 build** — it was locked by the
  user's running instance; the migrated build is in `publish-stage/`
  (gitignored). After the user closes MarkLite, rerun `build/publish.ps1`.
- Fixture touch: sample.md's mermaid heading no longer claims "expected:
  plain code block".
- Exe grew 21.8 → 37.8 MB (mermaid+math renderers, ColorCode, fonts; Avalonia
  12's Skia is also +2.6 MB). RAM/cold-start stayed in class. If exe size
  ever matters, the Math and Mermaid packages are clean opt-outs.

## Phase 10B: Migration — TOC + anchors on MarkView APIs
Status: Complete

- [x] Rebuild TOC sidebar from `TableOfContents`/`HeadingEntries` + `Anchors` (delete HeadingParser; setext headings start working). _(`HeadingEntries`/`Anchors` are INTERNAL in 12.2.1 — the public surface is the `TableOfContents` tree + `TableOfContentsMaxDepth` + `ScrollToAnchor`; HeadingParser.cs deleted, setext headings verified working)_
- [x] Scroll-to-heading + current-section tracking against `Anchors` controls (replace HeaderScrolled machinery); anchor links via `ScrollToAnchor`. _(HeaderScrolled machinery was already gone in 10A — tracking rides the template ScrollViewer's ScrollChanged. Position math still uses the rendered `markdown-hN` controls, paired positionally with the flattened TOC; `ScrollToAnchor` is the fallback for non-heading anchors)_
- [x] Verification: test-toc.ps1 pattern — heading count matches, UIA click scrolls, headingless doc hides sidebar; AOT publish exit 0.

### Verification Plan
- AOT publish exits 0, build 0 warnings.
- Heading count logged by the app equals an independent scan of the file.
- UIA `InvokePattern` click on a sidebar entry scrolls (logged offset > 0).
- Headingless document: sidebar hidden, `toc built: 0 headings`.
- Setext + h4–h6 headings appear (they could not before).
- Long document with repeated heading names: entries stay position-addressed.

**Verification results (2026-08-23, scripted `test-toc.ps1` / `test-toc-long.ps1`
via UIA, plus one mouse-click test for the anchor path — ALL PASS):**
- Build 0 warnings; AOT publish exit 0, zero trim warnings.
- `sample.md`: `toc built: 8 headings` (file has exactly 8), no `toc mismatch`;
  sidebar buttons = the 8 heading titles; UIA click on "Blockquote" logged
  `scroll-to-heading #6 'Blockquote' offset 1411.3`.
  Screenshots `plans/reference/markdig-toc-sidebar.png`, `markdig-toc-click.png`.
- **Setext headings now work** (impossible with the old HeadingParser):
  `toc built: 3 headings`, "Setext Level One"/"Setext Level Two" listed.
  Screenshot `markdig-toc-setext.png`.
- h1–h6 fixture: all 6 listed (`TableOfContentsMaxDepth = 6`), no mismatch.
- Headingless document: `toc built: 0 headings`, sidebar hidden.
- **Long documents** (`test-toc-long.ps1`): this plan file — logged 53 headings
  == independent ATX scan (53), no mismatch, first render 186 ms, 12 entries
  named "Verification Plan"; clicking the LAST one logged
  `scroll-to-heading #48 'Verification Plan' offset 23752.0` and landed on
  Phase 11's section (screenshot `toc-planfile.png` in the session scratchpad)
  — proof entries are addressed by position, not by name. README.md: 6 == 6,
  click → offset 912.7.
- Anchor link `[go to target](#target-section)`: inline links expose no UIA
  Invoke pattern, so this needed one synthetic mouse click (user approved).
  Result: the view jumped to "Target Section" (screenshot
  `markdig-anchor-jump.png`) with NO MarkLite log line — the viewer resolves
  fragments internally and never raises `LinkClicked` for them (confirms the
  10A finding). Its scroll is `BringIntoView`, so the target lands at the
  bottom edge rather than the top.
- Note for 10D: working set on the plan file measured **126 MB** right after a
  23 000 px scroll jump (README 66 MB). Scroll-filled glyph/layout caches, the
  known pathological case — 10D's measurement pass owns the honest number.

### Phase Summary
TOC now comes from the parser instead of a hand-written scanner:
- **`HeadingParser.cs` is deleted** (~110 lines of regex + slug logic). Entries
  come from `MarkdownViewer.TableOfContents`, a TREE of `MarkView.Avalonia.
  TocEntry` (`Level`/`Text`/`Slug`/`Children`) built from the Markdig AST during
  render; `MainWindow.FlattenToc` walks it depth-first back into document order
  for the flat indented sidebar. `MarkLite.TocEntry` is gone — the type is now
  MarkView's.
- **`TableOfContentsMaxDepth = 6` is load-bearing**: entries are paired
  POSITIONALLY with the rendered `markdown-hN` controls (`RebuildTocData`), so a
  shallower depth would drop deep headings from the list while their controls
  remain and shift every later pairing. Verified with an h1–h6 fixture.
- Gained for free: setext (`===`/`---`) headings, and correct handling of `#`
  lines inside fenced code (the parser knows what a fence is).
- Position math and current-section tracking are unchanged from 10A
  (`TranslatePoint` against the heading controls + ScrollChanged) — deliberately
  NOT switched to `ScrollToAnchor`, which is a `BringIntoView` jump landing the
  heading at the viewport bottom and reports no offset for tests to assert.
- `ScrollToAnchor(slug)` in MainWindow: slug lookup in the tab's flattened
  entries → same scroll path as a sidebar click; unknown slugs (explicit ids,
  footnotes) fall through to `viewer.ScrollToAnchor`. Slugs are the viewer's own,
  so they cannot drift from its anchor table.
- Dead-path recorded honestly: `MarkLiteHyperlinkCommand`'s `#anchor` branch
  never fires — the viewer swallows fragment clicks. Kept as a safety net with a
  comment saying so; if MarkView ever routes fragments through `LinkClicked`,
  MarkLite's own (top-aligned) scroll takes over automatically.
- Cosmetic issue seen on the plan file, not fixed here: long sidebar titles are
  clipped by the fixed 250 px panel with no tooltip (e.g. `Phase 9: File
  association — "Open witl`). Candidate polish for 10D.

## Phase 10C: Migration — search port
Status: Complete

- [x] Port DocumentSearch to standard Inlines run-splitting (one code path; try `DocumentSelectionLayer`'s text index for match enumeration). _(one code path achieved; the selection layer's index is INTERNAL — `DocumentSelectionLayer`/`IndexEntry` are non-public in 12.2.1 — so blocks are enumerated from the visual tree as before)_
- [x] Keep: debounce, F3 nav, wraparound, count label, reload/theme survival, per-tab state. _(all MainWindow-side and untouched; verified by test)_
- [x] Verification: test-search.ps1 pattern (match count vs Select-String, highlight screenshots, zero-match, reload re-apply); AOT publish exit 0. SendKeys tests: warn user + wait for go.

### Verification Plan
- AOT publish exits 0, build 0 warnings.
- Match count logged equals an independent regex count over the raw file.
- Screenshots: current match distinct from other matches, syntax colors kept.
- F3 / Shift+F3 step and wrap; zero-match shows "0 results" without crashing.
- Live reload re-applies the active search; Esc reverts highlighting fully.

**Verification results (2026-08-23, scripted `test-search.ps1`, SendKeys — user
approved the focus steal — ALL 11 CHECKS PASS):**
- Build 0 warnings; AOT publish exit 0, zero trim warnings.
- `search 'line': 8 matches` == 8 raw occurrences in the fixture; count label
  "1 of 8"; `search scroll to match 1 offset 0.0` (first match is on screen).
- F3 → `search current 2 of 8`, `3 of 8`; Shift+F3 sequence `2,3,2,1,8` — wraps
  backward past the first match.
- Screenshots: `search-first-match.png` — prose match solid orange with dark
  text; the match inside the `inline code` chip highlighted gold ON the chip
  (zoom crop `crop-inline-chip.png`); `search-third-match.png` — six code-block
  matches gold with ColorCode syntax colors intact on non-current pieces,
  current one solid orange.
- Zero matches (`zzqqxxplugh`): `0 matches`, label "0 results", alive.
- Live reload: appended a line containing the term twice → `reload triggered`
  then counts `8,8,10` — the active search re-applied itself on the new tree.
- Esc: `search closed`, screenshot shows highlighting fully reverted (bold,
  italics, inline-code chips, links all normal). Working set 78 MB at the end.
- Not re-tested here (unchanged MainWindow code, covered in Phases 7–8):
  per-tab search term sync on tab switch, theme-switch re-apply.

### Phase Summary
`DocumentSearch` rewritten for the MarkView tree — 558 lines down to ~400, and
the two-inline-system split from Phase 7 is gone:
- **One code path.** Every searchable block is a `TextBlock` (prose blocks are
  `MarkdownSelectableTextBlock`, code blocks the `SelectableTextBlock` inside
  MarkLite's code panel) holding standard Avalonia `Inlines`. No CInline /
  CTextBlock special cases, no separate prose and code splitters, one
  `CloneRun` and one `SetPieceState`.
- **`DocumentSelectionLayer`'s text index was NOT usable**: the spike listed it
  as a possible match-enumeration shortcut, but `DocumentSelectionLayer` and
  `IndexEntry` are internal in 12.2.1. Blocks come from
  `GetVisualDescendants().OfType<TextBlock>()` instead.
- **Spans are recursed into and kept** — only their children are replaced. That
  matters for `MarkdownHyperlink` (a `Span` subclass): the viewer hit-tests
  links by walking the inline collection with a character index, so a highlight
  inside link text leaves clicking intact.
- **Change propagates upward**: a split deep inside a span marks its ancestors
  changed so the top-level collection is also rebuilt (Clear + AddRange) — that
  is what reliably makes the owning TextBlock re-measure. Undo records are
  restored in reverse (children before parents).
- **Text-only blocks** (no inlines at all) are handled by building split runs
  from `TextBlock.Text`; clearing the inlines restores the original render,
  since non-empty Inlines take precedence over Text.
- **Chrome is skipped** — `markdown-list-marker`, `CodeLangLabel`,
  `TaskCheckGlyph`. This fixes Phase 7's known deviation where searching digits
  lit up every ordered-list marker.
- Scroll-to-match keeps the Phase 7 behavior (block position via
  `TranslatePoint`, plus a line-proportional offset inside tall code blocks),
  now driven through the viewer's template `ScrollViewer` like the TOC code.
- Verification-infra note, re-confirmed on Avalonia 12: **menu items still
  expose no UIA Invoke or ExpandCollapse pattern** (probe: File/Edit/View
  advertise `ScrollItem` only), so the find bar cannot be opened without a
  keystroke — search tests need SendKeys and therefore user approval.

## Phase 10D: Migration — integration, measurements, wrap-up
Status: Complete

- [x] Tabs/reload/theme-switch on the new viewer: per-tab scroll save/restore (viewer resets offset on render — reuse tab code), deferred inactive-tab renders, stale banner, single-instance handoff unchanged. _(found and fixed a real crash — mermaid + tab switching, see summary; viewers now stay attached and scroll survives exactly)_
- [x] Re-run Phase 5 measurement suite: WS (sample, plan file, awesome-README), 5-run cold start, WebView2, binary sizes. Compare against Phase 5/8 numbers in a table; honest verdict on the <100 MB worst case. _(target MISSED on documents ≥ ~40 KB — 111 MB; verdict recorded below, not rationalized)_
- [x] Re-add post-render GC.Collect + idle trim if the migration lost them. _(both survived the migration; the idle trim now recovers only ~1 MB — noted)_
- [x] Delete `spike/MarkdigSpike`; update README (stack description, sizes, mermaid/math now supported). _(also removed 1.4 GB of untracked AotSpike build leftovers)_
- [x] Merge decision: present the branch diff summary to the user; user merges `markdig-spike` → `main` (or says to). _(summary presented; merge is the user's call)_

### Verification Plan
- AOT publish exits 0, build 0 warnings.
- Integration: tabs via pipe handoff, per-tab scroll across switches, active-tab
  reload, deferred inactive-tab render, stale banner, welcome state.
- Theme: live flip in both directions, both variants legible.
- Measurements: WS per fixture, 5-run cold start median, WebView2 attribution,
  deployment size.

**Verification results (2026-08-23):**
- Build 0 warnings; AOT publish exit 0, zero trim warnings.
- `test-integration.ps1` — **12/12 PASS**, input-free (pipe handoff + UIA):
  single process after 2 handoffs, 3 tabs, re-launch focuses the existing tab,
  **tab scroll saved 13990 == restored 13990** (exactly, no clamping),
  active-tab reload, inactive-tab deferral then render on activation, stale
  banner on delete, reload on recreate, welcome state after closing all tabs.
- `test-theme.ps1` — PASS: `theme changed to Light` → `theme changed to Dark`,
  live both directions, alive throughout, user's registry value restored.
  Light-variant screenshot re-checked (the item 10A deferred): sidebar, table
  grid with right-aligned numerics, blockquote, inline-code chips all legible.
- WebView2 processes attributed to MarkLite: **0** (MarkdownMonster, olk and
  SearchHost show up — other apps, as always).

**Measurements, migrated stack vs pre-migration records:**

| Metric | Before (Av11 + whistyun) | After (Av12 + MarkView) |
|---|---|---|
| WS sample.md | 54 MB | **69 MB** (now renders a mermaid diagram) |
| WS sample-plan.md | 55 MB | **65 MB** |
| WS plan file | 74 MB (34 KB) | **111 MB** (40 KB) |
| WS awesome README | 97 MB (77 KB, Ph5) · 103–105 (78.8 KB, Ph7/8) | **111 MB** (77 KB) |
| Cold start median, 5 warm runs | 48 ms | **55 ms** (59/62/53/53/55) |
| First render, large docs | — | 148–181 ms |
| Exe | 21.8 MB | **37.9 MB** |
| Deployment total | 32.5 MB | **51.4 MB** (exe + Skia 11.6 + HarfBuzz 2.0) |
| WebView2 processes | 0 | 0 |

**Honest verdict on the <100 MB target: missed on large documents.** Typical
plan-sized docs sit at 65–69 MB (was 54–55); anything from ~40 KB of markdown
up lands at ~111 MB. Two separate costs, both measured:
- **Baseline +11–15 MB** — Avalonia 12, the mermaid/math renderers, bundled
  fonts, and (for sample.md) an actually-rendered diagram instead of a code
  block.
- **Per-KB tree cost roughly doubled**: ≈0.6 MB per KB of markdown before
  ((74−54)/34), ≈1.15 MB per KB now ((111−65)/40). MarkView realizes more
  control per block and its selection layer keeps a text index per block.
The idle trim, which used to claw back tens of MB, now recovers ~1 MB — the
growth is live control tree, not garbage. The real fix stays the Future-work
item: virtualized rendering, which the migration made *tractable* (real AST,
explicit block→control mapping) where the old stack made it impossible.

### Phase Summary
Integration surfaced one genuine crash, and the measurement pass says the
migration costs real memory for real capability.
- **Bug found and fixed: switching tabs crashed on any document containing a
  mermaid diagram.** `MermaidBlockRenderer` registers a
  `DetachedFromLogicalTree` handler that calls `Cancel()` on an already-disposed
  `CancellationTokenSource`, so the SECOND detach of such a viewer threw
  `ObjectDisposedException` — mid-`ActivateTab`, leaving the tab half-switched
  (viewer swapped, sidebar still showing the previous document). Found by a
  temporary try/catch around ActivateTab after the log showed a missing
  "tab switched" line. **Fix: viewers are never detached.** `ViewerHost` is now
  a `Panel` holding every tab's viewer; activation flips `IsVisible`. Detach
  happens once, at tab close. Bonus: scroll offsets survive switches exactly
  (13990 → 13990, previously clamped), because nothing re-attaches. Worth
  reporting upstream — the handler should null-check/dispose-guard its CTS.
- **Symbols are no longer published**: `publish/` was shipping 250 MB of pdb
  (ILC's 145 MB `MarkLite.pdb` plus Skia/HarfBuzz natives) — 303 MB total for a
  52 MB app. Fixed in csproj, not by deleting files afterwards:
  `DebugType=none` for Release (this is the lever — setting
  `NativeDebugSymbols` directly does nothing, the ilcompiler targets import
  after the project and re-derive it from `DebugType`; `StripSymbols` is
  Unix-only, every strip step in `Microsoft.NETCore.Native.targets` is gated on
  `'$(_targetOS)' != 'win'`), plus an `ExcludeSymbolsFromPublish` target that
  removes `.pdb` from `ResolvedFileToPublish` for the native package symbols.
  Deployment 303.3 → **51.4 MB**. Gotcha that cost a cycle: a stale
  `bin\...\native\MarkLite.pdb` from an earlier build kept being copied and
  looked like the flag not working.
- Spike deleted; README rewritten for the new stack (mermaid/math as features,
  new RAM/size numbers, `MARKLITE_STANDALONE` documented).
- Verification-infra notes: UIA peers for controls destroyed by a TOC/tab
  rebuild throw `ObjectDisposedException` on access — automation helpers must
  re-query and retry; sidebar entries below the fold need
  `ScrollItemPattern.ScrollIntoView()` before `Invoke`. Test windows are parked
  on the secondary monitor with `SWP_NOACTIVATE` and never activated — they
  must never pop over the user's foreground work.

## Phase 11: Velopack packaging + GitHub distribution
Status: Complete — except the live GitHub release, deferred by the user

Distribute MarkLite via GitHub Releases with install + auto-update using
[Velopack](https://velopack.io) (Squirrel.Windows successor; claims
NativeAOT/trimming compatibility — verify, don't trust). `vpk` CLI packs the
existing publish output; the app checks GitHub Releases for updates.

**Prerequisite:** ~~repo does not exist yet~~ resolved before this phase — the
repo is live and public at github.com/PneutronixJeremy/MarkLite, so tokenless
`GithubSource` update checks work.

- [x] **Logo** (user request 2026-08-23): design a cool, recognizable MarkLite mark — SVG master in `assets/` (works at 16 px and 256 px, legible in dark and light contexts; propose 2–3 candidates, user picks). Derive multi-res `MarkLite.ico` (16/24/32/48/64/128/256). _(three rounds: user first picked the "plan signature" checkbox mark (committed with the phase), then chose a new direction the next day — a white M beside a wide blue feather hanging tip-down like a down arrow (Markdown's M↓ lineage, arrow made of "lite"). 16/24/32 ico slots carry a simplified master `MarkLite-small.svg` (M + solid arrow) because the feather vane blurs below 48 px; ico assembled from both SVGs with PNG-compressed slots)_
- [x] Wire the icon everywhere: csproj `ApplicationIcon` (exe icon), `Window.Icon` (title bar/taskbar), `vpk pack --icon` (installer + shortcuts), README header image. Verify each spot after publish. _(exe icon verified by extraction; window icon via AvaloniaResource link + avares URI, app runs; pack.ps1 passes --icon; README header `<img>` added)_
- [x] Add `Velopack` NuGet package; call `VelopackApp.Build().Run()` first thing in `Main` (before Avalonia init — it handles install/uninstall/update hooks and shortcut creation, and exits early on those invocations). _(Velopack 1.2.0; plus `OnBeforeUninstallFastCallback` → association cleanup)_
- [x] Verify AOT publish still exits 0 with Velopack integrated and the published exe still renders (this is the phase's decision gate — if Velopack breaks AOT, stop and report, per project constraints). _(exit 0, zero trim warnings, renders — Velopack is AOT-clean)_
- [x] Confirm startup cost of `VelopackApp.Run()` is negligible (re-measure first-content-render; must stay well under the 500 ms target). _(56 ms first render vs ~55 before — noise)_
- [x] Version flow: single source of truth in csproj (`Version`); `build/pack.ps1` reads it and runs `vpk pack --packId MarkLite --packVersion <ver> --packDir publish --mainExe MarkLite.exe` (install `vpk` as dotnet global tool). _(vpk 1.2.0 installed; pack.ps1 also sets --shortcuts StartMenuRoot — vpk's default adds a Desktop shortcut too)_
- [x] Update check: `UpdateManager` + `Velopack.Sources.GithubSource` — background check shortly after startup + Help > Check for updates menu item; unobtrusive banner (reuse stale-banner pattern) offering "Restart to update"; silent download, apply on restart via `WaitExitThenApplyUpdates`. Never block rendering on the check; offline = silent no-op. _(`UpdateService.cs`; banner generalized into NoticeBanner with one configurable action button; `MARKLITE_UPDATE_URL` env var overrides the source for local testing)_
- [x] Installer behavior audit: per-user install (no admin), Start-menu shortcut, uninstaller registered; MUST NOT touch `.md` file associations — the user's existing double-click default stays. _(all verified by the scripted e2e test; desktop shortcut disabled via `--shortcuts StartMenuRoot`)_
- [x] Before any association write: export the current `.md`/`.markdown` HKCU keys (guardrail inherited from the dropped Phase 9), and never write `UserChoice`. _(deviation: backups go to `%APPDATA%\MarkLite\assoc-backup-<stamp>-<ext>.reg` via reg.exe, not `build/` — the in-app path runs from the installed exe where no repo exists, and `%LOCALAPPDATA%\MarkLite` is the Velopack install dir that uninstall wipes)_
- [x] `MARKLITE_DEBUG=1` logs: update check start/result, version found, download/apply steps.
- [x] Local end-to-end test WITHOUT GitHub: pack v1.0.0 and v1.0.1, install v1.0.0 from local Releases dir, point update source at the local dir (Velopack supports local/file sources for testing), verify update to v1.0.1 applies on restart. _(scripted `test-velopack.ps1`, 16/16 PASS — see results)_
- [x] In-app association option (user request 2026-08-23): menu item (e.g. under a new Options/Settings menu) "Register 'Open with' for .md/.markdown" — performs an HKCU-only ProgID (`MarkLite.md`: friendly name, icon, open command) plus `HKCU:\Software\Classes\.md\OpenWithProgids` and the same for `.markdown`, in-process (no script), pointing at the INSTALLED exe path; shows checked state when registered; unregisters on toggle-off. Guardrail applies: this never flips the default handler. _(`FileAssociation.cs`; Options menu CheckBox item; exe path = `Environment.ProcessPath`. PLUS a user-requested one-time first-run offer: installed copies show a dismissable NoticeBanner offering registration once, flag in `HKCU\Software\MarkLite`)_
- [x] Companion menu item "Make MarkLite the default…": Windows protects `UserChoice` with a hash, so programmatic default-flipping is off the table — the item opens the Windows default-apps settings page (`ms-settings:defaultapps`) and shows a short hint instead. Only added alongside the register option; still requires the user's explicit action in Windows UI.
- [x] Velopack uninstall hook removes the registration (no orphaned registry keys). _(verified in the e2e test — ProgID, OpenWithProgids values and the `HKCU\Software\MarkLite` state key all gone after uninstall)_
- [x] GitHub release flow: `build/release.ps1` using `vpk upload github` publishing Setup.exe + nupkg + RELEASES; document manual steps in README. _(gh CLI not installed on this machine — script takes GITHUB_TOKEN env var or -Token; -Draft supported)_
- [ ] Real-world verify: install from the GitHub release on this machine, bump patch version, publish again, confirm the installed copy self-updates. _(deferred — user chose to run release.ps1 themselves later; `releases/` already holds the icon-equipped v1.0.0 artifacts ready to upload)_

### Verification Plan
- AOT publish exit 0 with Velopack referenced; WebView2 attribution still empty.
- `vpk pack` exit 0; Setup.exe installs into `%LOCALAPPDATA%` without admin; installed exe launches and renders sample.md.
- Local-source update test: debug log shows check → found v1.0.1 → downloaded → applied after restart; `vpk`'s Releases dir diffed before/after.
- First-content-render timing re-measured, still ~50 ms class.
- `.md` UserChoice/default handler unchanged after install AND uninstall (reg query before/after).
- Uninstall leaves no stray files/registry (spot check).

**Verification results (2026-08-23/24, scripted `test-velopack.ps1` — 16/16 PASS):**
- Build 0 warnings; AOT publish exit 0, zero trim warnings, published exe
  renders sample.md (first content render 56 ms; window open 52/38 ms on the
  installed copies).
- Silent install of packed v1.0.0 into `%LOCALAPPDATA%\MarkLite`: no admin
  prompt, Start-menu shortcut created, uninstaller registered under HKCU.
- Installed copy with `MARKLITE_UPDATE_URL` at the local feed logged the whole
  chain: `update check started (current 1.0.0)` → `update found: 1.0.1;
  downloading` → `update downloaded` → (graceful close) → `update will apply
  on exit` → next launch `update check started (current 1.0.1)` → `up to
  date`. Delta package was built and used (1 file patched, 5 unchanged).
- Uninstall: install dir + shortcuts + uninstall key removed; the
  `--veloapp-uninstall` hook ran MarkLite's cleanup — ProgID, OpenWithProgids
  values, and the `HKCU\Software\MarkLite` state key all verified gone.
- `.md` UserChoice ProgId identical before/after the full cycle (never
  touched).
- WebView2 attribution: no MarkLite group (MarkdownMonster/olk/SearchHost as
  usual).
- First-run offer banner confirmed live on the installed copy (`update banner
  deferred: notice banner already in use` in the log — the offer occupied it).

### Phase Summary
Packaging and distribution are done end to end except pressing the publish
button, which the user kept for themselves (`build/release.ps1` with their
GITHUB_TOKEN; `releases/` holds the finished v1.0.0 artifacts).
- **Velopack 1.2.0 is AOT-clean**: zero trim warnings, +1 ms startup. `Main`
  order is Velopack → single-instance → Avalonia; the uninstall FastCallback
  calls `FileAssociation.UninstallCleanup()`.
- **New files**: `UpdateService.cs` (source selection with `MARKLITE_UPDATE_URL`
  override, check+download, `Pending` update state, restart-now vs
  apply-on-exit), `FileAssociation.cs` (HKCU-only Open-with ProgID, reg.exe
  backups to `%APPDATA%\MarkLite`, one-time-offer flag, uninstall cleanup),
  `build/pack.ps1`, `build/release.ps1`.
- **UI**: Options menu (Register Open-with toggle, Make-default → ms-settings),
  Help > Check for updates, and the StaleBanner pattern generalized into
  `NoticeBanner` (text + one configurable action button + dismiss) shared by
  updates, the first-run association offer, and hints.
- **Assembly-wide `[SupportedOSPlatform("windows")]`** in Program.cs — the
  registry/Velopack calls tripped CA1416 under the zero-warnings gate; the app
  is Windows-only by design, so declare it instead of suppressing per call.
- **Icon pipeline**: `assets/MarkLite.svg` (full mark) + `assets/MarkLite-small.svg`
  (16/24/32 slots) → `assets/MarkLite.ico` with PNG-compressed slots, assembled
  by a python one-liner (cairosvg + manual ICONDIR); csproj `ApplicationIcon`,
  AvaloniaResource link + `Window.Icon` avares URI, `vpk --icon`, README header.
- **Gotcha**: `%LOCALAPPDATA%\MarkLite` is Velopack's install root and is wiped
  on uninstall — nothing else (like registry backups) may live there.
- Session extras folded in: `.claude/settings.local.json` with
  `respectGitignore: false` (user request — @-mention now lists `plans/`), and
  the plan file itself scrubbed + un-gitignored for committing (user decision;
  AGENTS.md updated with the standing scrub rule).

## Future work (out of scope, do not start)
- **One live viewer per window** (user question 2026-08-23, measured): tabs now
  keep every viewer attached, which costs memory. A/B on identical JIT builds,
  3 heavy tabs (sample.md + this plan file + the 77 KB awesome README), same
  fixtures and same switch sequence:

  | Hosting | 1 tab | 3 tabs | after 2× cycling | after idle trim |
  |---|---:|---:|---:|---:|
  | ContentControl swap (pre-10D) | 120 MB | 204 MB | 265 MB | 267 MB |
  | Panel + IsVisible (current) | 124 MB | 234 MB | 293 MB | 293 MB |

  ≈25–30 MB for 3 tabs (partly confounded: the pre-10D build crashed on some
  switches and so did less layout). The alternative is to keep only the ACTIVE
  tab rendered and re-render on return — memory then tracks the active document
  instead of the sum of tabs, and each viewer detaches exactly once, which also
  side-steps the Mermaid detach crash without relying on staying attached. Cost:
  50–180 ms per switch on large documents, plus re-applying scroll and search
  (the render path already does both). Pairs naturally with virtualization.
- Mermaid is NOT the memory driver (checked 2026-08-23): documents without a
  diagram (this plan file, awesome README) sit at the same 111 MB, and
  sample.md costs ~4 MB more than sample-plan.md for its one diagram. Gating
  `UseMermaid()` on the document text would save nearly nothing — under AOT the
  renderer is compiled in regardless and a fence-less document never invokes
  it. The only real mermaid saving is exe size (~10 MB), i.e. dropping the
  package.
- Virtualized rendering (user RAM discussion 2026-08-23): the engine realizes
  the whole document as controls up front — per-document cost ≈ 0.5–1 MB per KB
  of markdown, and scroll fills glyph/layout caches (observed 99 → 158 MB on
  the 40 KB plan file after heavy use; idle trim added to claw back the
  growth). Real fix: host block-level DocumentElements in a virtualizing list
  and realize controls near the viewport only. Phase-sized; reworks scroll
  preservation, TOC position math, and search highlighting.
- Single physical exe (user question 2026-08-23): NativeAOT output needs
  libSkiaSharp/libHarfBuzzSharp/av_libglesv2 dlls beside the exe (no
  self-extract on AOT). Options to investigate: static-link Skia/HarfBuzz into
  the AOT binary (unsupported, custom .lib builds), or accept trimmed JIT +
  `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract` (single exe but
  loses AOT: slower cold start, bigger). Current deployment: exe + 3 dlls.
- Math rendering (KaTeX-equivalent): investigate native options — e.g. CSharpMath (SkiaSharp path) or WPF-Math descendants. No browser, no JS.
- ~~Mermaid stays skipped permanently unless a native .NET renderer appears.~~
  **One appeared** (user links, 2026-08-23): **Mermaider** — pure .NET mermaid
  parser + layout + renderer, AOT-ready, no JS. Used by two Avalonia-12
  Markdig-based viewer libraries: [LiveMarkdown.Avalonia](https://github.com/DearVa/LiveMarkdown.Avalonia)
  (Apache-2.0, streaming-optimized, TextMateSharp + optional CSharpMath) and
  [MarkView.Avalonia](https://github.com/Kryptos-FR/MarkView.Avalonia)
  (MIT, task lists + theme-aware mermaid, early-stage ~21 stars). Both require
  **Avalonia 12** — adopting either means migrating off Avalonia 11 +
  Markdown.Avalonia.Tight. Post-MVP spike options: (a) try Mermaider directly
  inside the existing CodeBlockOverride on Avalonia 11 (if its output is
  UI-agnostic/SkiaSharp), or (b) evaluate an Avalonia 12 + MarkView/LiveMarkdown
  stack migration — would also replace the homegrown task-list pipeline and
  give Markdig (fixing Phase 6's AST assumption natively).

## Final Recap
_(write when all phases complete: summary of the entire piece of work)_

## Deployment Plan
_(write when all phases complete: step-by-step deployment instructions)_
