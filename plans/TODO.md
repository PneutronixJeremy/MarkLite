# MarkLite — future work

Ideas parked deliberately. Nothing here is scheduled; each entry records enough
context to pick it up cold.

## Single physical exe

NativeAOT output needs `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` and
`av_libglesv2.dll` beside the exe — AOT has no self-extract. Two options worth
investigating:

- Static-link Skia and HarfBuzz into the AOT binary. Unsupported by the
  SkiaSharp packages; needs custom `.lib` builds.
- Accept a trimmed **JIT** publish with `PublishSingleFile` +
  `IncludeNativeLibrariesForSelfExtract`. One file, but it loses AOT: slower
  cold start and a bigger download.

Current deployment is the exe + 2 native dlls (51.4 MB), which is acceptable;
this is a nicety.

## Upstream bug report: Mermaid renderer crash

`MarkView.Avalonia.Mermaid`'s `MermaidBlockRenderer` registers a new
`DetachedFromLogicalTree` handler on **every** attach and calls `Cancel()` on an
already-disposed `CancellationTokenSource`, so the second detach of the same
diagram throws `ObjectDisposedException`. It also leaks an
`Application.PropertyChanged` subscription when the image never attaches under a
`ScrollViewer`. MarkLite vendors a fixed copy
(`src/MarkLite/Rendering/MermaidFenceRenderer.cs`, MIT notice in
`THIRD-PARTY-NOTICES.md`); the fix should go upstream as an issue plus PR
against https://github.com/Kryptos-FR/MarkView.Avalonia.
