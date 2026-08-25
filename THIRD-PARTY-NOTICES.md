# Third-party notices

MarkLite bundles or depends on the following third-party components. Each is
governed by its own license, available at the linked upstream project.

## Libraries (NuGet)

| Component | License | Source |
|-----------|---------|--------|
| Avalonia (+ Desktop, Themes.Fluent) | MIT | https://github.com/AvaloniaUI/Avalonia |
| MarkView.Avalonia (+ Math) | MIT | https://github.com/Kryptos-FR/MarkView.Avalonia |
| Markdig (transitive) | BSD-2-Clause | https://github.com/xoofx/markdig |
| Mermaider | MIT | https://github.com/Kryptos-FR/Mermaider |
| Svg.Controls.Skia.Avalonia | MIT | https://github.com/wieslawsoltes/Svg.Skia |
| CSharpMath (transitive) | MIT | https://github.com/verybadcat/CSharpMath |
| ColorCode.Core | MIT | https://github.com/CommunityToolkit/ColorCode-Universal |
| SkiaSharp / HarfBuzzSharp (native libs shipped beside the exe) | MIT | https://github.com/mono/SkiaSharp |

## Vendored source

`src/MarkLite/Rendering/MermaidFenceRenderer.cs` is adapted from the
`MermaidBlockRenderer` of **MarkView.Avalonia.Mermaid** — Copyright (c) Nicolas
Musset, MIT license, https://github.com/Kryptos-FR/MarkView.Avalonia. MarkLite
carries its own copy because the packaged renderer registers a detach handler
per attach and cancels an already-disposed `CancellationTokenSource` on the
second detach; the file header records the fixes. The MIT license text is
available at https://opensource.org/licenses/MIT.

## Bundled fonts (`src/MarkLite/Assets/Fonts/`)

| Font | License | Source |
|------|---------|--------|
| Fira Code (Retina) | SIL Open Font License 1.1 — Copyright (c) The Fira Code Project Authors | https://github.com/tonsky/FiraCode |
| Roboto | Apache License 2.0 — Copyright Google LLC (newer releases: SIL OFL 1.1) | https://fonts.google.com/specimen/Roboto |
| Lexend | SIL Open Font License 1.1 — Copyright (c) The Lexend Project Authors | https://github.com/googlefonts/lexend |
| Google Sans | SIL Open Font License 1.1 — Copyright Google LLC | https://fonts.google.com/specimen/Google+Sans |

The bundled Google Sans files are subset to Latin coverage (a modification
permitted by the OFL; the license and attribution name-table entries are
retained in the files).

The SIL Open Font License 1.1 text: https://openfontlicense.org
The Apache License 2.0 text: https://www.apache.org/licenses/LICENSE-2.0
