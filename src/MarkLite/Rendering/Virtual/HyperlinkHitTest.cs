using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;

using MarkView.Avalonia.Rendering.Inlines;

namespace MarkLite.Rendering.Virtual;

/*  Which link, if any, is under the pointer.

    MarkView draws a link as a MarkdownHyperlink — a Span, not a control — so
    there is nothing to click: the viewer has to hit-test the text itself. Its
    own hit test walks an index of every text block in the document, which the
    virtualizing viewer never builds (that index IS the fully rendered tree it
    exists to avoid), and the method is internal besides. This is the
    replacement, and it only ever looks at the block under the pointer.

    Two ways in, because a link can wrap two very different things:

    - text, found by asking the layout which character the point is over and
      then which span covers that character;
    - an embedded control — an image link — found by walking UP from the control
      that was hit to the InlineUIContainer holding it.

    MarkView's MarkdownSelectableTextBlock has its own HitTestHyperlink, but it
    is not public, so the walk below is MarkLite's — counting layout positions
    the way Avalonia's text layout does, which is one position for an embedded
    control and its own length for a run. */
internal static class HyperlinkHitTest
{
    /// <summary>The link covering a point given in <paramref name="block"/>'s coordinates.</summary>
    public static MarkdownHyperlink? At(TextBlock block, Point pointInBlock)
    {
        if (block.Inlines is not { Count: > 0 } inlines || block.TextLayout is not { } layout)
        {
            return null;
        }

        var local = pointInBlock - new Point(block.Padding.Left, block.Padding.Top);
        var candidate = AtLayoutIndex(inlines, layout.HitTestPoint(local).TextPosition);
        if (candidate is null)
        {
            return null;
        }

        /*  Confirmed against the link's own rectangles rather than trusting the
            hit test's IsInside flag.

            IsInside answers a different question than it looks like it does — it
            comes back false for points that are demonstrably on top of the
            glyphs, so gating on it loses real clicks. Asking instead whether the
            point is inside one of the rectangles the layout says this link
            occupies is both stricter (a click in the empty space right of a
            short line no longer follows whatever link ended that line) and
            exact. */
        foreach (var (link, start, length) in Links(block))
        {
            if (!ReferenceEquals(link, candidate))
            {
                continue;
            }
            foreach (var rect in layout.HitTestTextRange(start, length))
            {
                if (rect.Contains(local))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>Every link in a block, in document order, with the layout range it covers — what
    /// turns "the second link in block 12" into a point that can be clicked.</summary>
    public static IEnumerable<(MarkdownHyperlink Link, int Start, int Length)> Links(TextBlock block)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
        {
            return [];
        }
        var found = new List<(MarkdownHyperlink, int, int)>();
        var seen = 0;
        Walk(inlines);
        return found;

        void Walk(InlineCollection collection)
        {
            foreach (var inline in collection)
            {
                if (inline is Span span && span.Inlines is { } children)
                {
                    var start = seen;
                    Walk(children);
                    if (span is MarkdownHyperlink link && seen > start)
                    {
                        found.Add((link, start, seen - start));
                    }
                    continue;
                }
                seen += LayoutLength(inline);
            }
        }
    }

    /// <summary>The link wrapping a control that was hit — an image inside a link. Walks up from
    /// the control to the TextBlock that hosts it, then finds the inline container holding it.
    /// </summary>
    public static MarkdownHyperlink? AtControl(Control control)
    {
        var host = control.FindAncestorOfType<TextBlock>();
        if (host?.Inlines is not { Count: > 0 } inlines)
        {
            return null;
        }
        return Enclosing(inlines, container => IsHost(container, control));
    }

    private static bool IsHost(InlineUIContainer container, Control control)
    {
        for (Visual? visual = control; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(container.Child, visual))
            {
                return true;
            }
        }
        return false;
    }

    /*  Which span covers a position in the TEXT LAYOUT — layout positions, not
        copyable characters, because that is what the layout's own hit test
        answers in. An embedded control occupies one position here and none in
        the block's text; see BlockTextIndex for the conversion the selection
        needs. */
    private static MarkdownHyperlink? AtLayoutIndex(InlineCollection inlines, int index)
    {
        var seen = 0;
        return Walk(inlines, null);

        MarkdownHyperlink? Walk(InlineCollection collection, MarkdownHyperlink? enclosing)
        {
            foreach (var inline in collection)
            {
                if (seen > index)
                {
                    return null;
                }
                switch (inline)
                {
                    case Span span when span.Inlines is { } children:
                    {
                        var found = Walk(children, span as MarkdownHyperlink ?? enclosing);
                        if (found is not null)
                        {
                            return found;
                        }
                        break;
                    }

                    default:
                    {
                        var length = LayoutLength(inline);
                        if (length > 0 && index >= seen && index < seen + length)
                        {
                            return enclosing;
                        }
                        seen += length;
                        break;
                    }
                }
            }
            return null;
        }
    }

    /// <summary>Positions one inline occupies in the text layout. An embedded control takes one,
    /// which is why this is not the same as the characters it contributes to the block's text —
    /// see BlockTextIndex.</summary>
    private static int LayoutLength(Inline inline) => inline switch
    {
        Run run => run.Text?.Length ?? 0,
        LineBreak => 1,
        InlineUIContainer => 1,
        _ => 0,
    };

    /*  The innermost link whose subtree contains an inline the predicate
        accepts. Used for embedded controls, where there is no character index to
        look up — only "this control is in there somewhere". */
    private static MarkdownHyperlink? Enclosing(
        InlineCollection inlines, System.Func<InlineUIContainer, bool> matches)
    {
        return Walk(inlines, null);

        MarkdownHyperlink? Walk(IEnumerable<Inline> collection, MarkdownHyperlink? enclosing)
        {
            foreach (var inline in collection)
            {
                switch (inline)
                {
                    case InlineUIContainer container when matches(container):
                        return enclosing;

                    case Span span when span.Inlines is { } children:
                    {
                        var found = Walk(children, span as MarkdownHyperlink ?? enclosing);
                        if (found is not null)
                        {
                            return found;
                        }
                        break;
                    }
                }
            }
            return null;
        }
    }
}
