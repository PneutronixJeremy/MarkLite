using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MarkLite.Rendering.Virtual;

/*  Paints the selection.

    One control, drawing rectangles, rather than a highlight woven into the text
    like the search's split runs. Selection changes on every pointer move during
    a drag, and re-laying out the text of every block it crosses on every frame
    is not affordable; a rectangle per rendered line is.

    It is a permanent child of the panel, added FIRST so the blocks draw on top
    of it — a selection belongs behind the glyphs, the way it does in a text box.
    Arranged over the whole document height, so a block's content offset means
    the same number here as it does there, and no scroll position has to be
    tracked.

    Blocks with no controls draw nothing, and need to draw nothing: a fully
    covered block that has not been realized is, by definition, not on screen. */
internal sealed class SelectionAdorner : Control
{
    private readonly VirtualBlockPanel _panel;
    private readonly DocumentSelection _selection;
    private IBrush _brush = new SolidColorBrush(Color.FromArgb(0x59, 0x64, 0x95, 0xED));

    public SelectionAdorner(VirtualBlockPanel panel, DocumentSelection selection)
    {
        _panel = panel;
        _selection = selection;
        //  Behind the text and never in the way of a click on it.
        IsHitTestVisible = false;
        _selection.Changed += InvalidateVisual;
    }

    public override void Render(DrawingContext context)
    {
        if (_selection.IsEmpty)
        {
            return;
        }

        ResolveStyling();
        var start = _selection.Start;
        var end = _selection.End;

        foreach (var (index, container) in _panel.RealizedBlocks)
        {
            if (index < start.Block || index > end.Block)
            {
                continue;
            }

            /*  Interior blocks are covered end to end; the first and last are
                clipped to the endpoints the reader dragged to. */
            var text = _selection.IndexFor(index, container);
            var from = index == start.Block ? start.Offset : 0;
            var to = index == end.Block ? end.Offset : text.Length;

            foreach (var entry in text.Entries)
            {
                var localFrom = Math.Max(from, entry.Start) - entry.Start;
                var localTo = Math.Min(to, entry.End) - entry.Start;
                if (localTo <= localFrom)
                {
                    continue;
                }
                /*  Straight to the PANEL, not to the container and then down: the
                    adorner shares the panel's coordinate space, and the
                    container is arranged inset by the gutter strip. Translating
                    only within the container loses that inset and paints the
                    band a strip's width to the left of the text it belongs to. */
                if (entry.Block.TranslatePoint(new Point(0, 0), _panel) is not { } origin)
                {
                    continue;
                }
                foreach (var rect in BlockTextIndex.HighlightRects(entry.Block, localFrom, localTo))
                {
                    context.FillRectangle(_brush, rect.Translate(new Vector(origin.X, origin.Y)));
                }
            }
        }
    }

    /*  Resolved per render pass rather than cached, for the same reason the
        gutter's is: a theme switch replaces the brush, and this control is
        repainted on every selection change anyway. */
    private void ResolveStyling()
    {
        var variant = (this.FindAncestorOfType<Window>() as IThemeVariantHost)?.ActualThemeVariant
            ?? ThemeVariant.Default;
        if (this.TryFindResource("MdSelectionBackground", variant, out var value)
            && value is IBrush brush)
        {
            _brush = brush;
        }
    }
}
