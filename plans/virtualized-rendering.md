# Virtualized rendering + active-document-only tabs

Make MarkLite's working set independent of document size and of the number of
open tabs: keep only the active tab rendered, then replace the all-at-once
control tree with a virtualizing host that realizes Markdown blocks near the
viewport only. Ships as v1.1.0. Picks up the two "Future work" items recorded
by the original build plan (one live viewer per window; virtualized rendering).

Decisions fixed with the user on 2026-08-24 (do not re-open):
- Scope: both items, active-only rendering first, then virtualization.
- Memory target: **< 100 MB working set worst case**, ~flat versus document
  size (< 0.2 MB per KB of markdown after virtualization); 3 heavy tabs
  < 120 MB. Measured on the published AOT exe.
- Scrollbar: estimated extent for unmeasured blocks, corrected as blocks
  realize; a per-block **height cache keyed by block source hash** keeps the
  thumb stable across live reloads of a lightly edited document.
- Selection: model-based anchors (block index + character offset), highlight
  only on realized blocks, drag autoscrolls and realizes as it goes.
  **Copy produces the markdown source slice** between the anchors
  (Ctrl+A = whole file). No plain-text copy path.
- Approach: **own host control over MarkView's public API** (Markdig parse →
  top-level blocks → `AvaloniaRenderer.Write(block)` per realized block).
  MarkView stays a NuGet dependency; nothing vendored except the Mermaid block
  renderer (below). Reasons: `MarkdownViewer` exposes no AST, root panel, or
  block→control mapping, and renders synchronously as one full tree;
  `AvaloniaRenderer` and every block renderer are public, so per-block
  realization needs no library change.
- **Vendor a fixed `WriteMermaid`** (~90 lines, MIT, copyright notice kept in
  `THIRD-PARTY-NOTICES.md`): the package version registers a new
  `DetachedFromLogicalTree` handler on every attach and calls `Cancel()` on a
  disposed `CancellationTokenSource` (crash on the second detach — certain
  under realize/recycle), and leaks an `Application.PropertyChanged`
  subscription when the image never attaches under a `ScrollViewer`.
- Fixture: generated synthetic **~500 KB** `testdata/stress-large.md`
  (fictional content, deterministic script). Third-party READMEs stay local
  comparison material, never committed.
- Verification scripts live in **`tools/verify/`** and are committed
  (scrub-clean, input-free: debug log assertions, a debug command channel,
  UIA InvokePattern, PrintWindow).
- Plan files: **every plan under `plans/` is committed** from now on and must
  pass a scrub check (`plans/reference/` stays untracked). Enforcement is a
  committed git **pre-commit hook** (`.githooks/pre-commit`, activated once
  per clone with `git config core.hooksPath .githooks`) that scans the
  staged content — not the agent's memory; the agent additionally runs the
  check before suggesting a commit for fast feedback. The completed original
  plan is deleted once its release procedure and remaining future work are
  moved out (user approval 2026-08-24).
- Final phase: **release v1.1.0** (user runs `git push` and `release.ps1`;
  the agent never pushes or tags).

Pipeline decision (user, 2026-08-24, after Phase 3 measured both options):
- **Footnotes ON**, added in Phase 4. MarkView's `UseSupportedExtensions` never
  included them, so `[^n]` has always rendered as literal text and definitions
  as ordinary paragraphs. Measured cost of enabling: `stress-large.md` goes
  2073 → 2074 blocks and 308 → 311 anchors; the other three fixtures do not
  move at all.
- **Generic `{#id}` attributes NOT enabled** — do not re-open. The extension
  claims any trailing `{...}`, not just ids: `# Config for {env}` silently
  becomes "Config for" and its slug changes from `config-for-env` to
  `config-for`, breaking existing links and losing text. MarkView's
  `HeadingRenderer` ignores `HtmlAttributes.Id` anyway, so the classic viewer
  could not honour such an anchor even once parsed. The model's `{#id}` support
  stays (it is correct and tested); the pipeline simply never feeds it.

Two features requested mid-plan (user decisions 2026-08-24), added as Phase 1A
and Phase 4A rather than reopening the phases above:
- **HTML comments visible** (Phase 1A, next): the user objected to markers that
  a viewer hides. Scope is **comments only** — `<!-- … -->` blocks and inlines
  render as dimmed monospace showing the full markup; other raw HTML stays
  dropped, so a README's `<img>` tag does not turn into visible text. A View
  menu toggle, persisted, **default on**.
- **Line-number gutter** (Phase 4A, after the virtual host and parity exist):
  **per-block source line numbers, plus true line-by-line numbering inside
  fenced code**. Rendered Markdown reflows, so a per-row or per-source-line
  gutter would either lie or force one `TextBlock` per source line; block start
  lines come free from `Markdig.Block.Span` on the Phase 3 model, and code
  fences are the one place the mapping is 1:1. Placed after Phase 4 so the
  scroll-anchor and live-reload rework does not have to be redone in the
  gutter.

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

Repo-specific rules that apply to every phase:
- This file is committed. The pre-commit hook (Phase 0) blocks any commit
  whose staged content fails `tools/scrub-check.ps1`; run it yourself
  before suggesting a commit and fix every hit. No absolute
  local paths, no personal details, no credentials, no screenshots outside
  `testdata/` fixtures. Assume the repo is public.
- Numbers quoted as verification results come from the **published AOT exe**
  (`build/publish.ps1`), never from a Debug/JIT run, unless the item says
  otherwise. Working set = `Process.WorkingSet64` after the idle trim unless
  stated.
- No input injection (SendKeys, mouse_event, focus stealing) in scripts. Use
  the debug command channel (Phase 1), UIA InvokePattern, and PrintWindow.
  If a check genuinely needs injected input, warn the user and wait for an
  explicit go.
- Commit subjects: `Area > Subarea > Description. [w/ Claude]` (no
  `MarkLite >` prefix). Never `git push`, never tags, never `--amend`.

Code map (as of the start of this plan, for orientation):
- `src/MarkLite/MainWindow.axaml.cs` — tab orchestration (`CreateTab`,
  `ActivateTab`, `RenderTab`, `CloseTab`), TOC (`RebuildTocData`,
  `ScrollToHeading`, `UpdateCurrentSection`), find bar, idle trim.
- `src/MarkLite/DocumentTab.cs` — per-tab state bag (viewer, watcher,
  search, `PendingText`, `SavedScrollY`, heading controls).
- `src/MarkLite/DocumentSearch.cs` — highlight by splitting `Run`s in
  realized `TextBlock`s; walks `GetVisualDescendants()`.
- `src/MarkLite/Rendering/MarkLiteRenderExtension.cs` — `IMarkViewExtension`
  registering the prominent task-list renderer, silent inline task renderer,
  and `MarkLiteCodeBlockRenderer` (forwards mermaid fences to the package).
- `src/MarkLite/Rendering/MarkdownTheme.axaml` — app styles, all scoped
  `mv|MarkdownViewer …`.
- `src/MarkLite/SingleInstance.cs` — named pipe `MarkLite-<user>`, one UTF-8
  path per connection, primary side `StartServer(Action<string>)`.
- `src/MarkLite/DebugLog.cs` — `MARKLITE_DEBUG=1` → `[marklite] …` on stderr.
- MarkView 12.2.1 facts that shape the design (verified in source):
  `MarkdownViewer : ContentControl`, template `Border > ScrollViewer
  PART_ScrollViewer > ContentPresenter`, selector `:is(mv|MarkdownViewer)` so
  subclasses inherit template and theme; `RenderMarkdown` is synchronous,
  full rebuild, resets scroll to 0; root is a hard-coded `StackPanel`
  exposed as `AvaloniaRenderer.RootPanel`; one top-level block → 0, 1 or 2
  controls appended to `RootPanel.Children` (HTML/YAML → 0, footnote group →
  2); `HeadingRenderer` sets `Tag = slug` and classes `markdown-heading
  markdown-h{n}`; `TocEntry.BuildTree(headingEntries, depth)` and
  `SlugGenerator` are public; `Markdig.Block.Span` gives source spans;
  Markdig caches renderer lookup per type on first `Write`, so all
  `ObjectRenderers` edits must precede the first `Write`; the selection
  layer, hyperlink hit-test, and `OnLinkClicked` are internal — replaced by
  MarkLite code in Phase 6.

## Phase 0: Repo housekeeping, fixture, scrub check
Status: Complete

Plans policy, tooling skeleton, and the large fixture every later phase
measures against. No app code changes.

- [x] `.gitignore`: replace `plans/*` + the single exception with
  `plans/reference/` only, so every `plans/*.md` is tracked.
- [x] `tools/scrub-check.ps1`: scans tracked text files under `plans/`,
  `docs/`, `tools/`, `README.md`, `AGENTS.md`, `THIRD-PARTY-NOTICES.md` for
  drive-letter absolute paths (`[A-Za-z]:\`), `\Users\`, `/Users/`, `/home/`, <!-- scrub-check:allow -->
  email addresses other than the GitHub noreply form, token shapes (`ghp_`,
  `github_pat_`, `AKIA`), the current machine name and user name <!-- scrub-check:allow -->
  (`$env:COMPUTERNAME`, `$env:USERNAME`, case-insensitive). Prints
  `file:line: match`, exits 1 on any hit, 0 otherwise. `build/` is excluded
  (build scripts may need functional paths). Two modes: default scans the
  working tree; `-Staged` scans `git diff --cached` content of the matching
  paths (what the commit would actually contain).
- [x] `.githooks/pre-commit` (`#!/bin/sh`, LF endings, `.gitattributes`
  entry `.githooks/* text eol=lf`): runs `pwsh -NoProfile -File
  tools/scrub-check.ps1 -Staged`, exits non-zero on hits with the offending
  lines printed. Activate in this clone: `git config core.hooksPath
  .githooks`. Git cannot auto-install hooks, so the one-time command is
  documented in AGENTS.md and README "Build".
- [x] `AGENTS.md`: rewrite the plans section — all `plans/*.md` committed and
  scrub-clean, `plans/reference/` untracked, pre-commit hook enforces
  `tools/scrub-check.ps1` (activation command; agent also runs it before
  suggesting a commit); note `tools/verify/` as the home of verification
  scripts and the no-input-injection rule.
- [x] `docs/RELEASING.md`: move the release procedure and machine
  prerequisites out of the original plan's Deployment Plan (version bump in
  `src/MarkLite/MarkLite.csproj`, `build/pack.ps1`, `build/release.ps1`,
  token env var name, PowerShell 7, `vpk`, VS-less linker note, self-update
  behavior). README "Packaging a release" links to it.
- [x] `plans/TODO.md`: future work not covered by this plan, carried over
  from the original plan: single physical exe (static Skia/HarfBuzz vs
  trimmed JIT single-file), upstream bug report for the Mermaid CTS crash,
  math/mermaid notes that are now historical get dropped. Keep it short.
- [x] `git rm plans/marklite-native-markdown-viewer.md` (approved
  2026-08-24) — after the two moves above, in the same phase.
- [x] `tools/gen-stress-fixture.ps1` → `testdata/stress-large.md`:
  deterministic (fixed seed, no timestamps), fictional content (an operations
  manual for an invented orbital station), ~500 KB, roughly: 300 headings
  (H1–H4, some setext), 1500 paragraphs with bold/italic/inline code/links
  (including `#anchor` links to earlier headings), 150 bullet and ordered
  lists (nested, ~40 task lists), 40 pipe tables, 60 fenced code blocks
  (csharp, json, powershell, plain), 10 blockquotes, 2 mermaid fences, 1 math
  block + a few inline math, 3 footnotes, 5 thematic breaks. First line is an
  HTML comment naming the generator script. Report block count and byte size
  on stdout.
- [x] `tools/verify/README.md`: one paragraph per script (what it asserts,
  how to run), kept current by later phases.

### Verification Plan
- `git check-ignore -v plans/virtualized-rendering.md` → not ignored;
  `git check-ignore -v plans/reference/x.png` → ignored by `plans/reference/`.
- `pwsh tools/scrub-check.ps1` → exit 0 on the current tree; then a temp copy
  of a plan file containing `C:\Users\someone\x` → exit 1 with the line <!-- scrub-check:allow -->
  printed (delete the temp file afterwards).
- Hook: `git config core.hooksPath` prints `.githooks`; stage a plan file
  with a fake `C:\Users\someone\x` line and run `git commit --dry-run` is <!-- scrub-check:allow -->
  not enough (dry-run skips hooks) — instead stage it and run
  `.githooks/pre-commit` directly via `sh` → exit 1 with the line; unstage
  and revert. Then confirm a clean staged set → exit 0.
- `pwsh tools/gen-stress-fixture.ps1` twice → identical SHA-256 both runs;
  size 450–600 KB; `Select-String -Pattern '^#{1,4} ' testdata/stress-large.md
  | Measure-Object` ≈ 300.
- `git status` shows: modified `.gitignore`, `AGENTS.md`, `README.md`;
  deleted original plan; new `docs/RELEASING.md`, `plans/TODO.md`,
  `plans/virtualized-rendering.md`, `tools/…`, `testdata/stress-large.md`.

### Phase Summary
Done, no app code touched.

- `.gitignore` now ignores only `plans/reference/`; every `plans/*.md` is
  tracked. The completed original plan
  (`plans/marklite-native-markdown-viewer.md`) was `git rm`'d after its release
  procedure moved to `docs/RELEASING.md` and its live future work to
  `plans/TODO.md` (single physical exe; upstream bug report for the Mermaid
  CTS crash — the math/mermaid entries were historical and dropped).
- `tools/scrub-check.ps1`: scans tracked **and not-yet-added** text files under
  `plans/`, `docs/`, `tools/` plus `README.md`, `AGENTS.md`, `CLAUDE.md`,
  `THIRD-PARTY-NOTICES.md`. `build/` excluded. `-Staged` scans `git show :path`
  content instead of the working tree. Three implementation decisions worth
  knowing:
  - Patterns are **assembled from fragments** (`'/Us' + 'ers/'`) so the script
    does not match itself when it scans `tools/`.
  - Machine/user names (`COMPUTERNAME`, `USERNAME`, `USERDOMAIN`) are matched
    **bounded on both sides**, otherwise the GitHub account inside the clone
    URL and the release token's env-var name are permanent false positives.
  - A `scrub-check:allow` line marker (and an `allow-start`/`allow-end` region
    form) skips lines, for prose that must spell the patterns out. Four lines
    of this plan carry it as an invisible HTML comment: the two that list the
    patterns, and the two verification steps quoting a fake profile path.
- `.githooks/pre-commit` (LF, pinned by a new `.gitattributes` entry) runs the
  `-Staged` check. Activated in this clone: `git config core.hooksPath
  .githooks` — verified it prints `.githooks`. Note the hook's index mode is
  `100644`; Windows git runs it through `sh` regardless, but a Linux clone
  would need `git update-index --chmod=+x .githooks/pre-commit`.
- `AGENTS.md` rewritten (plans policy, scrub check + hook activation, the
  suppression marker rule, `tools/verify/` and the no-input-injection rule,
  pointer to `docs/RELEASING.md`). README gained the hook-activation snippet
  under Build and a RELEASING link under "Packaging a release".
- `testdata/stress-large.md` generated by `tools/gen-stress-fixture.ps1`:
  **540,951 bytes**, 5,868 lines, 2,078 top-level blocks, **300 ATX headings**
  (+8 setext), 1500 paragraphs, 150 lists (40 task lists), 40 tables, 60 code
  fences, 10 blockquotes, 2 mermaid, 1 math block + inline math, 3 footnotes,
  5 thematic breaks, 241 in-document `#anchor` links. Fictional content
  (Kestrel Station, an invented orbital platform). Seed 20260824, CRLF output,
  so a regenerated file is byte-identical and `git status` stays clean.
  **65 headings deliberately share a slug** with an earlier one — that is the
  material for Phase 3's `-1`/`-2` slug-dedup test; the generator only links to
  first occurrences, since it does not reproduce the renderer's numbering.
- `tools/verify/README.md` written with the ground rules and fixture list; the
  script list is empty until Phase 1.

### Verification Plan results
- `git check-ignore -v plans/virtualized-rendering.md` → exit 1 (not ignored);
  `plans/reference/x.png` → ignored by `.gitignore:11`. **PASS**
- `pwsh tools/scrub-check.ps1` → `clean (10 file(s) scanned)`, exit 0. Temp
  `plans/scrub-negative-temp.md` with a fake profile path → 2 hits, exit 1,
  lines printed; temp file deleted. **PASS**
- `git config core.hooksPath` → `.githooks`. Staged the bad temp file and ran
  `sh .githooks/pre-commit` → exit 1 with both hits. Unstaged, deleted, staged
  the real change set → `clean (8 file(s) scanned)`, exit 0. Index unstaged
  again afterwards (the agent does not stage for the user). **PASS**
- `gen-stress-fixture.ps1` run twice → SHA-256
  `26d565c2615f6455cdaf3c28f4b7a8bb35755f4694ff4795ae57490cd84718be` both
  times; 540,951 bytes (within 450–600 KB); `^#{1,4} ` count = 300; all 241
  anchors resolve to a heading that appears above them. **PASS**
- `git status`: modified `.gitignore`, `AGENTS.md`, `README.md`; deleted
  `plans/marklite-native-markdown-viewer.md`; new `.gitattributes`,
  `.githooks/pre-commit`, `docs/RELEASING.md`, `plans/TODO.md`,
  `plans/virtualized-rendering.md`, `testdata/stress-large.md`,
  `tools/scrub-check.ps1`, `tools/gen-stress-fixture.ps1`,
  `tools/verify/README.md`. **PASS**

## Phase 1: Test harness — debug command channel + verify scripts + baseline
Status: Complete

Input-free automation the remaining phases assert against, plus baseline
numbers on the current renderer so the improvement is measurable.

- [x] Debug command channel, active only when `MARKLITE_DEBUG=1`: the
  single-instance pipe accepts messages prefixed `cmd:` (plain paths keep
  working). Commands, each acknowledged by a `[marklite] cmd …` log line:
  `scroll <y>` (absolute offset), `scroll-end`, `scroll-page <n>` (n pages,
  negative = up), `tab <index>`, `close-tab`, `find <term>`, `find-next`,
  `find-close`, `toc <index>` (same path as clicking the sidebar entry),
  `anchor <slug>`, `select-all`, `copy` (copies via the normal path; the
  script reads the clipboard), `gc` (forces the idle trim now),
  `dump-state` — writes one JSON line: tabs (path, active, scrollY, extent,
  viewport), toc count, current toc index, match count/current, working set
  and private bytes, and (from Phase 3) realized/total block counts.
- [x] `SingleInstance.SendToPrimary` gains an internal overload used by a new
  `--cmd <text>` CLI switch, so scripts drive the app with
  `MarkLite.exe --cmd "scroll-end"` (the process exits after sending; without
  `MARKLITE_DEBUG=1` on the primary the command is ignored and logged).
- [x] `tools/verify/common.ps1`: `SetProcessDpiAwarenessContext(-4)` before
  any capture; `Start-MarkLite` (published exe, `MARKLITE_DEBUG=1`, stderr to
  a log file, waits for `first content render`); `Send-Cmd`; `Wait-Log
  -Pattern -TimeoutSec`; `Get-State` (sends `dump-state`, parses the JSON
  line); `Capture-Window` (PrintWindow → PNG); `Stop-MarkLite` (graceful
  close via UIA WindowPattern, kill as fallback). Never sends keyboard or
  mouse input.
- [x] `tools/verify/measure-memory.ps1 -Files <paths…>`: opens the files as
  tabs, records working set after first render, after `scroll-end` +
  `scroll 0` on each tab, after cycling tabs twice, and after `gc`; prints a
  markdown table. Used unchanged by every later phase.
- [x] `tools/verify/test-tabs.ps1`: opens 3 fixtures via secondary launches,
  asserts `handed off`, tab count, independent scroll (scroll tab 2, switch
  away and back, offset restored ±1 px), close-all → welcome state.
- [x] `tools/verify/test-toc-search.ps1`: `toc <n>` changes scroll and
  current-section index; `find` match count equals `Select-String
  -AllMatches` count over the fixture's rendered-text approximation
  (headings + paragraphs + code; tolerance documented); `find-next` advances.
- [x] Baseline run of all scripts against the published v1.0.1 code on
  `testdata/sample.md`, `testdata/sample-plan.md`, `testdata/stress-large.md`
  (the stress fixture is expected to be very slow and very large on the
  current renderer — record whatever it does, including timeouts).

### Verification Plan
- `build/publish.ps1` exit 0, 0 warnings from MarkLite code.
- `pwsh tools/verify/test-tabs.ps1` and `test-toc-search.ps1` → all PASS on
  the current renderer (harness proven before the renderer changes).
- `pwsh tools/verify/measure-memory.ps1 -Files testdata/sample.md
  testdata/sample-plan.md` → table printed; sample.md single tab within
  ±5 MB of the recorded v1.0.1 number (~65 MB).
- Baseline table for the three fixtures recorded in this phase's summary
  (first-render ms, working set single tab, after scroll-through, 3 tabs).
- Launch without `MARKLITE_DEBUG` and send `--cmd scroll-end` → no effect,
  no crash.

### Phase Summary
The harness exists and passes against the CURRENT renderer, so later phases
change one thing at a time.

- **Debug command channel** (`src/MarkLite/DebugCommands.cs`, a partial of
  `MainWindow`): live only when `MARKLITE_DEBUG=1`. Commands `scroll <y>`,
  `scroll-end`, `scroll-page <n>`, `tab <i>`, `close-tab`, `find <term>`,
  `find-next`, `find-prev`, `find-close`, `toc <i>`, `anchor <slug>`,
  `select-all`, `copy`, `gc`, `dump-state`. Each answers with
  `[marklite] cmd <text> -> <result>`, so scripts wait on an acknowledgement
  instead of sleeping. `dump-state` writes `[marklite] state {json}`: per-tab
  path/name/active/chars/scrollY/extent/viewport/stale, plus activeTab,
  tocCount, tocIndex, findVisible, matches, matchIndex, workingSetMb,
  privateMb, managedMb. JSON is hand-built (no reflection, trimmer-safe).
- **Transport**: `SingleInstance` gained a `cmd:`-prefixed message form, a
  `SendToPrimary(DebugCommand)` overload and an `onCommand` callback on
  `StartServer`; `Program` handles `--cmd <text>` (sends, exits, exit code 1
  when no primary answers). `select-all`/`copy` map to `MarkdownViewer`'s
  public `SelectAll()` / `CopyToClipboardAsync()` — both exist in 12.2.1.
- **`MARKLITE_INSTANCE` env var** (new, not in the original plan, and load
  bearing): it suffixes the pipe name, so a verification run forms its own
  single-instance group. Without it the first scripted launch handed its test
  file to the MarkLite the user already had open, and commands went to that
  window. `common.ps1` always sets `MARKLITE_INSTANCE=verify`.
- **Search fix found by the harness**: `FindMove` flushed a pending debounce by
  re-running the search even when the term had not changed, which reset the
  current match to the first — F3 immediately after typing lost its place. It
  now drops the pending tick and steps normally when the term is unchanged.
- **`tools/verify/`**: `common.ps1` (launch, `Send-Cmd`, `Wait-Log`,
  `Get-State`, `Save-WindowCapture` via PrintWindow, `Stop-MarkLite` via
  WM_CLOSE with a kill fallback, `Assert-*`), `measure-memory.ps1`,
  `test-tabs.ps1`, `test-toc-search.ps1`, README updated. Two harness
  decisions: the window is moved to the **non-primary display** at a fixed
  **1400x1000** with `SWP_NOACTIVATE` (user request — runs must not land on
  the display they are working on; the fixed size also keeps the software
  framebuffer, and therefore the memory numbers, comparable), and
  `Stop-MarkLite` uses `PostMessage(WM_CLOSE)` rather than UIA WindowPattern —
  same effect, one less dependency, still no input injection.
- **Gotchas worth keeping**: `pwsh -File script.ps1 -Files a b` binds only
  `a`, so list parameters also accept a comma-separated string; a PowerShell
  function returning `@()` unrolls to `$null`, so log reads are wrapped as
  `@(Get-LogLines)`.
- **Scrub-check hardening** (prompted by the user asking whether the checker
  itself is clean): an independent literal-pattern audit found no sensitive
  content in `tools/scrub-check.ps1`, but showed that a suppression marker
  merely *mentioned* in prose took effect — AGENTS.md's own rule text silently
  exempted lines 24-65 of that file. Markers now count only when they close
  their line (bare or before `-->`, `*/`, `#>`), the marker spellings are
  assembled at runtime so the checker's own source is not exempt, and every
  skipped line is listed in the output. Skipped lines are now exactly the four
  intended ones in this plan.

### Baseline: published v1.0.1 renderer

Published AOT exe, `MARKLITE_DEBUG=1`, fixed 1400x1000 window,
`Process.WorkingSet64` sampled by `dump-state` (the `after gc` row is the
forced aggressive collect, i.e. what the idle trim does).

| Fixture(s) | First render | 1 tab | all tabs open | after scroll-through | after cycling 2x | after gc |
|---|---:|---:|---:|---:|---:|---:|
| sample.md (1.6 KB) | 76 ms | 73.2 | 73.2 | 76.3 | 76.3 | 75.9 |
| sample.md + sample-plan.md (3.6 KB) | 97 ms | 72.6 | 76.7 | 81.1 | 83.9 | 80.1 |
| **stress-large.md (528 KB)** | **784 ms** | **304.0** | 304.0 | 304.1 | 304.2 | **303.0** |
| stress + sample-plan + sample | 759 ms | 467.8 | 302.7 | 321.9 | 362.3 | 301.3 |

Reading: the 528 KB fixture costs ~230 MB of managed heap on top of the ~73 MB
floor — about 0.44 MB per KB of markdown — and scrolling adds nothing because
everything is already realized. The 467.8 MB in the last row is the peak during
the first render, before the post-render collect. Search on that fixture is
usable but slow (whole-document `Run` splitting per query).

### Verification Plan results
- `build/publish.ps1` exit 0, **0 warnings**. **PASS**
- `test-tabs.ps1` → **ALL PASS (11 checks)** on the current renderer: handoff
  for both secondary launches, 3 tabs, tab 1's offset restored to 1125.3 px
  after switching away and back, welcome state after closing all. **PASS**
- `test-toc-search.ps1` on sample-plan.md → **ALL PASS (12)**; on
  stress-large.md → **ALL PASS (12)**, 308 headings, `find station` = 373 both
  sides. **PASS**
- `measure-memory.ps1` → tables above. The plan's "sample.md within +/-5 MB of
  ~65 MB" is **NOT met**: the harness reads **73.2 MB**. Cause is measurement
  method, not a regression — the README's 65-69 MB was hand-measured at the
  default window size without debug logging; this harness fixes the window at
  1400x1000, keeps `MARKLITE_DEBUG=1` on, and samples immediately rather than
  after a 30 s idle. The harness numbers above are the reference for later
  phases; the README's figures get replaced from the Phase 7 run. **RECORDED**
- Launch without `MARKLITE_DEBUG` + `--cmd scroll-end` → client exits 0 (the
  message is delivered), the app ignores it and stays alive. **PASS**

## Phase 1A: HTML comments visible (View toggle, default on)
Status: Complete

MarkView renders `HtmlBlock` and `HtmlInline` to nothing, so a comment in a
document is invisible in the viewer — including the `scrub-check:allow` markers
this plan carries. Make comments visible and controllable. Renderer-local work
that survives the virtualization rewrite (the same renderers are reused per
block in Phase 3).

- [x] `Rendering/HtmlCommentRenderer.cs`: replacement `HtmlBlock` renderer that
  emits a dimmed monospace `TextBlock` (class `markdown-html-comment`) with the
  block's source text **only when the block is a comment** (`HtmlBlockType
  .Comment`); every other HTML block keeps today's behavior — dropped, nothing
  rendered. Companion `HtmlInline` renderer for end-of-line markers, which
  Markdig parses as inline HTML inside the surrounding paragraph or list item:
  emits a dimmed monospace `Run` in place, not a separate block.
- [x] Both renderers consult a single static `ShowHtmlComments` flag (set
  before a render pass, read during it) so one toggle covers block and inline.
- [x] `MarkdownTheme.axaml`: `markdown-html-comment` styling for both theme
  variants — muted foreground (a new `MdHtmlCommentForeground` brush), the code
  font, and the same font size as body text so line height does not jump.
- [x] View menu: `Show HTML comments` checkbox, **default on**, persisted via
  `UserSettings` (same pattern as the body font), re-render on change. Log
  `html comments: shown|hidden`.
- [x] Debug command `html-comments <on|off>` for scripted checks.
- [x] `tools/verify/test-html-comments.ps1`: opens a fixture containing
  comments (this plan's markers are the natural case — use a copy under
  `testdata/`), asserts `find <marker text>` reports 0 matches with the toggle
  off and the expected count with it on, and captures both states with
  PrintWindow. Also asserts a raw `<img …>` block stays invisible in both.
- [x] `testdata/sample-html.md`: small fictional fixture with a block comment,
  an end-of-line comment inside a list item, and a raw HTML tag.

### Verification Plan
- `build/publish.ps1` exit 0, 0 warnings; `test-tabs.ps1` and
  `test-toc-search.ps1` still ALL PASS (renderer changes must not disturb TOC
  or search counts on documents without comments).
- `test-html-comments.ps1` ALL PASS: toggle off → 0 matches for the marker
  text; toggle on → 4 matches in the plan-derived fixture; `<img` never matches
  in either state.
- Setting survives a restart (write, close, relaunch, `dump-state` reflects it).
- Memory unchanged within 2 MB on `sample.md` versus the Phase 1 baseline.

### Phase Summary
Comments are content the author wrote; hiding them silently is what made this
necessary. They now render, under a toggle that defaults to on.

- `src/MarkLite/Rendering/HtmlCommentRenderer.cs`: `HtmlComments` (static
  `Visible` flag + comment tests + block source text), `HtmlCommentBlockRenderer`
  replacing MarkView's no-op `HtmlBlockRenderer`, and `HtmlCommentInlineRenderer`
  replacing `HtmlInlineRenderer`. Only comments render — `HtmlBlockType.Comment`
  for blocks, a `<!--` prefix on `HtmlInline.Tag` for inlines. Every other HTML
  block and tag keeps the old behavior of rendering nothing, so a document that
  opens with an `<img>` tag is unchanged.
- Inline comments matter as much as block ones: an end-of-line marker inside a
  list item is parsed as `HtmlInline` within the paragraph, not as a block. It
  renders as a `Run` in place, so the line keeps its flow and wrapping. Markdig
  can split one comment into several `HtmlInline` pieces; each renders
  independently and they read as the original markup side by side.
- Styling in `MarkdownTheme.axaml`: `markdown-html-comment` on both a
  `:is(TextBlock)` and a `Run` selector — code font, muted foreground, block
  form at 0.75 opacity. Theme-variant agnostic (it uses existing brushes).
- View menu `Show HTML comments` (checkbox, default on), persisted as a DWORD
  under `HKCU\Software\MarkLite`, restored before the first render. Debug
  command `html-comments <on|off>`.
- **Bug found and fixed while testing the toggle**: `Markdown` is a styled
  property, so re-assigning the SAME text raised no change and rebuilt nothing —
  a re-render with unchanged text (theme switch, body font, comment toggle) was
  silently a no-op. `RenderTab` and the welcome path now clear `Markdown` to
  null first. This also explains why the font/theme paths always looked like
  they needed a tab switch to take effect.
- **Known cosmetic detail**: Fira Code ligatures fuse the delimiters, so a
  comment reads `<!— … —>` rather than `<!-- … -->`. Two `FontFeatures` syntaxes
  (`-liga`, `liga=0`) had no effect — Avalonia 12's text path ignores the
  property here — so the dead setter was removed rather than left in place. Fix
  candidates if it ever matters: a second bundled mono face without ligatures,
  or drawing the delimiters as separate non-mono runs.
- `testdata/sample-html.md`: fictional fixture with a block comment, two
  end-of-line comments inside list items, a multi-line comment block, and a raw
  `<img>` tag.

### Verification Plan results
- `build/publish.ps1` exit 0, **0 warnings**. **PASS**
- `test-html-comments.ps1` → **ALL PASS (9 checks)**: with the toggle on,
  `verify-against` = 2 and `kestrel-doc-id` = 1; with it off, both 0; the
  `<img>` tag's `does-not-exist.svg` = 0 in both states; prose unaffected;
  the setting survives a restart (`html comments restored: hidden` on the next
  launch) and the script leaves it back on. **PASS**
- Regression: `test-tabs.ps1` ALL PASS (11), `test-toc-search.ps1` ALL PASS (12)
  on sample-plan.md and ALL PASS (12) on stress-large.md — TOC and match counts
  unmoved by the renderer change. **PASS**
- Memory on sample.md: first render **73.6 MB** vs the Phase 1 baseline
  73.2 MB (+0.4, inside the 2 MB allowance); first content render 75 ms. **PASS**
- Captures for both states written to the verify capture directory (not
  committed; `docs/` screenshots get refreshed in Phase 7).

## Phase 2: Active-document-only rendering + vendored Mermaid renderer
Status: Complete

Only the active tab holds a rendered tree; every other tab keeps text and a
scroll anchor. Memory tracks the active document instead of the sum of tabs.
Independent of the virtualization work, ships on the existing viewer.

- [x] `ActivateTab`: on deactivation save `SavedScrollY`, then set
  `Viewer.Markdown = null` (drops the tree, viewer stays attached and
  hidden). On activation always `RenderTab(tab, tab.CurrentText,
  tab.SavedScrollY)`. Remove `PendingText` and the deferred-render branches
  (`RerenderAllTabs`, `OnTabFileChanged` just update `CurrentText` for
  inactive tabs). Welcome viewer: `Markdown = null` while any tab is open,
  re-set on `ShowWelcome`.
- [x] Log `tab switched to '<name>'; render <ms> ms` (Stopwatch around the
  render + post-layout pass) for the switch-cost table.
- [x] `DocumentTab`: drop `PendingText`; keep `Search`, `TocEntries`,
  `HeadingControls` (rebuilt by the render path as today).
- [x] Vendor the Mermaid block renderer: `src/MarkLite/Rendering/
  MermaidFenceRenderer.cs` adapted from MarkView.Avalonia.Mermaid
  `MermaidBlockRenderer` (MIT; header notice + `THIRD-PARTY-NOTICES.md`
  entry with the MarkView copyright line and license text). Fixes: single
  detach handler registered once (not per attach), `cts = null` after
  dispose, theme subscription always paired with unsubscribe on detach,
  cancellation on detach. `MarkLiteCodeBlockRenderer` calls it directly for
  `mermaid` fences; `viewer.UseMermaid()` removed (renderer still uses the
  package's Mermaider/SVG types — confirm AOT publish keeps them).
- [x] Delete the "hidden-but-attached because of the Mermaid crash" comment
  and any code that existed only for it; document the new invariant on
  `ActivateTab` (one live tree; switching costs a render).
- [x] Update `tools/verify/test-tabs.ps1` for the new log lines; add a
  mermaid tab to its fixture set (sample.md has a diagram) and switch away
  and back 5 times.

### Verification Plan
- `build/publish.ps1` exit 0; `test-tabs.ps1` PASS including 5 round-trips
  through the mermaid tab (no `ObjectDisposedException` in the log).
- `measure-memory.ps1 -Files testdata/sample.md testdata/sample-plan.md
  <local heavy README>`: 3 tabs ≤ single heaviest tab + 10 MB; after cycling
  twice ≤ that + 5 MB.
- Switch cost from the log on sample-plan.md < 250 ms.
- Scroll restore after switch away/back: restored offset equals saved offset
  ±1 px (log lines `tab scroll saved` / `tab scroll restored`).
- Live reload of an inactive tab (script rewrites the file, then activates
  the tab): new content visible, log shows one render, no stale banner.

### Phase Summary
One live document per window. Every other open tab is now text plus a scroll
offset; the control tree belongs to whichever tab is on screen.

- **`ActivateTab` is the invariant's home.** Leaving a tab: save `ScrollY`,
  hide the viewer, `Search.Detach()`, `Viewer.Markdown = null`, log
  `tab scroll saved … ; tree dropped`. Arriving: the viewer is empty by
  definition, so activation always calls `RenderTab(tab, tab.CurrentText,
  tab.SavedScrollY)`, which restores the offset and rebuilds TOC and search on
  the way. The welcome viewer follows the same rule — its tree is dropped when
  a document takes the window and rebuilt in `ShowWelcome`.
- **`PendingText` is gone**, and with it every deferred-render branch.
  `CurrentText` is the single source of truth: a background reload writes it
  and stops (`reload stored (inactive tab)`), `RerenderAllTabs` only touches
  what is on screen, and `RenderTab` on a non-active tab is a logged no-op.
  A failed open now also stores its error page in `CurrentText` rather than
  assigning `Viewer.Markdown` once — otherwise the tab came back blank.
- **Viewers still stay attached to the host, hidden.** Not for the Mermaid
  crash any more (that is fixed) but because the template — and the
  `PART_ScrollViewer` the scroll hook rides on — is built once on first
  attachment.
- **Dropping the tree only frees it if nothing points into it.** The first
  measurement of three heavy tabs settled at 455 MB instead of ~315: the tab's
  `HeadingControls` list still held one `TextBlock` per heading (308 of them on
  the stress fixture), and a detached control keeps its own parent chain alive,
  so most of the discarded document stayed reachable. `ActivateTab` now clears
  the list along with the tree. `TocEntries` are plain data and stay;
  `DocumentSearch.Detach()` already dropped its own control references. Any
  future cache of rendered controls needs the same treatment.
- **Log-line ordering is load-bearing for the scripts**: `tab switched to
  '<name>'; render <ms> ms` is posted at `Background` priority from
  `RenderTab`'s `afterLayout`, so it lands *after* the scroll restore's own
  second pass. `tab switched` is therefore reliably the last line of a switch,
  and `Switch-Tab` in `test-tabs.ps1` waits on it. Waiting on the command
  acknowledgement alone reads state mid-render.
- **Vendored `Rendering/MermaidFenceRenderer.cs`** (MIT, MarkView copyright in
  the header and in `THIRD-PARTY-NOTICES.md` under a new "Vendored source"
  section). Only the mermaid path is copied; MarkLite's own code renderer keeps
  every other fence and calls `MermaidFenceRenderer.Write` directly. Three
  changes against upstream:
  - attach and detach are both hooked on the **visual** tree, so they pair.
    Upstream registered a fresh `DetachedFromLogicalTree` handler inside each
    `AttachedToVisualTree`, and every one of them cancelled and disposed the
    same `CancellationTokenSource` — the second detach threw
    `ObjectDisposedException`, which under this phase's drop-and-rebuild is
    certain rather than rare;
  - the token source is nulled after disposal, and a diagram whose render was
    cancelled by a detach restarts on re-attach;
  - the `Application.PropertyChanged` (theme) subscription is taken on attach
    and released on detach, instead of taken at render time and released only
    from inside a handler that a diagram with no `ScrollViewer` ancestor never
    reached.
  Handlers are held in explicit delegate locals: a local function converted to
  a delegate at two call sites is not guaranteed to produce equal instances,
  and `-=` removes only an equal one.
- **`MarkView.Avalonia.Mermaid` is no longer referenced.** The package existed
  only for that renderer, so the csproj now takes `Mermaider` 0.12.2 and
  `Svg.Controls.Skia.Avalonia` 12.0.0.15 directly — the versions it depended
  on. AOT publish is clean and diagrams render.
- **Switch cost is small on ordinary documents** (11 ms worst case across
  sample.md and sample-plan.md) and is the whole reason the stress fixture's
  numbers below are still large: re-rendering 528 KB on every activation is
  what Phase 3 removes.

### Verification Plan results
- `build/publish.ps1` exit 0, **0 warnings**. **PASS**
- `test-tabs.ps1` → **ALL PASS (14 checks)**, including `leaving a tab drops
  its rendered tree`, tab 1's offset restored to 1125.3 px, and **five
  round-trips through the mermaid document with no exception** (`ObjectDisposed`
  never appears — the same loop against the packaged renderer is what used to
  throw). Slowest switch render printed: **11 ms**. **PASS**
- Regression: `test-toc-search.ps1` ALL PASS (12) on sample-plan.md and ALL
  PASS (12) on stress-large.md (308 headings, `find station` = 373 both sides);
  `test-html-comments.ps1` ALL PASS (9). **PASS**
- Memory, published exe, 1400x1000. The plan's fixture set (sample.md +
  sample-plan.md + stress-large.md, the last of them active at the end):

  | Stage | Tabs | Working set (MB) | Managed (MB) |
  |---|---:|---:|---:|
  | first render (sample.md) | 1 | 73.2 | 6.9 |
  | opened 2 tabs | 2 | 75.4 | 10.0 |
  | opened 3 tabs (stress active) | 3 | 446.8 | 260.5 |
  | after scroll-through | 3 | 315.3 | 239.3 |
  | after cycling tabs twice | 3 | 405.7 | 253.6 |
  | after gc | 3 | 315.1 | 239.3 |

  Single heaviest tab on the same build: **313.5 MB after gc**. Three tabs
  settle at **+1.6 MB** over it (plan allows +10), unchanged after cycling
  twice (allows +5). **PASS**. The raw `opened 3 tabs` and `after cycling`
  rows are transients: activation re-renders the whole 528 KB document and the
  spike only comes down at the next collect. Phase 3 removes the spike by
  never building the whole tree.

- That set understates the phase, because two of its three documents are ~2 KB.
  **Three copies of the 528 KB fixture** is the shape the phase was for:

  | Stage | Tabs | Working set (MB) | Managed (MB) |
  |---|---:|---:|---:|
  | first render | 1 | 323.8 | 246.2 |
  | opened 2 tabs | 2 | 672.3 | 487.0 |
  | opened 3 tabs | 3 | 547.4 | 387.0 |
  | after scroll-through | 3 | 324.8 | 245.0 |
  | after cycling tabs twice | 3 | 486.9 | 256.7 |
  | after gc | 3 | 320.9 | 241.7 |

  Three heavy documents settle at **320.9 MB — the cost of one** (the old
  renderer would have held all three trees at once). Working set is now flat in
  the NUMBER of tabs; it is still linear in the size of the ACTIVE document,
  which is Phase 3's job.
- The stress fixture's single-tab figure has grown against the Phase 1
  baseline (313.5 vs 303.0 MB after gc). Not this phase's doing: Phase 1A's
  clear-then-set re-render fix landed after that baseline and stress-large.md
  was never re-measured. Recorded here so Phase 3's comparison starts from a
  real number.
- `DocumentTab.HeadingControls` is cleared on deactivation (see the summary);
  without it three heavy tabs settled at 455 MB instead of 320.9.
- Switch cost on sample-plan.md: **11 ms** (plan allows < 250). **PASS**
- Scroll restore after switch away/back: 1125.3 px saved, 1125.3 px restored
  (±1 allowed). **PASS**
- Live reload of an inactive tab (one-off script, not committed — Phase 4's
  `test-reload.ps1` is where this becomes permanent): rewriting the file logs
  `reload stored (inactive tab)` and renders nothing; activating the tab
  renders **exactly once**; the new text is findable, the old text is not; no
  stale banner. **8/8 PASS**

## Phase 3: Virtualized host — model, block panel, realization
Status: Complete

The new control renders `testdata/stress-large.md` fast and flat in memory.
Wired into the app behind a temporary `MARKLITE_VIRTUAL=1` env switch so both
renderers can be measured side by side until the cutover phase; TOC, search,
selection and links are Phases 4–6.

- [x] `tests/MarkLite.Tests` (xunit, net10.0, references the app project;
  `InternalsVisibleTo` in the app csproj). Runs with `dotnet test`. Tests
  are added per item below.
- [x] `Rendering/Virtual/MarkdownDocumentModel`: parse with the shared
  pipeline; `Blocks` = top-level `Block` list with `SourceSpan`, source
  slice, and a 64-bit FNV-1a hash of the slice; `Headings` (level, text,
  slug, block index) computed from the AST with a fresh `SlugGenerator` and
  an inline-text extractor equivalent to `HeadingRenderer`'s; `Anchors`
  (slug → block index) covering headings, footnote definitions (`fn-<n>`),
  and explicit `{#id}` attributes; `TocEntries` via `TocEntry.BuildTree`.
  Tests: block count/spans on the fixtures, slug dedup order matches
  MarkView (`-1`, `-2`), setext headings included, hash stable across parses.
- [x] `Rendering/Virtual/BlockRealizer`: one `AvaloniaRenderer` per document
  (`BaseUri`, `ImageResizeMode`, extensions registered in the same order as
  today, `pipeline.Setup` — all before the first `Write`); `Realize(index)`
  calls `renderer.Write(block)`, moves the delta children out of
  `RootPanel` into a `BlockContainer : Panel` (one per block; zero children
  allowed), sets `Tag = index`. Recycle = drop the container (GC). Also
  applies MarkView's `=WxH` image-size preprocessor to the source before
  parsing (one regex, copied).
- [x] `Rendering/Virtual/VirtualBlockPanel : Panel`, hosted in the
  `ScrollViewer`: per-block `double[] Heights` + `bool[] Measured`; extent =
  sum; unmeasured height = cached height for the block's hash at the current
  width, else the running average of measured blocks (default 40 px before
  any measurement); realization window = viewport ± 1 viewport height;
  `MeasureOverride` measures realized children, records heights, invalidates
  extent; `ArrangeOverride` places them at cumulative Y; child
  `SizeChanged` (async images/mermaid/math) updates heights; **scroll
  anchoring**: when a block above the viewport top changes height, the
  offset shifts by the delta so content under the cursor stays put. Public:
  `ScrollToBlock(index, offsetWithin)`, `FirstVisibleBlock`,
  `RealizedRange`, `BlockOffset(index)` (exact if all above are measured,
  else estimated), `ScrollAnchor` get/set = (block index, pixel offset
  within block).
- [x] Height cache: `Dictionary<(ulong hash, int width), double>` per
  document, survives re-parse (live reload), cleared on font/theme change.
  Test: reload with one changed paragraph keeps every other cached height.
- [x] `Rendering/Virtual/VirtualMarkdownView : MarkdownViewer` — inherits
  the `:is(mv|MarkdownViewer)` template and both style sheets, sets
  `Content` to the `VirtualBlockPanel` in its constructor and **never** sets
  `Markdown` (guard: setting `Markdown` throws `InvalidOperationException`
  with a message pointing at `Load(text)`). API: `Load(string text)`,
  `Model`, `Panel`, `LinkClicked` re-raised (Phase 6), `Realized` event for
  Phase 5 highlighting. First item of the phase: confirm MarkView's theme
  styles (`markdown-paragraph`, `markdown-code-block`, tables, lists) apply
  inside the subclass on a Debug run; if any are scoped in a way that skips
  us, retarget those selectors in `MarkdownTheme.axaml` instead.
- [x] Wire-up behind `MARKLITE_VIRTUAL=1`: `CreateViewer` returns the new
  view; `RenderTab` calls `Load`; TOC/search/anchor code paths tolerate the
  new view (no-ops logged) until Phases 4–6. Debug `dump-state` reports
  realized/total blocks, extent, and measured-fraction.
- [x] Idle trim unchanged; log `render: parsed <n> blocks in <ms>, first
  layout <ms>, realized <k>`.

### Verification Plan
- `dotnet test` → all green.
- Published exe with `MARKLITE_VIRTUAL=1`: `measure-memory.ps1 -Files
  testdata/stress-large.md` → first content render < 300 ms; working set
  after first render < 90 MB; after `scroll-end`, `scroll 0`, 10× `scroll-page`
  down + `gc` **< 100 MB**; `dump-state` realized blocks ≤ 3 viewports'
  worth at every sample (< 10 % of total).
- Same for `testdata/sample.md` and `sample-plan.md`: numbers ≤ the current
  renderer's (Phase 1 baseline) and visually identical PrintWindow captures
  at offset 0 (compare `MARKLITE_VIRTUAL` on/off; differences limited to
  the scrollbar thumb).
- Scrollbar stability: script edits one paragraph in a copy of the stress
  fixture while scrolled to the middle → `dump-state` extent changes by
  < 2 % and `scrollY` by < 1 viewport; log shows the anchor block unchanged.
- Resize check by script (UIA `TransformPattern.Resize` on the window, no
  input injection): no exception, extent re-estimated, anchor block kept.

### Phase Summary
The 528 KB fixture renders in 111 ms and costs 79 MB, flat as it is scrolled
end to end. Both renderers are live side by side behind `MARKLITE_VIRTUAL=1`.

- **`Rendering/Virtual/MarkdownDocumentModel`** — everything knowable without
  building controls: top-level blocks with source spans and an FNV-1a hash per
  block, every heading (nested ones included) with the slug MarkView's own
  renderer would give it, an anchor table, and the TOC tree via
  `TocEntry.BuildTree`. MarkView's `=WxH` image preprocessor is copied here,
  because spans must index the text the renderers actually see. Nothing in this
  file touches Avalonia, which is why the unit tests can be plain xunit.
- **`BlockRealizer`** — one `AvaloniaRenderer` per document; `Write(block)`
  appends that block's controls to `RootPanel`, which are then moved into a
  `BlockContainer` of their own. Extensions register and `pipeline.Setup` runs
  before the first `Write`, because Markdig caches its renderer choice per type.
  Headings are **re-tagged from the model** afterwards: MarkView's slug
  generator counts repeats and assumes one ordered pass, so under scroll-order
  realization a heading realized twice would otherwise carry two different
  anchors.
- **`VirtualBlockPanel`** — a height per block (measured, or cached, or the
  running average), offsets as a prefix sum, extent as the total; realization
  window is the viewport ± one viewport. Heights are cached by
  `(block hash, layout width)`, so a recycled block keeps its true height and a
  reload of a lightly edited file re-uses nearly every entry.
- **`VirtualMarkdownView : MarkdownViewer`** — subclassed only for the chrome
  (template, `PART_ScrollViewer`, theme, the `LinkClicked` routed event). It
  never sets `Markdown`/`Pipeline`/`BaseUri`, all of which drive the base render
  path; `Markdown` is guarded with an exception naming `Load(text)`.

Four things that were not obvious and cost real time:

- **MarkLite's own theme selectors did not match a subclass.** They were scoped
  `mv|MarkdownViewer …`, which is exact-type; MarkView's own theme uses
  `:is(mv|MarkdownViewer)` for exactly this reason. All 30 MarkLite selectors
  were retargeted to `:is(...)`, so both viewers are styled identically.
- **`viewer.UseMath()` reassigns `Pipeline`.** Assigning `Pipeline` runs
  `MarkdownViewer.RenderMarkdown`, which sets `Content` from scratch — on the
  virtual view that silently threw the panel away, and the symptom was "the
  scroll extent equals the viewport". The call was replaced with
  `Extensions.AddMath()`; the maths parsing it wanted is already in
  `MarkLitePipeline.Shared`.
- **The parse pipeline is now a single named thing** (`Rendering/MarkLitePipeline`),
  shared by both viewers, the model and the tests. Before this it was rebuilt in
  three places, and `UseMath()` quietly replaced it with a fourth.
- **A scroll correction cannot be applied from inside the layout pass that
  computed it.** The ScrollViewer arranges around its content and re-clamps
  `Offset` afterwards, so the write was discarded; it is posted at `Loaded`
  priority instead. And a width change invalidates every height at once, so the
  anchor block is *held* across the following passes until its offset stops
  moving — correcting once left the reader 62 blocks away.

Known gaps, all owned by later phases and all logged rather than hidden:

- **Search still walks the rendered tree**, so under the virtual viewer it finds
  matches in realized blocks only (1 of 373 on the stress fixture). The app logs
  `search: realized blocks only (virtual viewer)` and the affected checks report
  SKIP with the real numbers. Phase 5.
- **Current-section tracking is block-level**, without the Phase 4 refinement
  from realized heading controls.
- **A resize leaves the reader within a few blocks**, not exactly on one: blocks
  above the viewport are never realized, so their heights stay estimates and the
  anchor's absolute offset can only be as good as those.
- **TOC entries are already model-backed** — brought forward from Phase 4
  because otherwise the sidebar would have been empty under the flag and the
  capture comparison meaningless. `HeadingControls` and the `toc mismatch` path
  are untouched and still Phase 4's to remove.

### Verification Plan results
- `dotnet test` → **15 passed, 0 failed**. Two of those tests exist because the
  first version of them failed: the app's pipeline enables **neither footnotes
  nor generic `{#id}` attributes** (they are not in MarkView's
  `UseSupportedExtensions`), so a footnote definition parses as an ordinary
  paragraph — including the three in the stress fixture. The model handles both
  correctly and a test proves it against a pipeline that enables them; a second
  test pins the app's actual behavior so enabling them later is a deliberate
  change. **PASS**
- Stress fixture block count is **2073**, not the 2078 the generator reports —
  the generator counts blocks it emits, the parser merges some. Pinned in a
  test. **RECORDED**
- Published exe, `MARKLITE_VIRTUAL=1`, `stress-large.md`: first content render
  **111 ms** (< 300), working set after first render **79.2 MB** (< 90), after
  `scroll-end` + `scroll 0` + 10 pages + `gc` **79.1 MB** (< 100). Realized
  blocks never exceeded **18 of 2073 (0.9 %)**, against a < 10 % limit. **PASS**
- `test-virtual.ps1` → **ALL PASS (18 checks)**, including extent 224 401 px
  over the whole document, `scroll-end` reaching the last block, `toc 250`
  landing on section 250, working set 89.5 MB after the scroll workout, and a
  300 px window resize keeping the reader within 5 blocks (measured drift: 3).
- Memory, all three fixtures, virtual vs the Phase 2 classic numbers:

  | Stage | Classic | Virtual |
  |---|---:|---:|
  | first render (sample.md) | 73.2 | 74.1 |
  | opened 2 tabs | 75.4 | 75.5 |
  | opened 3 tabs (stress active) | 446.8 | 88.3 |
  | after scroll-through | 315.3 | 91.4 |
  | after gc | 315.1 | **89.9** |

  sample.md costs **+0.9 MB** under the virtual viewer — the per-block arrays
  and the parsed model — which is the one number that is not an improvement.
  Everything involving the large document is roughly a quarter of the classic
  figure.
- Regression, **classic renderer** (`MARKLITE_VIRTUAL` unset): `test-tabs`
  ALL PASS (14), `test-toc-search` ALL PASS (12) on both fixtures,
  `test-html-comments` ALL PASS (9). **PASS**
- Regression, **virtual renderer**: `test-tabs` ALL PASS (14),
  `test-html-comments` ALL PASS (9), `test-toc-search` ALL PASS (12, 2 skipped)
  on sample-plan.md and ALL PASS (12, 3 skipped) on stress-large.md — the skips
  are the search gap above, printed with their real numbers. **PASS**
- Capture comparison at offset 0 on `sample-plan.md`, classic vs virtual,
  1400x1000: **0.15 % of sampled pixels differ**, and the difference is the
  scrollbar thumb (shorter under an estimated extent) — which the plan allows.
  Text column, sidebar, tables and blockquotes are pixel-identical.
- Resize by script used `SetWindowPos` with `SWP_NOACTIVATE`, not UIA
  `TransformPattern`: same effect, one less dependency, still no injected input
  and no focus stealing (the same substitution Phase 1 made for `WM_CLOSE`).

## Phase 4: Feature parity — TOC, anchors, scroll anchor, live reload
Status: Complete

Footnotes on, the sidebar and every anchor served from the parsed model, and the
reader's place expressed as a block rather than a pixel offset — so a reload that
inserts fifty paragraphs above the viewport, or a theme change that re-wraps the
whole document, leaves them looking at the same paragraph.

- [x] Enable footnotes in `MarkLitePipeline.Shared`
  (`Use<Markdig.Extensions.Footnotes.FootnoteExtension>()`, NOT MarkView's
  `UseFootnotes` — the name is ambiguous between the two namespaces). Knock-on
  effects to handle in the same item: `[^n]` becomes a superscript link instead
  of literal text; definitions leave their place in the flow and collect into a
  separator + footnote group at the END of the document (visible relocation for
  any file that defines them mid-document); `fn-<n>` anchors start resolving,
  which is what the footnote-slug check below needs. Update the pinned block
  count in `MarkdownDocumentModelTests` (2073 → 2074) and replace
  `FootnoteAndIdAnchorsAreAbsentUnderTheAppsPipeline` with one that asserts
  footnotes present and `{#id}` still absent. Re-run
  `test-html-comments`/`test-toc-search` on both renderers: the fixtures other
  than the stress file were measured not to move, so any change there is a real
  regression.
- [x] TOC sidebar from `Model.TocEntries` (no visual-tree walk); remove
  `HeadingControls` and the `toc mismatch` path. Current-section tracking:
  nearest heading block index ≤ `FirstVisibleBlock`, refined with the real
  Y of realized heading controls (`Tag` slug lookup) when present.
- [x] `ScrollToHeading`/`ScrollToAnchor` → `Panel.ScrollToBlock(index, -8)`;
  non-heading slugs resolved through `Model.Anchors`; unknown slug logged.
  After the jump, when the target block was unmeasured, a second pass at
  `Background` priority corrects the offset once heights are real.
- [x] Scroll preservation switches from pixel offset to `ScrollAnchor`
  (block hash + offset within block): saved on deactivate, restored on
  activate; on live reload the anchor block is found by hash first, then by
  nearest index; the same two-pass restore as today. `DocumentTab.SavedScrollY`
  replaced accordingly; log lines keep the `scroll saved/restored` wording
  and add the block index.
- [x] Live reload reuses realized containers whose block hash is unchanged
  (moved into the new model's index), so an edit far from the viewport
  changes nothing on screen and an edit inside the viewport re-realizes only
  that block. Heights carried over by hash.
- [x] Theme and body-font changes: model kept, all containers dropped, height
  cache cleared, re-realize at the same anchor.
- [x] Update `tools/verify/test-toc-search.ps1` (TOC half) and
  `test-reload.ps1` (new: append/insert/delete-paragraph edits with anchor
  assertions) for the virtual view.

### Verification Plan
- `dotnet test` green (anchor mapping, hash-based reuse, footnote anchors).
- Footnotes: `stress-large.md` parses to 2074 blocks and 311 anchors; the
  rendered document shows a footnote group at the end and `[^n]` no longer
  appears as literal text; `sample.md`, `sample-plan.md` and `sample-html.md`
  render identically to Phase 3 (capture comparison, differences only where a
  footnote exists).
- `test-toc-search.ps1` TOC half PASS on stress fixture: `toc 250` lands the
  heading within 8±2 px of the viewport top (measured via `dump-state`
  after the correction pass); current-section index becomes 250; `anchor`
  to a footnote slug scrolls to the footnote block.
- `test-reload.ps1` PASS: insert 50 paragraphs above the viewport → visible
  block unchanged (anchor hash), `scrollY` grows by the inserted extent;
  edit the visible paragraph → only that block re-realized (log shows
  `reused <n-1> containers`); delete the anchor block → nearest index used,
  no exception.
- Switch away/back on the stress fixture (Phase 2 path): anchor restored to
  the same block and offset ±1 px; `render` time < 300 ms.

### Phase Summary
The virtualizing viewer now navigates entirely from the parsed model, and the
reader's position is content-addressed. `dotnet test` is at 25 tests;
`test-virtual` at 24 checks; `test-reload` is new at 17.

- **Footnotes are on** (`Markdig.Extensions.Footnotes.FootnoteExtension`, named
  explicitly because both Markdig and MarkView publish a `UseFootnotes` and they
  are not the same call). MarkView registers `FootnoteGroupRenderer` and
  `FootnoteLinkRenderer` unconditionally, so only the parser side needed
  switching on. `[^n]` is now a superscript link, definitions collect into a
  group at the end of the document, and `fn-<n>` anchors resolve — under both
  renderers.
- **The contents sidebar, every anchor and the current-section highlight come
  from the model.** `ScrollToAnchor` no longer searches the sidebar's heading
  list first: `Model.Anchors` already covers headings, footnotes and explicit
  ids and resolves each to the block that has to be realized.
- **Current-section tracking is refined by realized heading controls**, which
  matters when one top-level block holds several headings (a heading inside a
  quote or list item) and when a tall block starts above the viewport while its
  headings are still below it.
- **A jump corrects itself.** `ScrollToBlock` aims at an offset built partly from
  estimated heights, then re-aims at `Background` priority once the blocks it
  realized have measured — bounded to two passes, and skipped entirely when
  every block above the target is already measured. The landing is now exact:
  8.0 px, on `toc 5` and on `toc 250` alike.
- **`ScrollRestore`** (block hash + index + offset within the block) replaces
  `DocumentTab.SavedScrollY`, with `CaptureScroll`/`RestoreScroll` on the tab.
  Restoring tries three things, weakest last: the reload alignment, the block
  hash, the old index.
- **Reload reuses containers.** `VirtualBlockPanel.Load` carries over every
  realized container whose block survived, and `BlockRealizer.Rebind` re-points
  one renderer at the new model instead of building a second one (controls
  already built keep working against the renderer that made them).
- **Theme, body font and comment visibility now call `ResetLayout`** instead of
  re-parsing: the model and the sidebar are kept, every control and every
  measured height is dropped, and the anchor is held across the passes it takes
  for the new heights to settle.

Four things that were not obvious, all found by the scripted checks:

- **Markdig gives `FootnoteGroup` and `LinkReferenceDefinitionGroup` a span
  covering the whole document.** Believed, the last block of the file changes on
  every edit anywhere in it — and the reload alignment works inward from both
  ends, so a last block that never matches collapses the entire suffix. The
  symptom was "5 of 2074 blocks aligned" and a reader thrown to a different
  paragraph on every reload. Both groups are containers whose children have
  honest spans, so each is now described by its definitions: extent from the
  children's min/max, hash over the children's slices in order.
- **A nearest-hash lookup is not good enough to align two versions of a
  document.** The stress fixture repeats paragraphs verbatim, so an insert above
  the viewport handed the reader an identical paragraph four blocks away and
  reported every container as re-used including the one that had just changed.
  Replaced by `MarkdownDocumentModel.AlignFrom`: longest matching prefix,
  longest matching suffix, `-1` in between. Exact for insert, edit and delete,
  O(n), no diff algorithm. The hash lookup survives as the fallback for a tab
  switch, where there is no previous model to align against.
- **A block that renders to nothing was still charged the 8 px inter-block
  gap.** Raw HTML, YAML front matter and the link-reference group produce no
  controls at all; MarkView's own root panel spaces its *children*, so a block
  that contributes none costs nothing. The virtual panel was adding the gap
  regardless, which pushed everything below such a block down — 12 px on
  `sample-html.md`, which opens with an `<img>` tag. Caught by the capture
  comparison (5.7 % of pixels differing), now 0 %.
- **A container realized during the current scroll event has not been arranged
  yet**, so its `TranslatePoint` still reports where it was before the jump.
  Refining the current section from those positions put the reader in whichever
  section had been on screen a moment earlier. Guarded on `IsArrangeValid`, with
  the block index as the answer until layout has run. Related: a heading carries
  a top margin, so the *drawn* glyphs sit lower than the block does — the
  refinement measures the heading's layout slot, or a heading the reader was
  just sent to reads as not-yet-reached.

One item is deliberately **not** done, and is carried to Phase 7:

- **`DocumentTab.HeadingControls` and the `toc mismatch` log line stay.** The
  classic viewer is still the default until the cutover, and its
  `ScrollToHeading` and `UpdateCurrentSection` have no other way to find a
  heading's position — MarkView exposes no block-to-control mapping. The virtual
  path never touches the list (it is cleared on every rebuild), so nothing is
  pinned by it; removing it is one line of Phase 7's classic-path deletion
  rather than a separate change that would leave the default renderer's sidebar
  broken for Phases 5 and 6.

### Verification Plan results
- `dotnet test` → **25 passed, 0 failed**. New: footnote anchors under the app's
  own pipeline, `{#id}` still absent, the four `AlignFrom` cases (insert, edit,
  delete, repeated blocks), the synthetic-group span and hash fix, and the
  fixture-scale alignment (2073 of 2074 blocks recognised across an insert; the
  one loss is the seam where the inserted paragraph merges with what follows).
  Block count pinned at **2074** and anchors at **311** (308 headings + 3
  footnotes), both as the plan predicted. **PASS**
- Footnotes: `stress-large.md` parses to **2074 blocks and 311 anchors**;
  `anchor fn-1` resolves to block 2073, the footnote group at the end of the
  document, under both renderers. `find station` still reports **373** on the
  classic renderer, so the footnote rendering added no stray prose. **PASS**
- Capture comparison at offset 0, classic vs virtual, 1400x1000, every second
  pixel sampled:

  | Fixture | Differing | Where |
  |---|---:|---|
  | `sample.md` | 0.012 % | scrollbar column only (x 1342) |
  | `sample-plan.md` | 0.147 % | one heading 1 px lower, from "Baseline measurements" down |
  | `sample-html.md` | **0 %** | — |

  Phase 3 recorded the same 0.147 % on `sample-plan.md` and attributed it to the
  scrollbar thumb; it is not. It is a sub-pixel accumulation difference: the
  panel sums `DesiredSize.Height` where the classic `StackPanel` arranges
  children directly, and after a blockquote with a fractional height the next
  heading snaps to the neighbouring device pixel. One heading, 1 px, everything
  else identical. **RECORDED**
- `test-toc-search.ps1`, virtual, `stress-large.md` → **ALL PASS (17 checks,
  3 skipped)**. `toc 5` and `toc 250` both land the heading **8.0 px** below the
  viewport top after the correction pass and both become the current section;
  the footnote anchor resolves to the last block. The three skips are the
  known search gap (Phase 5), printed with their real numbers. On
  `sample-plan.md`: ALL PASS (14, 3 skipped). **PASS**
- `test-reload.ps1` → **ALL PASS (17 checks)**. 1200-block generated document,
  reader parked on block 359: insert 50 paragraphs at the top → **1200 of 1200
  blocks aligned**, reader on block 409, all **43 of 43** containers carried
  over, scroll 14749 → 16788 px; rewrite the paragraph under the reader →
  **1249 of 1250** aligned, **42 of 43** carried over, reader still on 409;
  delete it → 1249 blocks, reader still on 409 via the old-index fallback.
  No exceptions. **PASS**
- `test-virtual.ps1` → **ALL PASS (24 checks)**, including the two new items:
  a comment-visibility toggle keeps the model (2074 blocks), the sidebar (308
  headings) and the reader's block (602 → 602); and a switch away to
  `sample.md` and back renders in **51 ms** (< 300) and brings back **block 998
  at the same 74 px into it**. The pixel offset legitimately differs by ~2.9 k px
  because a fresh render re-estimates the unmeasured blocks above the viewport —
  which is exactly why `dump-state` now reports `anchorWithin`. **PASS**
- Regression, **classic renderer**: `test-tabs` ALL PASS (14),
  `test-html-comments` ALL PASS (9), `test-toc-search` ALL PASS (13, 1 skipped)
  on `sample-plan.md` and ALL PASS (14) on `stress-large.md`. **PASS**
- Regression, **virtual renderer**: `test-tabs` ALL PASS (14),
  `test-html-comments` ALL PASS (9). **PASS**
- Memory, all three fixtures, after this phase:

  | Stage | Classic | Virtual |
  |---|---:|---:|
  | first render (sample.md) | 73.9 | 74.3 |
  | opened 2 tabs | 75.6 | 75.2 |
  | opened 3 tabs (stress active) | 379.2 | 87.9 |
  | after scroll-through | 315.8 | 93.2 |
  | after cycling tabs twice | 407.2 | 89.4 |
  | after gc | 314.9 | **88.3** |

  Unchanged in character from Phase 3, and the footnote group costs nothing
  measurable.

## Phase 4A: Line-number gutter (View toggle)
Status: Complete

A left margin showing where each block starts in the source, so what is on
screen can be found again in the editor. Built on the Phase 3 model
(`Markdig.Block.Span` → source line) and the Phase 4 anchor work.

- [x] `MarkdownDocumentModel`: per-block `StartLine` (and `EndLine`) computed
  once from the source text — a prefix array of newline offsets, binary-searched
  per block span. Test: line numbers of the first and last block on each
  fixture; setext heading reports its text line, not the underline.
- [x] `Rendering/Virtual/GutterPanel`: drawn beside `VirtualBlockPanel`, sharing
  its scroll offset and per-block Y positions; one right-aligned number per
  realized block at the block's top; recycled with the blocks it labels. Uses
  the code font, muted foreground, fixed width sized to the document's largest
  line number (so the text column does not shift while scrolling).
- [x] Fenced code blocks get **line-by-line** numbers: the code renderer
  already lays lines out 1:1, so the gutter labels each row from the block's
  start line. Every other block gets a single number at its top; wrapped rows
  stay unnumbered.
- [x] View menu `Show line numbers` checkbox (default off) + `UserSettings`
  persistence + debug command `gutter <on|off>`; `dump-state` gains
  `gutterVisible` and the first/last visible source line.
- [x] Layout: the gutter is outside the document's `MaxWidth` centering, so
  turning it on must not reflow the text column (verified by capture diff).
- [x] `tools/verify/test-gutter.ps1`: toggle on → `dump-state` first visible
  source line matches the line computed from the fixture for the first visible
  block (±0); scroll to a known heading via `toc <n>` and compare against the
  fixture's grep line number; captures with the toggle on and off.

### Verification Plan
- `dotnet test` green (line-number mapping, code-fence numbering).
- `test-gutter.ps1` ALL PASS on `sample-plan.md` and `stress-large.md`.
- Capture diff with the gutter off before/after the phase: identical text
  column position.
- Memory on `stress-large.md` with the gutter on stays under the phase target
  (< 100 MB) and realized block count is unchanged.

### Phase Summary
A muted column of source line numbers down the left of the document, off by
default, and free to turn on: the strip it draws in is reserved whether the
numbers show or not, so the toggle repaints 20 px of pixels and touches nothing
else.

- **`MarkdownDocumentModel` blocks carry `StartLine`/`EndLine`** (1-based),
  computed from the block's span against a prefix array of newline offsets built
  once per parse. From the span rather than Markdig's own `Block.Line`, so the
  numbers agree with the source slice the model hands out — and so the synthetic
  footnote and link-reference groups, whose `Line` is meaningless, report the
  lines their definitions are actually on.
- **`Rendering/Virtual/GutterPanel`** is a `Control` that draws rather than
  building controls: one `FormattedText` per realized block, right-aligned
  against the gap. A number per block — and per LINE of every code fence — as
  TextBlocks would be exactly the tree the virtualizing host exists to avoid. It
  is a permanent child of `VirtualBlockPanel`, arranged over the full document
  height, so a block's offset means the same number in both coordinate spaces
  and no scroll offset has to be tracked.
- **Fenced code is numbered line by line**, read off the code `TextBlock`'s own
  `TextLayout.TextLines` rather than multiplied out from a line height, so the
  numbers stay aligned if a theme changes the code font or its spacing. The fence
  line is the block's start, so the first line of code is one past it.
- **`VirtualBlockPanel` reserves the strip on BOTH sides**, always. That is the
  whole design: the document stays centred, and turning the numbers on re-wraps
  no text, moves no block and invalidates not one cached height.
- **View > Show line numbers**, default off, persisted in
  `UserSettings.ShowLineNumbers`, plus a `gutter <on|off>` debug command.
  `dump-state` gains `gutterVisible`, `firstVisibleLine`, `lastVisibleLine` and
  `targetBlockLine`, so the numbers can be checked against a grep of the fixture
  instead of against pixels.

Three things worth knowing:

- **The plan's layout constraint could not be met as written.** "The gutter is
  outside the document's `MaxWidth` centering" assumes spare room outside the
  centred box, and at the verification window size there is none: the sidebar
  leaves `ViewerHost` about 1015 px wide, so a `MaxWidth` of 1100 never binds and
  the text column already fills the space. Resolved with the user (2026-08-25):
  reserve the strip permanently on both sides so the *toggle* never reflows, and
  accept a one-time narrowing of the text column. The virtual viewer's outer
  margin drops from 28 px to 8 px to pay for part of it; the classic viewer is
  untouched, so classic and virtual captures now differ by that shift — expected,
  and it goes away with the classic path at the cutover.
- **Prose is selectable too.** The first version found the code text by looking
  for a `SelectableTextBlock` in the container, which also matches paragraphs —
  and numbering a paragraph's *wrapped* rows invents source lines that do not
  exist. The guard is the block type (`CodeBlock`, mermaid fences excluded), not
  the control type.
- **A group block's number can read lower than the block above it.** Footnote and
  link-reference definitions are gathered to the end of the document but their
  source lines are wherever they were written, so a file that defines them
  mid-document shows a number going backwards at the very bottom. That is the
  truth about where the text lives, and the fixtures keep their definitions at
  the end, so it does not arise there.

### Verification Plan results
- `dotnet test` → **30 passed, 0 failed**. Five new: source lines per block
  (including a two-line paragraph), identical line numbers under CRLF and LF, a
  setext heading reporting its text line rather than its underline, a fenced
  block spanning its fence lines, and the stress fixture's numbers running
  monotonically from line 1 to inside the file's 5868 lines. **PASS**
- `test-gutter.ps1` → **ALL PASS (19 checks)** on `sample-plan.md`, **ALL PASS
  (21)** on `stress-large.md`. The toggle's full-resolution capture diff finds
  every differing pixel in **x 423..442 — a 20 px band** inside the 40 px strip,
  with block count, realized set, extent, scroll offset and visible lines all
  unchanged. `toc 5` lands on source line 35 (`sample-plan.md`) and 94
  (`stress-large.md`), both real heading lines, both on screen. The setting
  survives a restart. **PASS**
- Line numbers spot-checked against the source by hand: at `toc 6` on
  `sample-plan.md` the gutter reads 40, 41, 43, 47 for the heading, status line,
  task list and lead-in, then 50..64 down the code fence — and lines 40, 41, 43,
  47 and 49 of the file are `## Phase 2: Upload with retry`, `Status: In
  progress`, the task list, `Retry loop sketch:` and the ```` ```csharp ````
  fence. **PASS**
- Memory, `stress-large.md`, gutter **on**, after ten pages of scrolling and a
  collect: **87.8 MB** (< 100), realized blocks **18 of 2074**. Identical to the
  gutter-off figure — the gutter allocates one `FormattedText` per visible number
  per paint and nothing else. Three fixtures as tabs: 86.1 MB after gc, against
  88.3 MB before this phase. **PASS**
- One-time layout change, measured: the virtual viewer's H1 left edge moves from
  **x 429 to x 459** on `sample-plan.md` at 1400x1000, the text column narrowing
  to match. The classic viewer is unchanged at 429. This is the cost the user
  accepted in exchange for a free toggle. **RECORDED**
- Regression, **virtual renderer**: `test-virtual` ALL PASS (24), `test-reload`
  ALL PASS (17), `test-tabs` ALL PASS (14), `test-html-comments` ALL PASS (9),
  `test-toc-search` ALL PASS (14, 3 skipped) on `sample-plan.md` and ALL PASS
  (17, 3 skipped) on `stress-large.md`. **PASS**
- Regression, **classic renderer**: `test-tabs` ALL PASS (14),
  `test-html-comments` ALL PASS (9), `test-toc-search` ALL PASS (13, 1 skipped)
  and ALL PASS (14). The View menu item is present but logs
  `line numbers: classic viewer has no gutter` — the classic path never gained
  one. **PASS**

## Phase 5: Search over the model
Status: Complete

- [x] `Model.BlockText(index)`: lazily computed plain-text projection per
  block by walking Markdig inlines (`LiteralInline`, `CodeInline`,
  `LineBreakInline` → newline, `HtmlEntityInline` → decoded, link/emphasis
  children, `InlineUIContainer`-only inlines → empty), `CodeBlock` lines,
  table cells, nested lists — no list markers, code language labels or
  checkbox glyphs, matching what `DocumentSearch` skips today.
- [x] `DocumentSearch` rewritten in two layers: **model search** (matches =
  (block, start, length); count, current, next/prev, `ScrollToBlock` for the
  current match, code-block line offset kept for the scroll target) and
  **block highlighting** applied to realized blocks only, reusing the
  existing `Run`-splitting code; highlighting re-runs the substring search
  on the realized block's rendered inline text so the highlight never depends
  on the projection matching the rendered text exactly. Per-block count
  mismatch between the two is logged once per block (debug only).
- [x] `Realized` event → highlight the new block if a search is active;
  recycled blocks need no undo (they are dropped).
- [x] Current-match emphasis (orange) follows the current match across
  realization; F3 to an unrealized match scrolls, realizes, then emphasizes
  (same two-pass pattern as anchors).
- [x] Find bar count text and `find*` debug commands unchanged from the
  user's point of view; `dump-state` gains `matches` and `highlighted`.

### Verification Plan
- `dotnet test`: projection excludes markers/labels; match positions on
  fixtures; entity decoding.
- `test-toc-search.ps1` search half PASS on stress fixture: `find station`
  count equals `Select-String -AllMatches` over the projection dump
  (`dump-text` debug command added for this — writes `Model.BlockText` for
  all blocks to a temp file) — exact equality; `find-next` × 5 → current
  index advances, each target block realized (`highlighted` > 0) and within
  the viewport per `dump-state`.
- Memory: search active on the stress fixture adds < 8 MB (Phase 1 script,
  `find` then `gc`).
- Capture at the current match shows the orange emphasis (PrintWindow).

### Phase Summary
Find-in-document now reports the document rather than the screen. `find station`
on the stress fixture says **373 matches** under the virtualizing viewer, the
same number the classic renderer reports from a tree that holds all 2074 blocks,
while only 5 of those matches have a mark on them — because only 14 blocks are
realized. `dotnet test` is at 43 tests; `test-toc-search` at 42 checks on the
stress fixture (39 on `sample-plan.md`).

- **The search is two layers.** `Rendering/Virtual/ModelSearch` finds matches in
  the parsed document — `(block, start, length)` plus the line position inside
  the block — and knows nothing about Avalonia, so it is testable against a
  fixture. `Rendering/Virtual/VirtualDocumentSearch` owns the count, the current
  match and the highlighting of whichever blocks happen to be realized. The
  count and the navigation therefore do not depend on what is rendered; only the
  highlight does.
- **`MarkdownDocumentModel.BlockText(index, includeHtmlComments)`** is the
  projection: the characters a reader would see if the block were on screen, in
  that order, and nothing else. Cached per block. The plan wrote it as
  `BlockText(index)`; the flag had to be added because comment visibility is
  decided while a control is BUILT, and a projection that ignored the View
  toggle would describe a tree that is not on screen. Flipping it drops the
  cache.
- **The projection's rules are the renderers' behaviour, not markdown's
  grammar.** List bullets and numbers, the code panel's language label and the
  task checkbox are chrome (`HighlightSession` skips the same three). Raw HTML
  contributes nothing except comments, and those only while the toggle is on.
  Front matter draws nothing, a maths block draws a formula, a mermaid fence
  draws a diagram: no text to find in any of the three. An image is a picture,
  so its alt text is markup — but a link's label is text, and its destination is
  not.
- **The pin that keeps those rules honest** is a test asserting 373 for
  "station" on the stress fixture — the count the classic renderer measures off
  a fully rendered tree. Plus the app's own
  `search: block <n> projects <x> matches, renders <y>` line, logged once per
  block when a realized block disagrees with the projection. **Not one such line
  appeared in any run of any script.**
- **`HighlightSession`** (`SearchHighlight.cs`) is the run-splitting extracted
  from `DocumentSearch`, unchanged in behaviour: split the text runs at the match
  boundaries, keep spans so link hit-testing survives, record an undo snapshot.
  The classic search uses one session for the whole tree; the virtual one uses a
  session per realized block, so recycling a block is `Remove()` and nothing
  else. `IDocumentSearch` is what the window talks to; `DocumentSearch` dies with
  the classic renderer at the cutover.
- **A match inside a long block is scrolled to by its line**, not to the top of
  its block: `VirtualBlockPanel.BlockHeight` (measured where measured, estimated
  otherwise) times the match's line position, minus the 100 px the classic path
  also leaves above the match. A match halfway down a 200-line fence lands on
  screen rather than a screenful below it.
- **`dump-state` gains `highlighted`** — matches that currently carry a mark, as
  opposed to `matches`, which was already the total. The two differing is what a
  script asserts on to tell "found in the document" from "on screen with a mark
  on it". **`dump-text`** writes the whole projection to a fixed file in the temp
  directory so a script can count occurrences in the exact text the search ran
  over.

Five things that were not obvious:

- **`MathBlock` and `YamlFrontMatterBlock` both derive from `CodeBlock`** (via
  `FencedCodeBlock` in the first case). A `case CodeBlock` that did not match
  them first would have put TeX source and YAML keys into the search — findable
  strings that are nowhere on screen. Same shape as the `LinkInline :
  ContainerInline` ordering: an image has to be matched before the container case
  that would otherwise walk into its alt text.
- **A code block's projection has no trailing newline**, and its line count is
  the number of code lines — the fence lines are not part of `Lines`. Assumed
  otherwise at first; the two tests that caught it now assert the exact join,
  which is the same loop `MarkLiteCodeBlockRenderer` runs.
- **`Detach()` has to undo on this viewer.** Detach exists for a tree about to be
  discarded, but a reload here CARRIES realized containers over to the new model
  — that is what keeps the screen still — so their split runs would survive into
  a document that has renumbered its blocks, and the next `Apply` would split the
  split. `Detach` is therefore `Clear`; on containers that really are being
  discarded, restoring collections nobody can see is a no-op.
- **Highlighting a newly realized block has to be posted, not done inline.**
  Realization happens inside `MeasureOverride`, and the code panel's inner
  `SelectableTextBlock` only appears in the visual tree once the `ScrollViewer`
  around it has been templated — plus mutating inlines during the measure that
  created them fights the pass that is running. The queue flushes at `Loaded`,
  and emphasis for the current match is applied there as well as on a bounded
  `Background` retry, so F3 to an unrealized match scrolls, realizes, highlights
  and emphasizes across three priorities without a sleep anywhere.
- **`VirtualBlockPanel` needed a `Recycled` event**, and it fires in one more
  place than expected: `Load` raises it for every carried container's OLD index,
  because after a reload that index means a different block. Carried containers
  are deliberately NOT re-announced as `Realized` — their controls were built
  before, and a listener that decorated them again would decorate them twice; the
  window re-applies the search after a load instead.

### Verification Plan results
- `dotnet test` → **43 passed, 0 failed**. Thirteen new: the projection dropping
  list markers, ordered-list numbers and task glyphs; code lines kept and the
  language label dropped; entity decoding; front matter, display and inline maths
  and a mermaid fence all contributing nothing; link labels kept and image alt
  text and link destinations dropped; the comment toggle honoured in both
  directions and the cache invalidated when it flips; cells and list items
  separated so a term cannot straddle them; match offsets indexing into the block
  they name; ordinals in document order and grouped per block; the line position
  inside a fence; a footnote definition searchable in the group at the end of the
  document; the empty term; and the 373-match pin on the stress fixture. **PASS**
- `test-toc-search.ps1`, virtualizing viewer, `stress-large.md` → **ALL PASS (42
  checks)**. `find station` reports **373**, equal to the source count AND to a
  regex count over the `dump-text` projection — exact equality, not an
  approximation. `highlighted` is **5 of 373** with 14 blocks realized, and the
  script asserts the inequality directly. `find-next` × 5: the ordinal advances
  each time, the target block is inside the realized window every time
  (`0..12`, `0..13`, `2..16`, `5..18`, `8..22`), and its top sits 100 px below
  the viewport top every time. `find-close` leaves `matches` and `highlighted`
  both 0. On `sample-plan.md` → **ALL PASS (39, 1 skipped)** — the skip is the
  fixture defining no footnotes. **PASS**
- Memory, `stress-large.md`, search active on all 373 matches, collected before
  and after: **91.8 → 93.7 MB**, so **+1.9 MB** against a budget of 8. The
  projection is one string per block and the highlight is a handful of split runs
  in the realized blocks. **PASS**
- Capture at the current match (PrintWindow, 1400x1000, dark theme): the H1
  "Kestrel **Station** Operations Manual" draws the match in the orange emphasis
  colour with the dark current-match foreground while "Station" in the paragraph
  below and in the next heading draw in the plain olive match colour; the find bar
  reads `1 of 373`. After `find-next` × 2 the emphasis has moved to the "Station
  Overview" heading, the H1 match has gone back to plain, and the bar reads `3 of
  373`. **PASS**
- Regression, **virtual renderer**: `test-virtual` ALL PASS (24), `test-reload`
  ALL PASS (17), `test-gutter` ALL PASS (21), `test-html-comments` ALL PASS (9),
  `test-tabs` ALL PASS (14). `test-html-comments` is the interesting one: it uses
  find as its probe for "is this string on screen", so it now exercises the
  projection's comment flag rather than a tree walk, and still agrees on all six
  counts. **PASS**
- Regression, **classic renderer**: `test-toc-search` ALL PASS (18, 1 skipped) on
  `sample-plan.md` and ALL PASS (19) on `stress-large.md` — the two checks this
  phase turned from SKIP into assertions pass on both renderers now.
  `test-html-comments` ALL PASS (9), `test-tabs` ALL PASS (14). **PASS**

## Phase 6: Links, hover cursor, selection, copy
Status: Complete

Replaces MarkView's internal selection layer and hyperlink hit-test, which
the virtual view never instantiates.

- [x] Hyperlink hit-test: tunnel `PointerPressed`/`PointerReleased` on the
  view; find the `TextBlock` under the pointer among realized containers,
  `TextLayout.HitTestPoint` → character index → walk `Inlines` (recursing
  `Span`s) to the `MarkdownHyperlink` covering that index → `NavigateUri` →
  existing `MarkLiteHyperlinkCommand`. Image links (`InlineUIContainer`
  inside a hyperlink) handled by walking up from the hit `Image`. Hand
  cursor on hover via the same test on `PointerMoved` (throttled to the
  hovered block).
- [x] Selection model: `anchor`/`focus` = (block index, character offset in
  the block's rendered text); drag updates focus; dragging past the top or
  bottom edge autoscrolls on a `DispatcherTimer` (realizing blocks as they
  enter the window); Ctrl+A selects (0,0)–(last, end); Escape/click clears.
- [x] Selection adorner: one overlay control over the panel drawing
  `TextLayout.HitTestTextRange` rects for realized blocks inside the range;
  fully covered unrealized blocks draw nothing (they are off-screen by
  definition). Uses the app's existing selection brush.
- [x] Copy (Ctrl+C, context or menu item if one exists today): markdown
  source slice. Endpoints map through inline `SourceSpan`s when the block's
  inline at that offset is a `LiteralInline`/`CodeInline` (character
  precise); otherwise fall back to the block boundary. Ctrl+A + Ctrl+C =
  the whole file text. Code blocks keep their own `SelectableTextBlock`
  copy (already there) and are treated as whole blocks by the document
  selection.
- [x] Debug commands `select-all`, `copy`, `select <b1> <o1> <b2> <o2>`;
  `dump-state` gains `selection`.

### Verification Plan
- `dotnet test`: offset→source mapping on fixtures (paragraph middle,
  across emphasis, code block, table cell fallback).
- `test-selection.ps1` (new): `select 10 5 12 20` + `copy` → clipboard text
  equals the source substring from block 10's mapped offset to block 12's;
  `select-all` + `copy` → clipboard equals the file (byte-identical after
  newline normalization); selection over a range spanning 500 unrealized
  blocks: `dump-state` realized count unchanged, copy still correct.
- Link click via UIA: the rendered `MarkdownHyperlink` has no UIA peer, so
  the check is `anchor`-free: script sends a synthetic
  `PointerPressed`/`Released` through a debug command `click-block <index>
  <x> <y>` (in-process, no input injection) on a block whose link targets a
  heading → log `anchor link: #…` and scroll change; an external `https://`
  link is tested by the command logging the URL without launching it
  (`MARKLITE_DEBUG` suppresses `Process.Start` for the test).
- PrintWindow capture with a selection spanning three blocks shows the
  highlight on all three.

### Phase Summary
Links click, text selects, and Ctrl+C gives back markdown. `dotnet test` is at
53 tests; `test-selection` is new at 26 checks. Selecting all 2074 blocks of the
530 KB stress fixture and copying puts **540,951 characters — every byte of the
file — on the clipboard**, with 14 blocks realized.

- **The selection is a MODEL range**: `SelectionPoint` is (block index,
  character offset in that block's text), the same kind of address the scroll
  anchor uses, so it survives recycling, re-measuring and re-parsing and can
  cover blocks that have never had controls. A range over 80 unrendered blocks
  copies correctly and realizes none of them — asserted.
- **Copy hands back the markdown source.** `MarkdownDocumentModel` gained a
  second output from the Phase 5 projection walk: the runs of the projection that
  came verbatim from the file, and `SourceOffset(block, offset, atEnd)` on top of
  them. Inside a run it is exact; between runs it snaps outward, a start forward
  to the next real character and an end back to the last one, so an endpoint
  never drags in markup the reader could not see. Selecting the words of a
  heading gives the words; everything BETWEEN the endpoints is the file verbatim,
  so a selection crossing a table, a link or a code fence pastes as markdown.
- **A selection that reaches an end of the document takes that end of the file
  with it**, rather than the first or last block's own extent — which is what
  makes Ctrl+A + Ctrl+C byte-identical to the file including front matter and the
  trailing newline.
- **`BlockTextIndex`** is the rendered-text side: one realized block's TextBlocks
  concatenated in visual order (which is document order) with a newline between
  them, exactly the separator the model's projection uses — that is what lets an
  offset counted on screen be handed to the model. Built on demand per realized
  block, dropped when the block is recycled.
- **`HyperlinkHitTest`** replaces MarkView's internal hit test, which walks an
  index of the whole rendered document. It looks only at the block under the
  pointer: layout position → the innermost `MarkdownHyperlink` span covering it.
  Image links are handled from the other end, walking up from the hit `Image` to
  the `InlineUIContainer` that holds it.
- **`SelectionAdorner`** paints the highlight: one control drawing
  `HitTestTextRange` rectangles, a permanent child inserted at the BOTTOM of the
  panel's children so the band sits behind the glyphs. Selection changes on every
  pointer move during a drag; re-laying out the text of every crossed block per
  frame is not affordable, a rectangle per line is.
- **Dragging past the window edge keeps selecting**, on a 50 ms timer whose step
  is proportional to the overshoot. The pointer has not moved but the content
  under it has, so the drag point is kept in both the viewport's and the panel's
  coordinates and the focus is recomputed from the latter.
- **Edit > Copy and Edit > Select all**, with Ctrl+C and Ctrl+A, and both stay
  out of the way where they mean something else: the find box keeps them for its
  own text, and a code block keeps them for the `SelectableTextBlock` inside it,
  which is also why a press that starts inside a code block is left alone.
  Escape clears the selection.
- **`MdSelectionBackground`** is new in `App.axaml` for both variants — the app
  had no selection brush of its own, and MarkView's is a private field. It has to
  be translucent, because it is painted underneath the text.
- **Debug channel**: `select <b> <o> <b> <o>`, `select-none`, `select-all` and
  `copy` now drive the model-backed selection, and `dump-state` gains
  `selection` ("12:5-14:80 (203 chars)") — a range assertable without a pixel.

Five things worth knowing:

- **Markdig's inline spans are NOT document-absolute inside a table.** Cells are
  parsed out of a slice of the row, and their inlines carry offsets relative to
  that slice — so a cell's text claimed a source offset near the top of the file,
  and copying a cell would have handed back a plausible-looking slice of the
  wrong part of the document. Every claimed offset is now CHECKED against the
  source before it is believed, and repaired by looking for the same characters
  further on inside the same block, from where the previous run ended. A test
  with the same word in two cells pins that the repair finds the right one, and a
  test over the whole stress fixture pins that every one of its 15,000-odd runs
  really does say what the source says at the offset it claims.
- **`TextHitTestResult.IsInside` answers a different question than it looks
  like.** Gating the link hit test on it lost real clicks: the flag came back
  false for a point demonstrably on top of the glyphs — measured, at the exact
  centre of the rectangle the layout itself reported for that link. The hit is
  now confirmed against the link's own rectangles instead, which is both stricter
  (a click in the empty space right of a short line no longer follows whatever
  link ended that line) and exact.
- **A text layout and a block's text do not have the same number of
  characters.** An embedded control — an inline image, a rendered formula —
  occupies one position in the layout and contributes none to the text, because
  it is a picture and neither search nor copy can produce it. Everything that
  hit-tests or paints therefore converts between the two counts; without it a
  selection in a paragraph containing an image would copy a slice shifted by one
  character per image before it.
- **Visual hit testing was the wrong tool for "what is under the pointer".** The
  first version used `GetVisualsAt`, which has to reckon with everything
  transparent to hit testing — a Border with no background, the adorner, the
  gutter — and answers differently depending on what is on top. The panel already
  knows which block owns a content offset, and the selection uses that answer, so
  the link hit test now uses it too and the two agree by construction.
- **The adorner shares the PANEL's coordinate space, not the container's.** The
  first version translated a TextBlock's origin only as far as its container,
  which is arranged inset by the gutter strip — so the band was painted 40 px
  left of the text it belonged to. Visible in the capture, invisible in every
  numeric check, which is why the capture is part of the verification.

**The pointer plumbing was verified with INJECTED INPUT, once, with the user's
explicit permission (2026-08-25).** Press-drag-release, the autoscroll while
dragging past the window edge and the hand cursor on hover cannot be reached from
the command channel — they are the plumbing the command channel stands in for —
so a one-off script synthesised real mouse input against the published exe. It is
NOT in `tools/verify/`, and must not be: that directory is injection-free by rule,
and a suite that moves the user's cursor is not one anybody can run while working.
What survives in the repo are the two debug commands it needed, which are useful
on their own:

- **`point-text <block> <offset>`** and **`point-link <block> [n]`** report where
  a character or a link is DRAWN, in screen pixels. A check outside the process
  cannot know that — where a character lands is the outcome of wrapping, theme
  metrics and the panel's layout — so aiming without asking means guessing pixels
  and quietly testing the margin instead. With them, a synthesised drag from
  (block 4, offset 0) to (block 6, offset 30) can be asserted to select exactly
  `4:0-6:30`, which is what it did.

Two things the injected run established that no numeric check had:

- **The hand cursor has to be set on the PANEL, not on the view.** MarkView's own
  pointer handler writes a cursor onto the viewer on every move and would reset
  anything set there; Avalonia resolves the cursor from the element under the
  pointer upward, so the panel — below the viewer, above every block — wins. The
  hover check reads the real system cursor through `GetCursorInfo`, so it is the
  cursor the user would see, not a property MarkLite believes it set.
- **Autoscroll is proportional, and gentle where it should be.** Held 20 px past
  the bottom edge the document creeps 176 px in a second; held 400 px past it
  moves 700 px in the same time. A reader nudging past the edge does not want the
  document to bolt.

### Verification Plan results
- `dotnet test` → **53 passed, 0 failed**. Ten new, all on the offset-to-source
  mapping: exact inside a paragraph; a heading copying its words and not its
  hashes; across emphasis (both the endpoints and the markers kept between them);
  per line inside a code fence; through a code span's backticks; table cells,
  including two cells holding the same word; a decoded entity not indexed into
  while its neighbours are; a block that projects nothing falling back to its own
  extent; and every verbatim run of the entire stress fixture matching the source
  at the offset it claims. **PASS**
- `test-selection.ps1` → **ALL PASS (26 checks)**. `select 10 5 12 20` + `copy`
  put exactly the 159 characters those offsets name in the file on the clipboard;
  `select 300 0 380 10` copied 5930 characters across 80 blocks that have no
  controls and realized none of them (30 realized before and after);
  `select-all` + `copy` returned the generated fixture's 29,380 characters and
  the stress fixture's **540,951** — character for character in both cases, with
  14 blocks realized. `dump-state` reported `10:5-12:20 (159 chars)` and
  `4:10-8:30 (1160 chars)`, and that 1160-character slice appears verbatim in the
  file. **PASS**
- Link clicks, no input injected: `click-link 4` on the stress fixture followed
  `https://example.invalid/kestrel/handbook` — logged as
  `link would open externally:` with **zero** `link opened externally:` lines, so
  no browser was launched — and `click-link 5` followed `#station-overview`,
  which scrolled the document and logged `anchor link: #station-overview`.
  `click-link 2000`, on a block with no controls, reports no link rather than
  throwing. **PASS**
- Capture, selection over three blocks: **55,420** selection-coloured pixels
  against **3,417** with nothing selected, over **345** rows against 284. By eye
  the band aligns with the text and sits behind the glyphs, three paragraphs
  covered end to end. **PASS**
- Pointer plumbing, **injected input**, one-off script outside `tools/verify/`,
  run with the user's explicit permission → **ALL PASS (16 checks)**. Hovering a
  link shows the hand cursor and hovering prose does not, read from the real
  system cursor. A press at (block 4, offset 0) dragged in twenty steps to
  (block 6, offset 30) and released selected exactly `4:0-6:30` — 653 characters
  of markdown — and the release did not disturb it. Held 20 px below the window
  the document scrolled 176 px in a second; held 400 px below, 700 px; the
  selection followed to block 16 with 16 blocks realized. A real click on a link
  logged `link clicked:` and scrolled to its anchor, and a click that did not
  move left no selection behind. No exceptions. The script raises the window
  without activating it, refuses to send any button event unless
  `WindowFromPoint` says MarkLite's own window is under the pointer, and puts the
  caller's cursor position back. **PASS**
- Regression, **virtual renderer**: `test-virtual` ALL PASS (24), `test-reload`
  ALL PASS (17), `test-gutter` ALL PASS (19), `test-html-comments` ALL PASS (9),
  `test-tabs` ALL PASS (14), `test-toc-search` ALL PASS (39, 1 skipped) on
  `sample-plan.md` and ALL PASS (42) on `stress-large.md`. **PASS**
- Regression, **classic renderer**: `test-tabs` ALL PASS (14),
  `test-html-comments` ALL PASS (9), `test-toc-search` ALL PASS (18, 1 skipped)
  and ALL PASS (19). The classic path keeps MarkView's own selection and hit
  test; nothing in this phase touches it. **PASS**

## Phase 7: Cutover and cleanup
Status: Not started

- [ ] Remove `MARKLITE_VIRTUAL` switch; `VirtualMarkdownView` is the only
  viewer (tabs and welcome). Delete the old code paths: `HeadingControls`,
  `GetVisualDescendants` walks in `DocumentTab`/`MainWindow`/
  `DocumentSearch`, `RebuildTocData`'s positional pairing, pixel-offset
  scroll fields, the `MarkdownViewer` scroll-hook workaround
  (`ScrollHooked`); `DocumentTab` shrinks to text, model, anchor, watcher,
  search, strip controls.
- [ ] `MarkdownTheme.axaml` selectors reviewed: keep `mv|MarkdownViewer`
  scoping (the subclass matches) or retarget to the new class — whichever
  Phase 3 settled on; remove dead classes.
- [ ] Re-read every comment touched by Phases 2–6 for plan references and
  for accuracy against the final code.
- [ ] `tools/verify/run-all.ps1` runs every script against the published exe
  and prints a PASS/FAIL table; `tools/verify/README.md` current.
- [ ] Screenshots in `docs/` refreshed from `testdata/` fixtures where the
  UI changed visibly (selection highlight, scrollbar) — only if they differ.
- [ ] README: Features bullets (virtualized rendering, selection copies
  markdown), "Why" section numbers replaced with the Phase 7 measurement
  table (typical doc, 40 KB plan, 500 KB stress fixture, 3 heavy tabs),
  RAM comparison table row for MarkLite updated.
- [ ] `THIRD-PARTY-NOTICES.md` lists MarkView (MIT) for the vendored
  renderer and any copied snippet (image-size regex, heading text
  extractor); License section of README points at it.

### Verification Plan
- `build/publish.ps1` exit 0, 0 warnings; `dotnet test` green;
  `tools/verify/run-all.ps1` → all PASS on the published exe.
- Final memory table (published exe, after idle trim): sample.md ≤ 70 MB;
  sample-plan.md ≤ 75 MB; stress-large.md < 100 MB after scroll-through;
  3 heavy tabs (stress + plan + sample) < 120 MB after cycling twice;
  first content render on stress-large.md < 300 ms; per-KB slope
  (stress − sample) / (KB difference) < 0.2 MB/KB.
- `tools/scrub-check.ps1` exit 0; `git grep -n "Phase [0-9]" src tools docs`
  → no hits; `git grep -n "plans/" src tools docs` → no hits.

### Phase Summary
_(write when phase completes)_

## Phase 8: Release v1.1.0
Status: Not started

- [ ] `<Version>1.1.0</Version>` in `src/MarkLite/MarkLite.csproj`.
- [ ] Release notes draft (for the GitHub release body): virtualized
  rendering, one live document per window, markdown-source copy, Mermaid
  crash fix, memory table before/after.
- [ ] `build/pack.ps1` with the previous release's files present in
  `releases/` so a delta package is produced; verify Setup.exe, full and
  delta nupkg, portable zip exist and the delta is a fraction of the full.
- [ ] Portable zip smoke run via `tools/verify/run-all.ps1 -Exe <unzipped
  exe>` → all PASS.
- [ ] Hand-off to the user: commit message, then the user runs `git push`
  and `build/release.ps1` (tags and uploads — agent never does either).
  After the user's release: installed v1.0.1 copy with `MARKLITE_DEBUG=1`
  logs the update check finding 1.1.0 and applies the delta.

### Verification Plan
- `pack.ps1` exit 0; `releases/` contains `MarkLite-1.1.0-full.nupkg`,
  `MarkLite-1.1.0-delta.nupkg`, `MarkLite-win-Setup.exe`, portable zip,
  `RELEASES`; delta size < 40 % of full.
- `run-all.ps1` against the packed portable exe → all PASS.
- `tools/scrub-check.ps1` exit 0 on the final tree.
- Post-release (user-run, recorded here from their log): update check line
  shows 1.1.0 found and applied.

### Phase Summary
_(write when phase completes)_

## Final Recap
_(write when all phases complete: summary of the entire piece of work)_

## Deployment Plan
_(write when all phases complete: step-by-step deployment instructions)_
