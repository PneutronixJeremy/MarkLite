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
  a tab, that each tab keeps its own scroll offset across switches, and that
  closing every tab returns to the welcome page.
- **`test-html-comments.ps1 [-Exe path] [-File doc.md] [-CaptureDir dir]`** —
  View > Show HTML comments. Uses find-in-document as the probe (it searches
  rendered text, so a findable string is on screen): comments findable with the
  toggle on, gone with it off, and a raw `<img>` tag findable in neither.
  Captures both states and checks the setting survives a restart, then leaves
  it back on.
- **`test-toc-search.ps1 [-Exe path] [-File doc.md] [-Term word]`** — contents
  sidebar (`toc <n>` scrolls and becomes the current section, `anchor <slug>`
  resolves) and find-in-document (match count equals a source count, `find-next`
  advances, closing clears).

Pass list parameters comma-separated (`-Files a.md,b.md`): with `pwsh -File`,
space-separated tokens do not bind to an array parameter.

### Counting matches against the source

`test-toc-search.ps1` compares the app's match count with a plain count over
the source file. The app searches the RENDERED text, so the source count first
strips HTML comments and link/image destinations — an anchor such as
`(#station-overview)` is never drawn and must not be counted. Terms are chosen
to appear only in prose; a mismatch on them is a real regression.

## Fixtures

- `testdata/sample.md`, `testdata/sample-plan.md` — the everyday documents.
- `testdata/stress-large.md` — ~530 KB generated stress fixture (300 headings,
  1500 paragraphs, 150 lists, 40 tables, 60 code fences, 2 mermaid diagrams,
  math, footnotes, 240+ in-document anchor links). Regenerate with
  `pwsh -NoProfile -File tools/gen-stress-fixture.ps1`; it is deterministic, so
  a regenerated file is byte-identical and `git status` stays clean.
