/*  Adapted from MarkView.Avalonia.Mermaid's MermaidBlockRenderer.
    Copyright (c) Nicolas Musset. Distributed under the MIT license.
    Upstream: https://github.com/Kryptos-FR/MarkView.Avalonia
    See THIRD-PARTY-NOTICES.md.

    Why a copy instead of the package's renderer:

    1.  The package registered a fresh DetachedFromLogicalTree handler on EVERY
        attach, and each of those handlers cancelled and disposed the SAME
        CancellationTokenSource. The second detach of a diagram therefore hit an
        already-disposed source and threw ObjectDisposedException — fatal for a
        viewer whose tree is dropped and rebuilt on every tab switch.
    2.  The theme subscription (Application.PropertyChanged) was taken at render
        time but only ever released from inside that attach handler, so a
        diagram that never attached under a ScrollViewer kept the application
        alive-referenced forever.
    3.  Its Write() claimed every fenced block; MarkLite has its own code-block
        renderer and only wants the mermaid ones.

    Here attach and detach are symmetric (both on the VISUAL tree, so they
    pair), the token source is nulled after disposal, and a diagram whose render
    was cancelled by a detach restarts when it comes back. */

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Svg.Skia;
using Avalonia.VisualTree;

using Markdig.Syntax;

using MarkView.Avalonia.Rendering;

using Mermaider;

using MermaidRenderOptions = Mermaider.Models.RenderOptions;

namespace MarkLite.Rendering;

/// <summary>Renders a mermaid fence to an SVG diagram (pure .NET, no browser).</summary>
internal static class MermaidFenceRenderer
{
    /// <summary>Widest a diagram is allowed to draw, regardless of viewport.</summary>
    private const double MaxDiagramWidth = 800;

    public static void Write(AvaloniaRenderer renderer, FencedCodeBlock block)
    {
        var source = ExtractSource(block);

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var border = new Border { Child = image };
        border.Classes.Add("markdown-mermaid");

        /*  Rendering runs off the UI thread and can be superseded by a theme
            switch or cancelled by the block leaving the tree, so the token
            source has to outlive any single render. */
        CancellationTokenSource? cts = null;
        ScrollViewer? hookedScroller = null;
        var hooked = false;

        void ApplyMaxWidth()
        {
            /*  A ScrollViewer offers its children infinite width, so an
                unconstrained diagram scales to its natural size and overflows
                the page. */
            if (hookedScroller is { } scroller && scroller.Viewport.Width > 0)
            {
                image.MaxWidth = Math.Min(scroller.Viewport.Width, MaxDiagramWidth);
            }
        }

        /*  Explicit delegate instances: a local function converted to a
            delegate at two call sites is not guaranteed to produce the same
            instance, and "-=" only removes an equal one. */
        EventHandler<SizeChangedEventArgs> onScrollerSizeChanged = (_, _) => ApplyMaxWidth();

        async Task RenderAsync()
        {
            cts?.Cancel();
            cts?.Dispose();
            var localCts = cts = new CancellationTokenSource();
            var token = localCts.Token;

            var options = GetRenderOptions();
            try
            {
                /*  MermaidRenderer.RenderSvg and the SkiaSharp SVG load are
                    both heavy; keep them off the UI thread. */
                var svgSource = await Task.Run(() =>
                {
                    var svg = MermaidRenderer.RenderSvg(source, options);
                    svg = InlineCssVariables(svg, options);
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
                    return SvgSource.LoadFromStream(stream);
                }, token);

                if (!token.IsCancellationRequested)
                {
                    image.Source = new SvgImage { Source = svgSource };
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer render, or the block left the tree.
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    var panel = new StackPanel { Spacing = 4 };
                    panel.Children.Add(new TextBlock { Text = $"Mermaid render error: {ex.Message}" });
                    panel.Children.Add(new TextBlock { Text = source });
                    border.Child = panel;
                    border.Classes.Clear();
                    border.Classes.Add("markdown-mermaid-fallback");
                }
            }
        }

        /*  Diagram colors are baked into the SVG at render time — unlike every
            styled control they cannot follow a DynamicResource — so a theme
            switch has to redraw. */
        EventHandler<AvaloniaPropertyChangedEventArgs> onThemeChanged = (_, e) =>
        {
            if (e.Property.Name == nameof(Application.ActualThemeVariant))
            {
                _ = RenderAsync();
            }
        };

        image.AttachedToVisualTree += (_, _) =>
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            Application.Current!.PropertyChanged += onThemeChanged;
            if (image.FindAncestorOfType<ScrollViewer>() is { } scroller)
            {
                hookedScroller = scroller;
                scroller.SizeChanged += onScrollerSizeChanged;
                ApplyMaxWidth();
            }
            /*  Nothing drawn and nothing in flight: a previous render was
                cancelled by a detach, so start over now that we are back. */
            if (cts is null && image.Source is null)
            {
                _ = RenderAsync();
            }
        };

        image.DetachedFromVisualTree += (_, _) =>
        {
            if (!hooked)
            {
                return;
            }
            hooked = false;
            Application.Current?.PropertyChanged -= onThemeChanged;
            if (hookedScroller is { } scroller)
            {
                scroller.SizeChanged -= onScrollerSizeChanged;
                hookedScroller = null;
            }
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
        };

        renderer.WriteBlock(border);
        _ = RenderAsync();
    }

    /*  Colors come from MarkdownMermaid* brush resources when the host app
        defines them; the literals are the upstream fallback. */
    private static MermaidRenderOptions GetRenderOptions()
    {
        var app = Application.Current;
        var isDark = app?.ActualThemeVariant == ThemeVariant.Dark;
        var theme = app?.ActualThemeVariant ?? ThemeVariant.Light;

        return new MermaidRenderOptions
        {
            Bg = ResolveHex(app, theme, "MarkdownMermaidBackground", isDark ? "#18181B" : "#FFFFFF"),
            Fg = ResolveHex(app, theme, "MarkdownMermaidForeground", isDark ? "#FAFAFA" : "#27272A"),
            Accent = ResolveHex(app, theme, "MarkdownMermaidAccent", isDark ? "#60A5FA" : "#3B82F6"),
            Transparent = false,
        };
    }

    private static string ResolveHex(Application? app, ThemeVariant theme, string resourceKey, string fallback)
    {
        if (app is not null
            && app.TryGetResource(resourceKey, theme, out var resource)
            && resource is ISolidColorBrush brush)
        {
            var color = brush.Color;
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        return fallback;
    }

    private readonly record struct Rgb(byte R, byte G, byte B);

    /*  Mermaider emits CSS custom properties (var(--_xxx)); SkiaSharp has no
        CSS cascade and silently ignores them, so every reference is replaced
        with its computed hex value. The formulas mirror Mermaider's own style
        block. */
    private static string InlineCssVariables(string svg, MermaidRenderOptions options)
    {
        var bg = Parse(options.Bg ?? "#FFFFFF");
        var fg = Parse(options.Fg ?? "#27272A");
        var accent = Parse(options.Accent ?? "#3B82F6");
        var muted = options.Muted is { } m ? Parse(m) : (Rgb?)null;
        var line = options.Line is { } l ? Parse(l) : (Rgb?)null;
        var surface = options.Surface is { } s ? Parse(s) : (Rgb?)null;
        var stroke = options.Border is { } b ? Parse(b) : (Rgb?)null;

        var variables = new (string Token, Rgb Color)[]
        {
            ("var(--_text)",          fg),
            ("var(--_text-sec)",      muted ?? Mix(fg, 55, bg)),
            ("var(--_text-muted)",    muted ?? Mix(fg, 35, bg)),
            ("var(--_text-faint)",    Mix(fg, 20, bg)),
            ("var(--_line)",          line ?? Mix(fg, 32, bg)),
            ("var(--_arrow)",         accent),
            ("var(--_node-fill)",     surface ?? Mix(fg, 4, bg)),
            ("var(--_node-stroke)",   stroke ?? Mix(fg, 22, bg)),
            ("var(--_group-fill)",    bg),
            ("var(--_group-hdr)",     Mix(fg, 4, bg)),
            ("var(--_group-stroke)",  Mix(fg, 10, bg)),
            ("var(--_inner-stroke)",  Mix(fg, 10, bg)),
            ("var(--_key-badge)",     Mix(fg, 8, bg)),
            ("var(--_accent-fill)",   Mix(accent, 8, bg)),
            ("var(--_accent-stroke)", Mix(accent, 20, bg)),
            ("var(--_accent-text)",   Mix(accent, 65, bg)),
        };

        var builder = new StringBuilder(svg);
        foreach (var (token, color) in variables)
        {
            builder.Replace(token, Hex(color));
        }
        builder.Replace("background:var(--bg)", $"background:{Hex(bg)}");
        return builder.ToString();

        static Rgb Parse(string hex)
        {
            hex = hex.TrimStart('#');
            return new Rgb(Convert.ToByte(hex[..2], 16),
                           Convert.ToByte(hex[2..4], 16),
                           Convert.ToByte(hex[4..6], 16));
        }

        // color-mix(in srgb, a N%, b) — linear interpolation in sRGB space.
        static Rgb Mix(Rgb a, int aPercent, Rgb b)
        {
            var bPercent = 100 - aPercent;
            return new Rgb((byte)((a.R * aPercent / 100) + (b.R * bPercent / 100)),
                           (byte)((a.G * aPercent / 100) + (b.G * bPercent / 100)),
                           (byte)((a.B * aPercent / 100) + (b.B * bPercent / 100)));
        }

        static string Hex(Rgb color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string ExtractSource(FencedCodeBlock block)
    {
        if (block.Lines.Lines is null)
        {
            return string.Empty;
        }

        var lines = block.Lines;
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }
            builder.Append(lines.Lines[i].Slice.AsSpan());
        }
        return builder.ToString();
    }
}
