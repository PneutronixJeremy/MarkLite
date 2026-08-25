using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

using Markdig.Syntax;

namespace MarkLite.Rendering.Virtual;

/*  The line-number gutter: for every block on screen, where it starts in the
    source file, so what the reader is looking at can be found again in an
    editor.

    It draws rather than builds controls. A number per realized block — and a
    number per LINE of a fenced code block — would be hundreds of TextBlocks
    for no benefit; the whole point of the virtualizing host is to not do that.
    Render() is one pass over the blocks that already have controls.

    The strip it draws in is reserved by VirtualBlockPanel on BOTH sides of the
    document, always, whether the numbers are showing or not. That is what makes
    the toggle free: turning it on changes no width, re-wraps no text, and
    invalidates not one cached block height. */
internal sealed class GutterPanel : Control
{
    /// <summary>Width of the reserved strip, sized for a four-digit line number in the code
    /// font — the largest fixture in the repo is under 6000 lines. A longer document's numbers
    /// eat into the gap rather than being clipped or moving the text column.</summary>
    internal const double Reserve = 40;

    /// <summary>Gap between the numbers and the document text.</summary>
    private const double TextGap = 10;

    private const double FontSize = 11.5;

    /*  Global, like the HTML-comment toggle: the setting belongs to the app, not
        to whichever tab happens to be active, and a tab switch rebuilds the
        panel from scratch. */
    public static bool Enabled { get; set; }

    private readonly VirtualBlockPanel _panel;
    private Typeface _typeface = Typeface.Default;
    private IBrush _brush = Brushes.Gray;

    public GutterPanel(VirtualBlockPanel panel)
    {
        _panel = panel;
        //  Nothing here is interactive, and hit-testing a strip that covers the
        //  whole document height would steal clicks meant for the text.
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        if (!Enabled || _panel.Model is not { } model)
        {
            return;
        }

        ResolveStyling();

        foreach (var (index, container) in _panel.RealizedBlocks)
        {
            if (index < 0 || index >= model.Blocks.Count)
            {
                continue;
            }
            var block = model.Blocks[index];
            var top = _panel.BlockOffset(index);

            /*  Only code blocks are numbered line by line, and the type check
                is the guard rather than "does it contain a SelectableTextBlock":
                prose is selectable too, and numbering a paragraph's WRAPPED
                rows would invent source lines that do not exist. Mermaid
                fences are excluded — they render a diagram, not lines. */
            if (block.Block is CodeBlock and not (FencedCodeBlock { Info: "mermaid" })
                && CodeLineOffsets(container) is { } code)
            {
                /*  Fenced code is laid out one source line per rendered line
                    (the renderer sets TextWrapping.NoWrap), so every line can
                    carry its own number. The fence's own line is the block's
                    start, so the first line of code is one past it. */
                var isFenced = block.Block is FencedCodeBlock;
                var firstLine = isFenced ? block.StartLine + 1 : block.StartLine;
                for (var line = 0; line < code.Tops.Length; line++)
                {
                    DrawNumber(context, firstLine + line, top + code.Origin + code.Tops[line]);
                }
                continue;
            }

            DrawNumber(context, block.StartLine, top);
        }
    }

    private void DrawNumber(DrawingContext context, int line, double top)
    {
        //  Cheap early-out: the strip is as tall as the whole document, but only
        //  the viewport is ever on screen.
        if (top + (FontSize * 2) < 0 || top > Bounds.Height)
        {
            return;
        }

        var text = new FormattedText(
            line.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            _brush);
        //  Right-aligned against the gap, so the digits line up whatever their
        //  count and the column of numbers reads as one column.
        context.DrawText(text, new Point(Reserve - TextGap - text.Width, top));
    }

    /*  Where a code block's rendered lines sit, relative to the block's top:
        the offset of the code TextBlock inside the block's container, plus the
        top of each line from the text layout itself. Asking the layout rather
        than multiplying by a line height keeps the numbers aligned when a
        theme changes the code font or its line spacing. */
    private (double Origin, double[] Tops)? CodeLineOffsets(BlockContainer container)
    {
        var text = container.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .FirstOrDefault();
        if (text is null || text.TextLayout is not { } layout || layout.TextLines.Count <= 1)
        {
            return null;
        }
        if (text.TranslatePoint(new Point(0, 0), container)?.Y is not { } origin)
        {
            return null;
        }

        var tops = new double[layout.TextLines.Count];
        var running = 0.0;
        for (var line = 0; line < layout.TextLines.Count; line++)
        {
            tops[line] = running;
            running += layout.TextLines[line].Height;
        }
        return (origin, tops);
    }

    /*  Resolved per render pass rather than cached: a theme switch replaces both
        the brush and (through MdCodeFontFamily) possibly the font, and the
        gutter is redrawn on every layout pass anyway. */
    private void ResolveStyling()
    {
        var variant = (this.FindAncestorOfType<Window>() as IThemeVariantHost)?.ActualThemeVariant
            ?? ThemeVariant.Default;

        if (this.TryFindResource("MdCodeFontFamily", variant, out var font)
            && font is FontFamily family)
        {
            _typeface = new Typeface(family);
        }
        if (this.TryFindResource("MdMutedForeground", variant, out var brush)
            && brush is IBrush muted)
        {
            _brush = muted;
        }
    }
}
