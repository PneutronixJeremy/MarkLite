# Version line in Help + reopen last session + bring to front

Small features requested together (user, 2026-08-25), shipping as v1.2.0: a
version line in the Help menu; Notepad-style session restore — the files that
were open when MarkLite closed come back when it launches, which matters most
across an auto-update restart; and a handoff that actually raises the window.
A fourth was added on 2026-08-26: a window resize must keep the reader on the
same place in the document instead of on the same pixel offset.

Decisions fixed with the user on 2026-08-25 (do not re-open):
- **Version line is static**, not a clickable About dialog: a greyed
  (`IsEnabled="False"`) item at the top of the Help menu, above a separator and
  the existing "Check for updates".
- **Launched with a file argument**: restore the session **and** open the
  argument on top of it, as the active tab. Notepad's behaviour — the session is
  the app's state, the argument is an addition.
- **Session restore is a setting**: Options > Reopen last session, persisted,
  **default on**. Same pattern as the View toggles.
- **Opening a file in a running MarkLite must raise the window**, not flash the
  taskbar button.
- Ships as **v1.2.0** (new feature, not a fix).

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
- This file is committed and must pass `tools/scrub-check.ps1` (a pre-commit
  hook enforces it). No absolute local paths, no personal details, no
  credentials. Assume the repo is public.
- Numbers quoted as verification results come from the **published AOT exe**
  (`build/publish.ps1`), never from a Debug/JIT run.
- No input injection (SendKeys, mouse_event, focus stealing) in scripts. Use the
  debug command channel, UIA InvokePattern and `PrintWindow`. If a check
  genuinely needs injected input, warn the user and wait for an explicit go.
- Commit subjects: `Area > Subarea > Description. [w/ Claude]` (no `MarkLite >`
  prefix). Never `git push`, never tags, never `--amend`.

Code map (as of the start of this plan):
- `src/MarkLite/MainWindow.axaml` — menu markup. Help menu is the last item and
  currently holds one entry, "Check for updates".
- `src/MarkLite/MainWindow.axaml.cs` — the constructor takes `args` and restores
  persisted settings (comments, line numbers, body font) before opening
  `args[0]` or showing the welcome page; `OpenFile`, `CreateTab`, `ActivateTab`,
  `CloseTab` own the tab list; a `Closed` handler disposes tabs and calls
  `_updateService.ApplyOnExit()`.
- `src/MarkLite/UserSettings.cs` — every persisted setting, in
  `HKCU\Software\MarkLite`. Registry rather than a file so the Velopack
  uninstall hook removes it all in one sweep. Each setting is a nullable
  property; null means "never set" and the caller applies the default.
- `src/MarkLite/DocumentTab.cs` — `FilePath`, `SavedScroll` (a `ScrollRestore`
  of Y + block hash + block index + offset within the block) and `CaptureScroll`
  / `RestoreScroll`.
- `src/MarkLite/UpdateService.cs` — `RestartToApply` calls Velopack's
  `ApplyUpdatesAndRestart`; `ApplyOnExit` calls `WaitExitThenApplyUpdates`.
- `src/MarkLite/SingleInstance.cs` — pipe name includes `MARKLITE_INSTANCE`
  when set, so verification runs form their own instance group.
- `tools/verify/` — scripted checks; `run-all.ps1` tabulates them.

## Phase 1: Version line in the Help menu
Status: Complete

- [x] A `MarkLiteVersion` helper (new small static, or a member of an existing
  one) returning the display version as a plain string: the assembly's
  `AssemblyInformationalVersionAttribute` truncated at the first `+`, so
  `1.1.0+78f542c…` reads `1.1.0`.
- [x] Help menu gains a **disabled** first item bound to it, then a separator,
  then the existing "Check for updates":

  ```xml
  <MenuItem Header="_Help">
      <MenuItem Name="VersionItem" Header="MarkLite" IsEnabled="False" />
      <Separator />
      <MenuItem Header="_Check for updates" Click="OnCheckForUpdatesClicked" />
  </MenuItem>
  ```

  Header set from code in the constructor. No mnemonic on the version item — it
  is not reachable by keyboard and a mnemonic would imply it is.
- [x] **Do not use `UpdateService.CurrentVersion` for this.** It comes from
  Velopack's `UpdateManager` and returns `"0.0.0-dev"` for any copy that is not
  installed, so the portable zip and every dev run would show a fake version.
  The assembly attribute is correct in installed, portable and dev builds alike.
- [x] Confirm the attribute survives **AOT + trimming**. Assembly-level custom
  attributes are normally preserved, but this must be checked on the published
  exe, not asserted. If it comes back null, fall back to
  `Assembly.GetName().Version` rendered as three parts (`1.1.0`), and record
  which one shipped.
- [x] `MARKLITE_DEBUG` log line on startup naming the version, so a script can
  assert on it without reading a menu.

### Verification Plan
- `build/publish.ps1` exit 0, 0 warnings; `dotnet test` green.
- On the published exe: the version reported by the app equals `<Version>` in
  `src/MarkLite/MarkLite.csproj` — asserted from the startup log line, and
  cross-checked against the menu item's UIA `Name` (read-only inspection; no
  clicking, no focus stealing).
- The same exe run from the **unzipped portable zip** reports the same version
  (this is the case `UpdateService.CurrentVersion` would have got wrong).
- Menu item reports `IsEnabled = false` through UIA.

### Phase Summary
`AppVersion.Display` (new `src/MarkLite/AppVersion.cs`) reads the assembly's
`AssemblyInformationalVersionAttribute` and cuts it at the first `+`, with a
`GetName().Version` fallback that logs a line when it is used. The Help menu's
first item is disabled and its header is set from the constructor
(`MarkLite {version}`); `Program.Main` logs `version <x.y.z>` right after the
Velopack hook, so every launch — including `--cmd` sends — states it.

Verification found that a submenu item is **not in the UI Automation tree
until the menu is opened**, so the planned UIA cross-check would have needed a
popup and the focus that comes with it. Instead `dump-state` gained a
`"version"` field that reports the *menu item's own header text*, which is the
same assertion without touching the UI.

Results, all on the published AOT exe:
- `build/publish.ps1` exit 0, no warnings; `dotnet test` 62/62 green.
- Startup log `version 1.1.0` == `<Version>` in `MarkLite.csproj`; `dump-state`
  `"version":"MarkLite 1.1.0"`.
- The "informational attribute missing" fallback line never appears, so the
  attribute **survives AOT + trimming** and the fallback did not ship.
- A copy of the publish output run from outside any install (the portable
  layout) reports the same `1.1.0` while logging "not an installed copy" —
  the case `UpdateService.CurrentVersion` would have called `0.0.0-dev`.
- `IsEnabled="False"` is markup, not runtime state; reading it back through UIA
  needs the Help popup open, which is a focus-stealing action and was not run.
- `run-all.ps1`: ALL PASS (7 scripts).

## Phase 2: Reopen last session
Status: Not started

- [ ] `UserSettings` gains:
  - `RestoreSession` (bool?, DWORD) — the Options toggle; null means the
    default, which is **on**.
  - `Session` (string[]?, `REG_MULTI_SZ`) — one entry per open tab, in tab-strip
    order.
  - `SessionActiveIndex` (int?, DWORD) — which of them was active.
- [ ] Session entry format: `path|blockIndex|blockHash|offsetWithin`, matching
  `ScrollRestore`'s fields, so a restored tab lands on the paragraph the reader
  was on rather than at the top. The block hash is what makes that survive the
  file being edited between sessions — the existing `RestoreScroll` already
  prefers hash over index. `|` is not legal in a Windows path.
- [ ] **Save on every change, not only on close.** `ApplyUpdatesAndRestart`
  hands the process to Velopack, which terminates it — `Window.Closed` cannot be
  relied on, and the update restart is the case this feature exists for. Save
  after `OpenFile` adds a tab, after `CloseTab`, and on `ActivateTab` (which
  already captures the outgoing tab's scroll anchor). Also save in the `Closed`
  handler, which is the only place the *active* tab's live scroll position can
  be captured.
- [ ] Restore in the constructor, before the `args` branch: read the list, open
  each path that still exists, apply each entry's `ScrollRestore`, then activate
  the saved index. With a file argument, open it afterwards so it becomes the
  active tab (user decision above). With no argument and no session, the
  welcome page as today.
- [ ] **Files that no longer exist are skipped silently** (one debug log line
  each). Restoring five "Cannot open file" tabs would be worse than restoring
  nothing. A session that ends up empty falls through to the welcome page.
- [ ] **Never save or restore when `MARKLITE_INSTANCE` is set.** Verification
  runs form their own single-instance group; if they also shared the session
  they would carry tabs between unrelated script runs and every existing check's
  tab assertions would start failing intermittently. The gate belongs in
  `UserSettings` so no caller can forget it.
- [ ] Options menu: `Reopen last session`, `ToggleType="CheckBox"`, checked
  state restored at startup like the View toggles. Turning it **off clears the
  stored session** rather than leaving a stale one behind.
- [ ] `dump-state` reports `restoreSession` (the setting) and `sessionCount`
  (entries currently stored), so a script can assert without reading the
  registry itself.

### Verification Plan
- New `tools/verify/test-session.ps1`, added to `run-all.ps1`:
  - open three fixtures as tabs, scroll the active one into the middle of the
    document, close the window with `WM_CLOSE` (the existing `Stop-MarkLite`);
  - relaunch with no arguments: three tabs, same paths in the same order, the
    same active index, and the active tab back on the same **block** (compare
    `firstVisibleBlock`, not pixels — an offset above the viewport is an
    estimate and legitimately differs between renders);
  - relaunch with a file argument: four tabs, the argument active;
  - delete one of the files and relaunch: two tabs, no error tab, and a log line
    naming the file that was skipped;
  - toggle the setting off, close, relaunch: welcome page, `sessionCount` 0.
- `run-all.ps1` still ALL PASS — in particular `test-tabs.ps1`, which asserts
  exact tab counts and would break first if the instance gate were wrong.
- Memory unchanged within 2 MB on `sample.md` versus the v1.1.0 figures
  (72.3 MB after the trim): the session is a handful of registry strings and
  must not register.
- `tools/scrub-check.ps1` exit 0.

### Phase Summary
_(write when phase completes)_

## Phase 3: A handoff raises the window
Status: Not started

`MainWindow` already calls `Activate()` when a handoff arrives, and it does not
work. Windows refuses to let a process that does not currently own the
foreground take it: `SetForegroundWindow` (which `Activate()` reaches) silently
degrades to flashing the taskbar button. The process with the right to give the
foreground away is the **secondary** launch — Explorer just started it in
response to the user's double-click, so it holds the foreground privilege for a
moment. It has to hand that privilege over before it exits.

- [ ] In `SingleInstance.SendToPrimary`, call `AllowSetForegroundWindow` before
  writing the path to the pipe. `ASFW_ANY` (`-1`) avoids plumbing the primary's
  process id through the protocol; the grant lapses at the next foreground
  change, so the window it opens is momentary.
- [ ] Only for a **file handoff**. The `--cmd` debug path goes through the same
  method and must never pull focus — verification scripts run while the user is
  working, and the existing handler already documents that. Either gate on the
  message type or grant only from the file-handoff caller.
- [ ] In the handoff handler, keep `Activate()` and consider following it with
  an explicit `SetForegroundWindow` on the window handle: `Activate()` is
  Avalonia's abstraction and it is worth confirming on the published exe which
  one actually raises a minimized window as opposed to merely an occluded one.
- [ ] A minimized primary must be **restored**, not just raised — check what
  `WindowState` it comes back in and restore it if the handoff finds it
  minimized.
- [ ] `MARKLITE_DEBUG` log line on the handoff recording whether the raise was
  attempted, so a failure is visible in the log rather than only on screen.

### Verification Plan
- **This one needs the window to take focus, which every other check in
  `tools/verify/` is written to avoid.** Warn the user and get an explicit go
  before running it; it is an observation (`GetForegroundWindow`), not injected
  input, but it will pull focus away from whatever they are doing.
- With the go: launch the primary on a document, click away so another window
  owns the foreground, hand a second file over with `Open-InMarkLite`, and
  assert `GetForegroundWindow()` is MarkLite's main window handle. Repeat with
  the primary minimized and assert it is restored, not just foregrounded.
- Without the go: `run-all.ps1` must still be ALL PASS — the `--cmd` path must
  not have started stealing focus, which `test-tabs.ps1` would not notice but
  the user would.

### Phase Summary
_(write when phase completes)_

## Phase 4: Keep the reading position across a window resize
Status: Not started

Requested by the user 2026-08-26, while Phase 1 was being verified. Resizing
the window keeps the **absolute** scroll offset, but a width change re-wraps
every block and re-measures the realized ones, so the offset no longer points
at the same text — the reader is dropped somewhere else in the document, and
the taller the document the further off it lands.

- [ ] Capture the anchor **before** the layout pass a size change triggers:
  `VirtualBlockPanel.FirstVisibleBlock` plus the offset within that block, the
  same pair `DocumentTab.CaptureScroll` already records and `dump-state`
  reports as `firstVisibleBlock` / `anchorWithin`.
- [ ] Restore it **after** the pass, once the new widths have been measured:
  scroll to `BlockOffset(block) + within`. The within-block offset is only
  meaningful up to the re-wrap; clamp it to the block's new height rather than
  letting it spill into the next block.
- [ ] Width changes only. A height-only resize does not re-wrap anything, and
  re-anchoring it would move the document under a reader who only made the
  window taller.
- [ ] The active tab is the one being resized, but every tab's viewer is laid
  out at the same width. Decide whether background tabs re-anchor on their next
  activation (their `SavedScroll` already exists) rather than doing work for
  windows nobody is looking at.
- [ ] Watch the interaction with the anchored reload path — both write the
  scroll offset, and a resize during a reload must not fight it.

### Verification Plan
- New `tools/verify/test-resize.ps1`, added to `run-all.ps1`. `SetWindowPos`
  with `SWP_NOACTIVATE` (already in `common.ps1`) changes the size without
  injecting input or taking focus.
- Open `stress-large.md`, scroll to the middle, record `firstVisibleBlock`,
  halve the window width, and assert the same block is still at the viewport
  top; then restore the width and assert it again.
- Repeat with a height-only change and assert the offset is untouched.
- A resize while the find bar is open and a match is highlighted keeps the
  match on screen.
- `run-all.ps1` still ALL PASS.

### Phase Summary
_(write when phase completes)_

## Phase 5: Release v1.2.0
Status: Not started

- [ ] README: Features bullet for session restore (the Help version line and
  the window raise are not worth bullets of their own).
- [ ] `<Version>1.2.0</Version>` in `src/MarkLite/MarkLite.csproj`.
- [ ] **Commit and push the version bump BEFORE packing.** `docs/RELEASING.md`
  step 2 says so and the v1.1.0 release did not: the shipped binary recorded the
  previous commit as its source revision while the tag pointed at the bump. No
  functional consequence that time, but the provenance was wrong.
- [ ] `build/pack.ps1` with the previous release's files present in `releases/`
  so a delta against 1.1.0 is produced; verify Setup.exe, full and delta nupkg
  and portable zip exist and the delta is a fraction of the full.
- [ ] Portable zip smoke run: `run-all.ps1 -Exe <unzipped>/current/MarkLite.exe`
  → all PASS.
- [ ] Release notes draft for the GitHub release body (`release.ps1` does not
  set one).
- [ ] Hand-off: the user runs `git push` and `build/release.ps1`. The agent
  never pushes, tags or uploads.

### Verification Plan
- `pack.ps1` exit 0; `releases/` contains the 1.2.0 full and delta nupkg,
  `MarkLite-win-Setup.exe`, the portable zip and `RELEASES`; delta < 40 % of
  full.
- `run-all.ps1` against the packed portable exe → all PASS.
- The packed exe's `ProductVersion` git hash matches the commit the `v1.2.0` tag
  will point at.
- `tools/scrub-check.ps1` exit 0 on the final tree.
- Post-release (user-run, recorded here from their log): an installed v1.1.0
  copy with `MARKLITE_DEBUG=1` finds 1.2.0, applies the delta, and comes back
  **with its tabs restored** — the feature's real acceptance test.

### Phase Summary
_(write when phase completes)_

## Final Recap
_(write when all phases complete: summary of the entire piece of work)_

## Deployment Plan
_(write when all phases complete: step-by-step deployment instructions)_
