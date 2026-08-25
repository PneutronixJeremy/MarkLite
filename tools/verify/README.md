# Verification scripts

Scripted, input-free checks that phases assert against. They drive the
**published AOT exe** (`build/publish.ps1` → `publish/MarkLite.exe`), not a
Debug/JIT run, because several past bugs only existed after AOT + trimming.

Ground rules:

- **No input injection.** No SendKeys, no `mouse_event`, no focus stealing.
  Scripts talk to the app over its debug command channel (a `cmd:` message on
  the single-instance pipe, enabled only when `MARKLITE_DEBUG=1`), read its
  stderr log, use UI Automation patterns, and capture with `PrintWindow`.
- Every script exits non-zero on the first failed assertion and prints
  `PASS`/`FAIL` lines so a future `run-all.ps1` can tabulate them.

## Scripts

- **`common.ps1`** — dot-sourced by the rest. Launches the published exe with
  `MARKLITE_DEBUG=1` and `MARKLITE_INSTANCE=verify` (its own single-instance
  group, so a MarkLite the user already has open is never sent test documents
  and never answers commands), waits for the first render, and parks the window
  on the non-primary display at a fixed 1400x1000 with `SWP_NOACTIVATE` — no
  focus stealing, and a fixed framebuffer size so memory numbers compare across
  machines. Provides `Send-Cmd`, `Wait-Log`, `Get-State`, `Save-WindowCapture`
  (PrintWindow), `Stop-MarkLite` (WM_CLOSE, kill as fallback) and the
  `Assert-*` / `Exit-WithSummary` helpers.
- **`measure-memory.ps1 -Files a.md,b.md [-Exe path] [-Label text]`** — opens
  the files as tabs and records working set at each stage (first render, each
  further tab, scroll each document end-to-end, cycle tabs twice, forced
  collect), then prints a Markdown table. Numbers come from the app's own
  `Process.WorkingSet64` via `dump-state`.
- **`test-tabs.ps1 [-Exe path] [-Files a.md,b.md,c.md]`** — asserts that a
  secondary launch hands its file to the running instance, that every file gets
  a tab, that each tab keeps its own scroll offset across switches, that
  leaving a tab drops its rendered tree, that five round-trips through a
  document holding a Mermaid diagram raise nothing, and that closing every tab
  returns to the welcome page. Prints the slowest switch render time.
- **`test-html-comments.ps1 [-Exe path] [-File doc.md] [-CaptureDir dir]`** —
  View > Show HTML comments. Uses find-in-document as the probe (it searches
  rendered text, so a findable string is on screen): comments findable with the
  toggle on, gone with it off, and a raw `<img>` tag findable in neither.
  Captures both states and checks the setting survives a restart, then leaves
  it back on.
- **`test-virtual.ps1 [-Exe path] [-File doc.md] [-CaptureDir dir]`** — the
  virtualizing viewer (sets `MARKLITE_VIRTUAL=1` for the app it launches):
  only a small fraction of the document is realized at any moment, the scroll
  extent still covers the whole document, jumps and `scroll-end` realize their
  target, the contents sidebar is complete from the parsed model, the working
  set stays under 100 MB after scrolling a 500 KB document end to end, a
  comment-visibility toggle rebuilds every control without re-parsing and
  leaves the reader on their block, a tab switch brings back the same block and
  the same offset into it, and a window resize keeps the reader near the block
  they were on.
- **`test-reload.ps1 [-Exe path]`** — live reload under the virtualizing viewer
  (sets `MARKLITE_VIRTUAL=1`). Generates its own 1200-block document, because
  the assertions need block numbering that is knowable from outside the app:
  every block is one line, blank-line separated, so block *k* is line *2k* and
  no two blocks share text. Parks the reader mid-document, then rewrites the
  file three times — 50 paragraphs inserted at the top, the paragraph under the
  reader rewritten, that paragraph deleted — and asserts the reader stays on the
  same paragraph each time and that only genuinely changed blocks are rebuilt
  (`reload: reused <n> of <m> containers, <a> of <b> blocks aligned`). Edits are
  file writes; the app's own watcher notices them.
- **`test-gutter.ps1 [-Exe path] [-File doc.md] [-CaptureDir dir]`** — the
  line-number gutter (sets `MARKLITE_VIRTUAL=1`). The strip is reserved on both
  sides of the document whether the numbers show or not, so the toggle has to be
  free: a full-resolution capture diff between the two states must find every
  differing pixel inside one strip's width, with the block count, realized set,
  extent, scroll offset and visible lines all unchanged. The numbers are checked
  against the source file rather than a screenshot — `dump-state` reports the
  first and last visible source line and `targetBlockLine`, so a `toc <n>` jump
  can be confirmed to have landed on a line that really is a heading (ATX or
  setext). On a document over 1000 blocks it also re-checks the working set and
  the realized fraction with the numbers showing, because the gutter draws and
  must never build controls. Leaves the setting off.
- **`test-toc-search.ps1 [-Exe path] [-File doc.md] [-Term word]`** — contents
  sidebar (`toc <n>` scrolls and becomes the current section; under the
  virtualizing viewer it also lands the heading 8 px below the viewport top,
  after the panel has corrected its own estimate; `anchor <slug>` resolves, and
  so does a footnote slug on a document that defines one) and find-in-document
  (match count equals a source count, `find-next` advances, closing clears
  matches and highlights). Under the virtualizing viewer the search half also
  asserts what only a model-backed search can do: the count equals a count over
  the app's own text projection (`dump-text`) **exactly**, matches outside the
  realized window are counted but not highlighted, every match stepped to ends
  up realized, highlighted and inside the viewport, and having a search active
  costs under 8 MB of working set.

Pass list parameters comma-separated (`-Files a.md,b.md`): with `pwsh -File`,
space-separated tokens do not bind to an array parameter.

### Counting matches: two comparisons, two meanings

`test-toc-search.ps1` compares the app's match count with two other counts.

The count over the **source file** is an approximation. The app searches what a
reader can see, so the script first strips HTML comments and link/image
destinations — an anchor such as `(#station-overview)` is never drawn and must
not be counted. Terms are chosen to appear only in prose; a mismatch on them is
a real regression, not a counting artefact.

The count over the app's **own text projection** is not an approximation. The
`dump-text` command writes `MarkdownDocumentModel.BlockText` for every block to
`marklite-blocktext.txt` in the temp directory — the very text the search ran
over — so that comparison is asserted as exact equality. It is the check that
catches the projection drifting away from what the renderers draw: the
projection is a description of their behaviour (chrome, dropped HTML, diagrams
and formulas contribute nothing; decoded entities and code lines do), and the
renderers are the authority. The app logs
`search: block <n> projects <x> matches, renders <y>` when a realized block
disagrees, which is the other half of the same check.

## Fixtures

- `testdata/sample.md`, `testdata/sample-plan.md` — the everyday documents.
- `testdata/stress-large.md` — ~530 KB generated stress fixture (300 headings,
  1500 paragraphs, 150 lists, 40 tables, 60 code fences, 2 mermaid diagrams,
  math, footnotes, 240+ in-document anchor links). Regenerate with
  `pwsh -NoProfile -File tools/gen-stress-fixture.ps1`; it is deterministic, so
  a regenerated file is byte-identical and `git status` stays clean.
