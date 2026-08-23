# MarkLite

A genuinely native Windows Markdown **viewer** — no web engine, no Electron, no
WebView2, no Node. Avalonia 11 + Skia, C#, .NET 10, published NativeAOT +
trimmed. Built as a lightweight *plan viewer*: open a Markdown plan, see real
checkboxes, tables, and highlighted code — editing stays in your editor of
choice.

## Why

Every mainstream Markdown viewer on Windows drags in a browser engine. MarkLite
does not:

| Viewer           | Working set (MB) | Web engine        |
|------------------|-----------------:|-------------------|
| glow (terminal)  |            20–40 | no                |
| **MarkLite**     |        **54–98** | **no**            |
| Notepad          |             ~160 | yes (WebView2)    |
| Markpad          |             ~375 | yes               |
| Markdown Monster |             ~402 | yes (WebView2)    |

Measured on this machine (Windows 11, 32 GB): 54 MB with a typical document,
98 MB worst case with a 77 KB pathological README containing thousands of
links. First content render: **~48 ms** after process start (median of 5).
Zero `msedgewebview2.exe` children, ever.

## Features

- Markdown Monster–style dark theme; follows the Windows app theme live
  (dark/light), including syntax-highlight palettes.
- GFM: pipe tables (with column alignment), task lists as prominent read-only
  checkboxes, fenced code with syntax highlighting (C#, JS/TS, PowerShell,
  JSON, XML, and more via ColorCode), inline code chips, blockquotes, nested
  lists.
- Mermaid fences render as plain labeled code blocks (no JS runtime — by design).
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

No argument opens the bundled sample. `MARKLITE_DEBUG=1` prints diagnostics
(startup timing, file loads, reloads, link routing) to stderr.

## Deployment

Copy `publish\MarkLite.exe` + `libSkiaSharp.dll` + `libHarfBuzzSharp.dll`
anywhere. No installer, no runtime, no registry.
