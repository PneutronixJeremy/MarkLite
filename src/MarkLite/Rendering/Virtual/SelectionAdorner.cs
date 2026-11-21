using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MarkLite.Rendering.Virtual;

/*  Paints the selection.

    One control, drawing rectangles, rather than a highlight woven into the text
    like the search's split runs. Selection changes on every pointer move during
    a drag, and re-laying out the text of every block it crosses on every frame
    is not affordable; a rectangle per rendered line is.

    It is a permanent child of the panel, drawn ON TOP of the blocks with a
    translucent brush. Behind the glyphs would be the text-box convention, but
    several blocks paint an opaque background of their own — the code panel,
    the table header row, the inline code chip — and a band underneath those
    simply vanished: a selection that crossed a code block looked like it had
    skipped it. Over the text it tints the glyphs a little and is visible
    everywhere. Arranged over the whole document height, so a block's content
    offset means the same number here as it does there, and no scroll position
    has to be tracked.

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
        //  Above every block (they are added later, at the default ZIndex) and
        //  never in the way of a click on them.
        ZIndex = 1;
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
                using (context.PushClip(ClipFor(entry.Block, container)))
                {
                    foreach (var rect in BlockTextIndex.HighlightRects(entry.Block, localFrom, localTo))
                    {
                        context.FillRectangle(_brush, rect.Translate(new Vector(origin.X, origin.Y)));
                    }
                }
            }
        }
    }

    /*  The part of the panel a text block is actually visible in. A code block
        scrolls its long lines sideways inside its own ScrollViewer, and the
        text layout reports rectangles for the whole line — the part scrolled
        out of view included. The adorner is not inside that ScrollViewer, so
        nothing clips it there for free: unclipped, the band ran on past the
        code panel's edge. Only a presenter INSIDE the block counts; the
        document's own ScrollViewer is above the panel and already clips. */
    private Rect ClipFor(TextBlock text, BlockContainer container)
    {
        var all = new Rect(Bounds.Size);
        if (text.FindAncestorOfType<ScrollContentPresenter>() is not { } presenter
            || !container.IsVisualAncestorOf(presenter)
            || presenter.TranslatePoint(new Point(0, 0), _panel) is not { } origin)
        {
            return all;
        }
        return new Rect(origin, presenter.Bounds.Size);
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
