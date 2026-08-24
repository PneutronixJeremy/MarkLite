# MarkLite

A genuinely native Windows Markdown **viewer** — no web engine, no Electron, no
WebView2, no Node. Avalonia 12 + Skia, C#, .NET 10, Markdig parsing through
[MarkView.Avalonia](https://github.com/Kryptos-FR/MarkView.Avalonia), published
NativeAOT + trimmed. Built as a lightweight *plan viewer*: open a Markdown plan,
see real checkboxes, tables, and highlighted code — editing stays in your editor
of choice.

## Why

Every mainstream Markdown viewer on Windows drags in a browser engine. MarkLite
does not:

| Viewer           | Working set (MB) | Web engine        |
|------------------|-----------------:|-------------------|
| glow (terminal)  |            20–40 | no                |
| **MarkLite**     |       **65–111** | **no**            |
| Notepad          |             ~160 | yes (WebView2)    |
| Markpad          |             ~375 | yes               |
| Markdown Monster |             ~402 | yes (WebView2)    |

Measured on this machine (Windows 11, 32 GB): 65–69 MB with a typical document,
111 MB on large ones (40–77 KB of markdown — the whole document is realized as
controls, roughly 1 MB of working set per KB of markdown). First content
render: **~55 ms** after process start (median of 5), 150–180 ms for the large
files. Zero `msedgewebview2.exe` children, ever.

## Features

- Markdown Monster–style dark theme; follows the Windows app theme live
  (dark/light), including syntax-highlight palettes.
- CommonMark + GFM via Markdig: pipe tables (with column alignment), task lists
  as prominent read-only checkboxes, fenced code with syntax highlighting (C#,
  JS/TS, PowerShell, JSON, XML, and more via ColorCode), inline code chips,
  blockquotes, nested lists, setext headings, autolinks, strikethrough, emoji.
- **Mermaid diagrams render natively** — real flowcharts drawn with
  [Mermaider](https://github.com/Kryptos-FR/Mermaider), pure .NET. Still no JS
  runtime, still no browser.
- **Math**: inline and block TeX typeset natively (CSharpMath).
- Bundled fonts, no system dependency: Fira Code Retina for code; body font
  selectable under View > Body font (Roboto, Lexend, Segoe UI).
- Tabs: multiple documents, per-tab scroll/search/watcher; a second launch hands
  its file to the running instance over a named pipe.
- Contents sidebar (Ctrl+T) built from the document's heading tree, with
  current-section tracking and working in-document `#anchor` links.
- Find in document (Ctrl+F): live highlighting, F3 / Shift+F3 navigation with
  wraparound, match counter, survives live reload.
- Live reload: edits from any editor appear ~150 ms after save, scroll position
  preserved; deleted/locked files show a stale banner and recover automatically.
- Open via CLI argument, File > Open, drag-drop, or relative links between
  Markdown files.
- Text selection + copy.

## Build

Requires .NET 10 SDK. Plain build:

```powershell
dotnet build MarkLite.slnx -c Release
```

NativeAOT publish (writes `publish\MarkLite.exe`):

```powershell
.\build\publish.ps1
```

Note: `publish.ps1` encodes this machine's VS-less linker setup (MSVC toolset
path + Windows SDK import libs staged from the `Microsoft.Windows.SDK.CPP.x64`
NuGet package). On a machine with the "Desktop development with C++" workload
installed, a stock `dotnet publish -c Release -r win-x64` works instead.

## Usage

```powershell
MarkLite.exe path\to\file.md
```

No argument opens the welcome page. `MARKLITE_DEBUG=1` prints diagnostics
(startup timing, file loads, reloads, link routing, search, TOC) to stderr;
`MARKLITE_STANDALONE=1` skips the single-instance handoff so a launch always
gets its own window (useful for scripted checks).

## Deployment

Copy `publish\MarkLite.exe` (37.9 MB) + `libSkiaSharp.dll` (11.6 MB) +
`libHarfBuzzSharp.dll` (2.0 MB) anywhere — 51.4 MB in total. No installer, no
runtime, no registry, no symbols.
