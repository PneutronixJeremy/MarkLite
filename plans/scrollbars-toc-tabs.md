# Wide scroll bars + resizable contents sidebar + tab drag reorder

Three View/UI features, then a v1.3.0 release: an option that keeps every scroll
bar at its expanded width instead of collapsing to Fluent's thin idle strip (on
by default); a contents sidebar whose width the reader can drag and which is
remembered; and tabs that can be reordered by dragging them along the strip.

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
When you write prose, tables or examples under a `- [ ]` item, indent them **two
spaces** — the item's content column. Six spaces (aligning under the text after
the checkbox) is an indented code block: after a blank line it silently renders
your paragraph or table as raw source.

Repo rules that apply to every phase (details in `AGENTS.md`):

- Verification drives the **published AOT exe** (`build/publish.ps1` →
  `publish/MarkLite.exe`) through `tools/verify/*.ps1`; no input injection. The
  pointer plumbing of a drag (splitter, tab) is therefore **not** scriptable —
  each phase gives the user a short manual acceptance list for that part, and
  everything else asserts over the debug command channel and `dump-state`.
- `pwsh -NoProfile -File tools/scrub-check.ps1` must exit 0 before any commit.
- C#: brace every control-flow body; long comments as `/*  … */` blocks.
- Commit subjects: `Area > Description. [w/ Claude]`, no phase numbers.

Decisions taken with the user (2026-08-29):

| Topic | Decision |
|---|---|
| "Wide" scroll bar | Fluent's expanded look (16 px thumb + visible track) kept permanently — `ScrollViewer.AllowAutoHide = false`. No thickness change beyond Fluent's `ScrollBarSize` (16). |
| Option default | **On.** Existing installs get wide bars after the update. `View > Wide scroll bars` checkbox. |
| Sidebar width | Persisted (registry, like the other settings). Clamped 140–600 px, default 250. Double-click on the splitter resets to 250. |
| Tab reorder | Live reorder while dragging (tabs shuffle when the pointer crosses a neighbour's midpoint); the dragged tab becomes active on press, browser-style. Drag only — no keyboard reorder shortcut. Order persists to the stored session. |
| Old plan | `plans/session-restore.md` (Complete, v1.2.0 shipped) deleted with this plan's first commit. |
| Tab strip overflow (added 2026-08-29, during Phase 1 acceptance) | Tabs **wrap onto further rows** (`WrapPanel`) instead of scrolling. The wide-bar option had put a 16 px scroll bar under the strip; the user preferred layered rows to any bar there. Consequence for Phase 3: no `BringIntoView`, and the reorder test must be by pointer position over items (rows), not by horizontal midpoints alone. |
| Release | Phase 4 ships v1.3.0. |

## Phase 1: View > Wide scroll bars
Status: Complete

How it works: Fluent's `ScrollBar` collapses its thumb with a scale transform
whenever `AllowAutoHide` is true and the pointer is away; `IsExpanded` is the
pseudo-state that undoes it. `ScrollViewer.AllowAutoHide` is a styled (attached)
property template-bound onto both bars, so one style on `ScrollViewer` covers the
document, the sidebar, the tab strip and every code block's horizontal scroller.
**Correction found in verification:** layout *does* change. Fluent's
`ScrollViewer` template spans the content presenter under the bars only while
`AllowAutoHide` is true (`^[AllowAutoHide=True] /template/ ScrollContentPresenter`
→ `ColumnSpan/RowSpan=2`); a permanent bar gets its own `ScrollBarSize` column, so
the option costs the document 16 DIP of width (no height), text never runs under
the bar, and the toggle re-wraps exactly like a 16 px window resize — the panel's
existing anchor correction keeps the reader on the same block. An overflowing
code block's horizontal bar likewise takes a 16 px row below the code.

- [x] `UserSettings.WideScrollBars` (`bool?`, DWord `WideScrollBars` under
  `HKCU\Software\MarkLite`, same shape as `ShowLineNumbers`). Null = never set =
  **on**.
- [x] `MainWindow.axaml`: `<MenuItem Name="WideScrollBarsItem" Header="_Wide scroll bars" ToggleType="CheckBox" IsChecked="True" Click="OnWideScrollBarsClicked" />`
  under View, after Show line numbers. A window-level style keyed on a class on
  the window itself:

  ```xml
  <Style Selector="Window.WideScrollBars ScrollViewer">
      <Setter Property="AllowAutoHide" Value="False" />
  </Style>
  ```

  Toggling `Classes` on the window flips every descendant `ScrollViewer`,
  template children included, with no per-viewer bookkeeping. The window
  carries `Classes="WideScrollBars"` in XAML so the default is on from the
  first frame.
- [x] `MainWindow.axaml.cs`: `SetWideScrollBars(bool wide)` adds/removes the
  `WideScrollBars` class, syncs the menu check, writes `UserSettings`, logs
  `wide scroll bars: on|off`. Read the setting in the constructor **before the
  first render** next to the line-numbers block (log `wide scroll bars restored:
  …` only when a stored value exists, matching the neighbours).
- [x] `DebugCommands.cs`: `wide-scrollbars on|off` → `SetWideScrollBars`.
  `dump-state` gains `"wideScrollBars":true|false` and
  `"scrollBarExpanded":true|false` — the active viewer's vertical `ScrollBar`
  (`Scroller.GetVisualDescendants().OfType<ScrollBar>()` where
  `Orientation == Vertical`) reporting `IsExpanded`; `false` when there is no
  document. With auto-hide off the bar sets `IsExpanded` permanently, so this is
  the assertable signal. Also added `"viewportWidth"` per tab (Phase 2 planned
  it; Phase 1's 16 px assertion needed it first).
- [x] `tools/verify/test-scrollbars.ps1`: generates a plain-paragraph fixture
  (300 one-line paragraphs, no code fences — so the only scroll bar in the
  capture is the document's vertical one) and asserts:
  1. fresh launch: `wideScrollBars` true, `scrollBarExpanded` true, no
     `restored` log line. The value lives in the shared `HKCU\Software\MarkLite`
     key (not the instance-scoped session key `Start-MarkLite` clears), so the
     script removes it itself before launch;
  2. `wide-scrollbars off` → `wideScrollBars` false and, once the hide delay
     runs out (polled, up to 6 s), `scrollBarExpanded` false;
  3. a full-resolution capture in each state: `blocks` equal, `viewportWidth`
     differs by exactly 16 (±0.5), `viewport` height equal, `scrollY` 0 at the
     top; every differing pixel is right of the sidebar and the rightmost strip
     did change (the wide thumb). Then a reader parked eight pages down stays
     on the same `firstVisibleBlock` through off→on→off→on;
  4. `wide-scrollbars off`, `Stop-MarkLite`, relaunch with the setting kept →
     still false, log `wide scroll bars restored: off`; then `wide-scrollbars on`
     and leave it on.
- [x] `tools/verify/README.md`: entry for `test-scrollbars.ps1`. `run-all.ps1`
  uses an **explicit ordered list**, not a glob — added after `test-gutter.ps1`.
- [x] Delete `plans/session-restore.md` (approved by the user).
- [x] (Added at acceptance.) Tab strip: `ScrollViewer` + `StackPanel` replaced by
  a `WrapPanel` (`ItemSpacing="2" LineSpacing="2"`); tabs wrap onto further rows
  and the strip never shows a scroll bar. `MainWindow.axaml.cs` looks the strip
  up as `WrapPanel`.
- [x] `tools/scrub-check.ps1` exit 0.

### Verification Plan
- `pwsh -NoProfile -File build/publish.ps1` exit 0. **Result: exit 0.**
- `pwsh -NoProfile -File tools/verify/test-scrollbars.ps1` → all PASS, exit 0.
  **Result: ALL PASS (25 checks).**
- `pwsh -NoProfile -File tools/verify/run-all.ps1` → every row PASS (no
  regression from the window-level style; `test-gutter.ps1` in particular
  still finds its diff inside one gutter strip). **Result: ALL PASS (10 scripts;
  `test-gutter` 13.2 s, `test-scrollbars` 14.2 s, rest under 7 s).**
- User acceptance (manual, not scriptable): with the option on, the document's
  bar shows its full thumb and track while the mouse is elsewhere, and the
  thumb still drags; the horizontal bar of an overflowing code block is wide
  too (and sits below the code, not over its last line); unchecking the menu
  item brings the thin idle bar back immediately and the text column widens by
  16 px without the reader losing their place.

### Phase Summary
- Setting `UserSettings.WideScrollBars` (shared key, DWord, null = on); menu
  `View > Wide scroll bars` (checked by default); window class `WideScrollBars`
  set in XAML, style `Window.WideScrollBars ScrollViewer` → `AllowAutoHide=False`.
  `ApplyWideScrollBars` (class + menu check) is separate from
  `SetWideScrollBars` (also persists + logs) so the constructor restore does not
  rewrite the registry. `WideScrollBarsOn` reads the class.
- Debug: `wide-scrollbars on|off`; `dump-state` → `wideScrollBars`,
  `scrollBarExpanded`, and per-tab `viewportWidth`.
- Key decision forced by the run: the plan assumed the toggle was layout-free.
  It is not (see the corrected "How it works" above) — the permanent bar takes a
  16 DIP column, which is the *right* outcome (text never under the bar), so the
  behaviour was kept and the test now asserts the 16 px width delta plus anchor
  preservation instead of "nothing moved". The XAML comment states the real
  mechanism.
- Side effect for Phase 2: `viewportWidth` already exists in `dump-state`.
- Side effect to know: an overflowing code block with the option on is 16 px
  taller (horizontal bar row). Not a bug; Fluent's permanent-bar layout.

## Phase 2: Resizable contents sidebar
Status: Not started

Today `TocPanel` is a `Border` with a hard `Width="250"` in
`Grid ColumnDefinitions="Auto,*"`. A `GridSplitter` needs a sized column to
drive, so the width moves from the Border to a named `ColumnDefinition`.

- [ ] `MainWindow.axaml`: columns become
  `TocColumn` (`Width="250" MinWidth="140" MaxWidth="600"`), a splitter column
  (`Auto`) and `*` for `ViewerHost` (now `Grid.Column="2"`). `TocPanel` loses
  `Width` and its right `BorderThickness` (the splitter draws the rule).
  `<GridSplitter Name="TocSplitter" Grid.Column="1" Width="5" ResizeDirection="Columns" ResizeBehavior="PreviousAndNext" Background="{DynamicResource MdTableBorder}" IsVisible="False" />`
  — a 5 px grab strip painted as the existing 1 px rule colour.
- [ ] `UserSettings.TocWidth` (`int?`, DWord `TocWidth`). Read in the
  constructor, clamped to 140–600, applied to `TocColumn.Width`; log
  `toc width restored: <n>` when a stored value exists.
- [ ] `MainWindow.axaml.cs`:
  - `SetTocWidth(double width, bool persist)`: clamps, sets
    `TocColumn.Width = new GridLength(width)`, writes `UserSettings.TocWidth`
    when `persist`, logs `toc width: <n>`.
  - `TocSplitter.DragCompleted` → `SetTocWidth(TocColumn.ActualWidth, persist: true)`
    (`GridSplitter` derives from `Thumb`, so the event exists).
  - `TocSplitter.DoubleTapped` → `SetTocWidth(250, persist: true)`.
  - `UpdateTocPanelVisibility()` also drives the column: hidden ⇒
    `TocColumn.MinWidth = 0`, `TocColumn.Width = new GridLength(0)`, splitter
    hidden; shown ⇒ `MinWidth = 140`, `Width` back to the remembered value,
    splitter visible. Keep the remembered width in a field (`_tocWidth`) so
    hide/show round-trips do not lose it.
- [ ] `DebugCommands.cs`: `toc-width <px>` → `SetTocWidth(px, persist: true)`,
  the same path a splitter release takes. `dump-state` gains
  `"tocVisible":true|false` (the Border's `IsVisible`), `"tocWidth":<n>`
  (`TocPanel.Bounds.Width`, 0 when hidden) and `"viewportWidth":<n>` on each
  tab (`Scroller.Viewport.Width`), so a check can see the document give the
  pixels up.
- [ ] `tools/verify/test-toc-width.ps1` on `testdata/sample-plan.md` (has
  headings, so the sidebar shows):
  1. fresh launch: `tocVisible` true, `tocWidth` 250 ±1;
  2. `toc-width 400` → `tocWidth` 400 ±1 and the active tab's `viewportWidth`
     shrank by 150 ±2; `firstVisibleBlock` unchanged (the resize re-wraps, the
     anchor holds — same guarantee `test-resize.ps1` asserts);
  3. clamps: `toc-width 50` → 140; `toc-width 900` → 600;
  4. `toc-width 400`, restart with the settings kept → 400 (persisted);
  5. `toc` toggle off (existing `toc` command with no argument, or add
     `toc-toggle` if the parser needs it) → `tocVisible` false, `tocWidth` 0,
     `viewportWidth` grew by 400 + splitter; toggle on → 400 again;
  6. `toc-width 250` to leave the machine as found; capture at 400 and at 250
     saved to `-CaptureDir` for the eye.
- [ ] `tools/verify/README.md` entry. `test-toc-search.ps1` and
  `test-virtual.ps1` re-run — the sidebar's contents and the `toc <n>` landing
  (8 px below the viewport top) must not care which column holds it.
- [ ] `tools/scrub-check.ps1` exit 0.

### Verification Plan
- `build/publish.ps1` exit 0; `tools/verify/test-toc-width.ps1` → all PASS.
- `tools/verify/run-all.ps1` → every row PASS.
- Docs screenshots unaffected: default width is still 250, so
  `docs/screenshot-*.png` need no recapture.
- User acceptance (manual): dragging the strip between sidebar and document
  resizes live and stops at 140 / 600; the width is back after closing and
  reopening MarkLite; double-click on the strip returns to 250; Ctrl+T twice
  brings the same width back; the cursor changes over the strip.

### Phase Summary
_(write when phase completes)_

## Phase 3: Drag tabs to reorder
Status: Not started

`TabStrip` is a `WrapPanel` of `Border.TabItem` (rows, no scrolling — see the
decisions table); `_tabs` is
the order everything else reads (`SaveSession`, Ctrl+Tab, `dump-state`,
close-tab's neighbour choice). A move is therefore one method that reorders both
in lockstep, and the drag is pointer plumbing on top of it. The name button
captures the pointer on press, so the item's handlers register with
`handledEventsToo: true` (and the press with `RoutingStrategies.Tunnel`) to see
the events anyway.

- [ ] `MainWindow.axaml.cs`: `MoveTab(DocumentTab tab, int toIndex)` — clamps,
  no-op when unchanged, moves the entry in `_tabs` and the `StripItem` in
  `TabStrip.Children` (`RemoveAt` + `Insert`, never rebuilt — the items hold
  live event subscriptions), `SaveSession()`, logs
  `tab moved '<name>' <from> -> <to>`.
- [ ] Drag state in `CreateTab` (per item, closures like the existing middle-
  click handler): on left press not originating from the close button →
  remember the pointer and its X in `TabStrip` coordinates, and
  `ActivateTab(tab)` (browser-style; the button's later `Click` finds the tab
  already active and is a no-op). On move with the button held: once |ΔX| > 6 px
  the item enters the drag (`Classes.Add("TabItemDragging")`; style: the
  active-tab background plus a 1 px `MdAccent` bottom border, so the lifted tab
  reads as lifted). While dragging: find the sibling whose `Bounds` (in
  `TabStrip` coordinates) contains the pointer — this is what makes rows work,
  a pointer on the second row lands on a second-row tab — and, when it is a
  neighbour crossed past its horizontal midpoint (left of the previous
  sibling's, right of the next sibling's), `MoveTab(tab, thatIndex)`; a pointer
  over a non-adjacent tab moves straight to that index. No `BringIntoView`: the
  strip wraps, nothing scrolls. Release or `PointerCaptureLost` ends the drag,
  removes the class, and logs `tab drag ended` — one `SaveSession` already
  happened per move. A press that never crossed the threshold changes nothing
  but the active tab.
- [ ] Close button unaffected: pressing ✕ never starts a drag (check
  `e.Source` ancestry for the `TabClose` button); middle-click close as before.
- [ ] `DebugCommands.cs`: `move-tab <from> <to>` → `MoveTab(_tabs[from], to)`;
  out-of-range indices answer `ignored (n tabs)` and change nothing. The
  `tabs[]` array in `dump-state` already carries `index`, `name`, `active`.
- [ ] `tools/verify/test-tab-order.ps1`: opens three fixtures (a, b, c; c
  active, as `test-tabs.ps1` does) and asserts:
  1. `move-tab 2 0` → names `c, a, b`, `activeTab` 0 (the active tab moved and
     is still the active one), each tab's `chars` travelled with its name;
  2. `move-tab 0 2` → `a, b, c` again; `move-tab 1 1` → log says unchanged;
     `move-tab 7 0` → `ignored`, order intact;
  3. `move-tab 0 2` → `b, c, a`; `sessionCount` 3, log
     `session saved: 3 tabs, active 2`; `Stop-MarkLite`; relaunch with
     `-KeepSession` (the `test-session.ps1` pattern) → tabs come back as
     `b, c, a` with `a` active — the order is what the session stores;
  4. `close-tab` on the active tab lands on its right-hand neighbour by the
     **new** order (existing behaviour, now over a reordered list).
- [ ] `tools/verify/README.md` entry (and a sentence under the ground rules:
  the tab drag joins the splitter drag in the "verified by hand" set, with
  `move-tab` as the scripted stand-in).
- [ ] `tools/scrub-check.ps1` exit 0.

### Verification Plan
- `build/publish.ps1` exit 0; `tools/verify/test-tab-order.ps1` → all PASS.
- `tools/verify/run-all.ps1` → every row PASS (`test-tabs.ps1` and
  `test-session.ps1` exercise the untouched paths around `_tabs`).
- User acceptance (manual): drag a middle tab to either end and back — tabs
  shuffle as the pointer crosses them, the strip scrolls when there are more
  tabs than fit, releasing leaves the order as shown; a plain click still
  switches tabs; ✕ still closes without ever starting a drag; Ctrl+Tab cycles
  in the new order; the new order is back after a restart.

### Phase Summary
_(write when phase completes)_

## Phase 4: Release v1.3.0
Status: Not started

- [ ] README: Features bullets — `View > Wide scroll bars` (on by default),
  the sidebar is resizable and remembers its width, tabs reorder by drag.
  Adjust the "Contents sidebar (Ctrl+T)" and "Tabs" bullets rather than adding
  three new ones where a clause will do.
- [ ] `docs/release-notes/v1.3.0.md` — short, end-user facing (what changed for
  them, not how), `## Added` / `## Fixed` sections as v1.2.0's.
- [ ] `<Version>1.3.0</Version>` in `src/MarkLite/MarkLite.csproj`.
- [ ] `tools/scrub-check.ps1` exit 0.
- [ ] **Commit and the user pushes BEFORE packing** (`docs/RELEASING.md`; the exe
  records HEAD as its source revision — v1.1.0 got this wrong). The agent never
  pushes, tags or uploads.
- [ ] `build/pack.ps1` with the 1.2.0 files present in `releases/` so a delta is
  produced; confirm Setup.exe, full and delta nupkg, portable zip and
  `RELEASES`; delta < 40 % of full. Never repack after the checks below.
- [ ] Portable zip smoke run: `run-all.ps1 -Exe <unzipped>/current/MarkLite.exe`
  → all PASS.
- [ ] Hand-off: the user runs `build/release.ps1`.

### Verification Plan
- `pack.ps1` exit 0; `releases/` holds the 1.3.0 full and delta nupkg,
  `MarkLite-win-Setup.exe`, the portable zip and `RELEASES`; delta < 40 % of
  full.
- `run-all.ps1` against the packed portable exe → all PASS (now 12 scripts:
  the nine existing plus `test-scrollbars`, `test-toc-width`,
  `test-tab-order`).
- The packed exe's `ProductVersion` git hash matches the commit the `v1.3.0`
  tag will point at.
- `tools/scrub-check.ps1` exit 0 on the final tree.
- Post-release (user-run): an installed 1.2.0 copy takes the delta and comes
  back with wide scroll bars on, its tabs restored, sidebar at 250.

### Phase Summary
_(write when phase completes)_

## Final Recap
_(write when all phases complete: summary of the entire piece of work)_

## Deployment Plan
_(write when all phases complete: step-by-step deployment instructions)_
