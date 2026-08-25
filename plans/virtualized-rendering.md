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
Status: Not started

The new control renders `testdata/stress-large.md` fast and flat in memory.
Wired into the app behind a temporary `MARKLITE_VIRTUAL=1` env switch so both
renderers can be measured side by side until the cutover phase; TOC, search,
selection and links are Phases 4–6.

- [ ] `tests/MarkLite.Tests` (xunit, net10.0, references the app project;
  `InternalsVisibleTo` in the app csproj). Runs with `dotnet test`. Tests
  are added per item below.
- [ ] `Rendering/Virtual/MarkdownDocumentModel`: parse with the shared
  pipeline; `Blocks` = top-level `Block` list with `SourceSpan`, source
  slice, and a 64-bit FNV-1a hash of the slice; `Headings` (level, text,
  slug, block index) computed from the AST with a fresh `SlugGenerator` and
  an inline-text extractor equivalent to `HeadingRenderer`'s; `Anchors`
  (slug → block index) covering headings, footnote definitions (`fn-<n>`),
  and explicit `{#id}` attributes; `TocEntries` via `TocEntry.BuildTree`.
  Tests: block count/spans on the fixtures, slug dedup order matches
  MarkView (`-1`, `-2`), setext headings included, hash stable across parses.
- [ ] `Rendering/Virtual/BlockRealizer`: one `AvaloniaRenderer` per document
  (`BaseUri`, `ImageResizeMode`, extensions registered in the same order as
  today, `pipeline.Setup` — all before the first `Write`); `Realize(index)`
  calls `renderer.Write(block)`, moves the delta children out of
  `RootPanel` into a `BlockContainer : Panel` (one per block; zero children
  allowed), sets `Tag = index`. Recycle = drop the container (GC). Also
  applies MarkView's `=WxH` image-size preprocessor to the source before
  parsing (one regex, copied).
- [ ] `Rendering/Virtual/VirtualBlockPanel : Panel`, hosted in the
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
- [ ] Height cache: `Dictionary<(ulong hash, int width), double>` per
  document, survives re-parse (live reload), cleared on font/theme change.
  Test: reload with one changed paragraph keeps every other cached height.
- [ ] `Rendering/Virtual/VirtualMarkdownView : MarkdownViewer` — inherits
  the `:is(mv|MarkdownViewer)` template and both style sheets, sets
  `Content` to the `VirtualBlockPanel` in its constructor and **never** sets
  `Markdown` (guard: setting `Markdown` throws `InvalidOperationException`
  with a message pointing at `Load(text)`). API: `Load(string text)`,
  `Model`, `Panel`, `LinkClicked` re-raised (Phase 6), `Realized` event for
  Phase 5 highlighting. First item of the phase: confirm MarkView's theme
  styles (`markdown-paragraph`, `markdown-code-block`, tables, lists) apply
  inside the subclass on a Debug run; if any are scoped in a way that skips
  us, retarget those selectors in `MarkdownTheme.axaml` instead.
- [ ] Wire-up behind `MARKLITE_VIRTUAL=1`: `CreateViewer` returns the new
  view; `RenderTab` calls `Load`; TOC/search/anchor code paths tolerate the
  new view (no-ops logged) until Phases 4–6. Debug `dump-state` reports
  realized/total blocks, extent, and measured-fraction.
- [ ] Idle trim unchanged; log `render: parsed <n> blocks in <ms>, first
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
_(write when phase completes)_

## Phase 4: Feature parity — TOC, anchors, scroll anchor, live reload
Status: Not started

- [ ] TOC sidebar from `Model.TocEntries` (no visual-tree walk); remove
  `HeadingControls` and the `toc mismatch` path. Current-section tracking:
  nearest heading block index ≤ `FirstVisibleBlock`, refined with the real
  Y of realized heading controls (`Tag` slug lookup) when present.
- [ ] `ScrollToHeading`/`ScrollToAnchor` → `Panel.ScrollToBlock(index, -8)`;
  non-heading slugs resolved through `Model.Anchors`; unknown slug logged.
  After the jump, when the target block was unmeasured, a second pass at
  `Background` priority corrects the offset once heights are real.
- [ ] Scroll preservation switches from pixel offset to `ScrollAnchor`
  (block hash + offset within block): saved on deactivate, restored on
  activate; on live reload the anchor block is found by hash first, then by
  nearest index; the same two-pass restore as today. `DocumentTab.SavedScrollY`
  replaced accordingly; log lines keep the `scroll saved/restored` wording
  and add the block index.
- [ ] Live reload reuses realized containers whose block hash is unchanged
  (moved into the new model's index), so an edit far from the viewport
  changes nothing on screen and an edit inside the viewport re-realizes only
  that block. Heights carried over by hash.
- [ ] Theme and body-font changes: model kept, all containers dropped, height
  cache cleared, re-realize at the same anchor.
- [ ] Update `tools/verify/test-toc-search.ps1` (TOC half) and
  `test-reload.ps1` (new: append/insert/delete-paragraph edits with anchor
  assertions) for the virtual view.

### Verification Plan
- `dotnet test` green (anchor mapping, hash-based reuse).
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
_(write when phase completes)_

## Phase 4A: Line-number gutter (View toggle)
Status: Not started

A left margin showing where each block starts in the source, so what is on
screen can be found again in the editor. Built on the Phase 3 model
(`Markdig.Block.Span` → source line) and the Phase 4 anchor work.

- [ ] `MarkdownDocumentModel`: per-block `StartLine` (and `EndLine`) computed
  once from the source text — a prefix array of newline offsets, binary-searched
  per block span. Test: line numbers of the first and last block on each
  fixture; setext heading reports its text line, not the underline.
- [ ] `Rendering/Virtual/GutterPanel`: drawn beside `VirtualBlockPanel`, sharing
  its scroll offset and per-block Y positions; one right-aligned number per
  realized block at the block's top; recycled with the blocks it labels. Uses
  the code font, muted foreground, fixed width sized to the document's largest
  line number (so the text column does not shift while scrolling).
- [ ] Fenced code blocks get **line-by-line** numbers: the code renderer
  already lays lines out 1:1, so the gutter labels each row from the block's
  start line. Every other block gets a single number at its top; wrapped rows
  stay unnumbered.
- [ ] View menu `Show line numbers` checkbox (default off) + `UserSettings`
  persistence + debug command `gutter <on|off>`; `dump-state` gains
  `gutterVisible` and the first/last visible source line.
- [ ] Layout: the gutter is outside the document's `MaxWidth` centering, so
  turning it on must not reflow the text column (verified by capture diff).
- [ ] `tools/verify/test-gutter.ps1`: toggle on → `dump-state` first visible
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
_(write when phase completes)_

## Phase 5: Search over the model
Status: Not started

- [ ] `Model.BlockText(index)`: lazily computed plain-text projection per
  block by walking Markdig inlines (`LiteralInline`, `CodeInline`,
  `LineBreakInline` → newline, `HtmlEntityInline` → decoded, link/emphasis
  children, `InlineUIContainer`-only inlines → empty), `CodeBlock` lines,
  table cells, nested lists — no list markers, code language labels or
  checkbox glyphs, matching what `DocumentSearch` skips today.
- [ ] `DocumentSearch` rewritten in two layers: **model search** (matches =
  (block, start, length); count, current, next/prev, `ScrollToBlock` for the
  current match, code-block line offset kept for the scroll target) and
  **block highlighting** applied to realized blocks only, reusing the
  existing `Run`-splitting code; highlighting re-runs the substring search
  on the realized block's rendered inline text so the highlight never depends
  on the projection matching the rendered text exactly. Per-block count
  mismatch between the two is logged once per block (debug only).
- [ ] `Realized` event → highlight the new block if a search is active;
  recycled blocks need no undo (they are dropped).
- [ ] Current-match emphasis (orange) follows the current match across
  realization; F3 to an unrealized match scrolls, realizes, then emphasizes
  (same two-pass pattern as anchors).
- [ ] Find bar count text and `find*` debug commands unchanged from the
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
_(write when phase completes)_

## Phase 6: Links, hover cursor, selection, copy
Status: Not started

Replaces MarkView's internal selection layer and hyperlink hit-test, which
the virtual view never instantiates.

- [ ] Hyperlink hit-test: tunnel `PointerPressed`/`PointerReleased` on the
  view; find the `TextBlock` under the pointer among realized containers,
  `TextLayout.HitTestPoint` → character index → walk `Inlines` (recursing
  `Span`s) to the `MarkdownHyperlink` covering that index → `NavigateUri` →
  existing `MarkLiteHyperlinkCommand`. Image links (`InlineUIContainer`
  inside a hyperlink) handled by walking up from the hit `Image`. Hand
  cursor on hover via the same test on `PointerMoved` (throttled to the
  hovered block).
- [ ] Selection model: `anchor`/`focus` = (block index, character offset in
  the block's rendered text); drag updates focus; dragging past the top or
  bottom edge autoscrolls on a `DispatcherTimer` (realizing blocks as they
  enter the window); Ctrl+A selects (0,0)–(last, end); Escape/click clears.
- [ ] Selection adorner: one overlay control over the panel drawing
  `TextLayout.HitTestTextRange` rects for realized blocks inside the range;
  fully covered unrealized blocks draw nothing (they are off-screen by
  definition). Uses the app's existing selection brush.
- [ ] Copy (Ctrl+C, context or menu item if one exists today): markdown
  source slice. Endpoints map through inline `SourceSpan`s when the block's
  inline at that offset is a `LiteralInline`/`CodeInline` (character
  precise); otherwise fall back to the block boundary. Ctrl+A + Ctrl+C =
  the whole file text. Code blocks keep their own `SelectableTextBlock`
  copy (already there) and are treated as whole blocks by the document
  selection.
- [ ] Debug commands `select-all`, `copy`, `select <b1> <o1> <b2> <o2>`;
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
_(write when phase completes)_

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
