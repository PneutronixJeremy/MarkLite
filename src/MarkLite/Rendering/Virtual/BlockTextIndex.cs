using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;

namespace MarkLite.Rendering.Virtual;

/*  One realized block's text, as one string, with the map back to the controls
    that draw it.

    A top-level block is not one TextBlock: a list is one per item, a table one
    per cell, a footnote group one per definition. A selection has to be
    addressable as a single offset into the block — that is the only thing that
    survives the block being recycled and re-realized — so the block's text
    blocks are concatenated in visual order, which is document order because
    every renderer appends its controls in the order the source has them.

    The concatenation is joined with a newline, the same separator the model's
    own projection puts between two leaves. That is what lets an offset counted
    here be handed to MarkdownDocumentModel.SourceOffset and come back as a
    position in the markdown file. */
internal sealed class BlockTextIndex
{
    /// <summary>One TextBlock's slice of the block's text.</summary>
    internal readonly record struct Entry(TextBlock Block, int Start, int Length)
    {
        public int End => Start + Length;
    }

    private readonly List<Entry> _entries;

    private BlockTextIndex(string text, List<Entry> entries)
    {
        Text = text;
        _entries = entries;
    }

    /// <summary>The block's rendered text, newline-separated per drawn TextBlock.</summary>
    public string Text { get; }

    public int Length => Text.Length;

    public IReadOnlyList<Entry> Entries => _entries;

    public static BlockTextIndex Build(BlockContainer container)
    {
        var builder = new StringBuilder();
        var entries = new List<Entry>();

        foreach (var textBlock in container.GetVisualDescendants().OfType<TextBlock>())
        {
            //  Same exclusions as search: list markers, the code panel's
            //  language label and the task checkbox are chrome, not text.
            if (HighlightSession.IsChrome(textBlock))
            {
                continue;
            }
            var text = HighlightSession.RenderedText(textBlock);
            if (text.Length == 0)
            {
                continue;
            }
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }
            entries.Add(new Entry(textBlock, builder.Length, text.Length));
            builder.Append(text);
        }

        return new BlockTextIndex(builder.ToString(), entries);
    }

    /// <summary>The TextBlock an offset falls in, and the offset within it.</summary>
    public bool TryLocate(int offset, out Entry entry, out int local)
    {
        foreach (var candidate in _entries)
        {
            if (offset >= candidate.Start && offset <= candidate.End)
            {
                entry = candidate;
                local = offset - candidate.Start;
                return true;
            }
        }
        entry = default;
        local = 0;
        return false;
    }

    /// <summary>Character offset for a point given in <paramref name="container"/> coordinates.
    /// A point in the gap between two drawn blocks, or out in the margin, resolves to the nearest
    /// one rather than to nothing — a drag has to keep meaning something when the pointer leaves
    /// the text.</summary>
    public int OffsetAt(BlockContainer container, Point point)
    {
        if (_entries.Count == 0)
        {
            return 0;
        }

        var best = _entries[0];
        var bestOrigin = new Point(0, 0);
        var bestDistance = double.MaxValue;

        foreach (var entry in _entries)
        {
            if (entry.Block.TranslatePoint(new Point(0, 0), container) is not { } origin)
            {
                continue;
            }
            var bounds = new Rect(origin, entry.Block.Bounds.Size);
            var distance = bounds.Contains(point)
                ? 0
                : VerticalDistance(bounds, point.Y);
            if (distance < bestDistance)
            {
                best = entry;
                bestOrigin = origin;
                bestDistance = distance;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        return best.Start + LocalOffsetAt(best.Block, point - bestOrigin);
    }

    /// <summary>Character offset inside one TextBlock for a point in ITS coordinates.</summary>
    public static int LocalOffsetAt(TextBlock block, Point point)
    {
        if (block.TextLayout is not { } layout)
        {
            return 0;
        }
        /*  The layout starts inside the padding, and its own hit test works in
            layout coordinates — a point handed over unshifted lands a few pixels
            off, which at the start of a line is a whole character. */
        var local = point - new Point(block.Padding.Left, block.Padding.Top);
        var hit = layout.HitTestPoint(local);
        /*  IsTrailing means the pointer is past the middle of the glyph, which
            is where a caret belongs after it — the same rule a text box uses,
            and what makes a drag select the character the pointer is over. */
        return ToTextOffset(block, hit.TextPosition + (hit.IsTrailing ? 1 : 0));
    }

    /// <summary>Rectangles covering [start, end) of one TextBlock's text, in ITS coordinates —
    /// what the selection adorner paints.</summary>
    public static IEnumerable<Rect> HighlightRects(TextBlock block, int start, int end)
    {
        if (block.TextLayout is not { } layout || end <= start)
        {
            return [];
        }
        var from = ToLayoutIndex(block, start);
        var to = ToLayoutIndex(block, end);
        if (to <= from)
        {
            return [];
        }
        var offset = new Point(block.Padding.Left, block.Padding.Top);
        return layout.HitTestTextRange(from, to - from)
            .Select(rect => rect.Translate(new Vector(offset.X, offset.Y)));
    }

    /*  TWO ways to count the characters of a TextBlock, and they are not the
        same count.

        The text layout counts what it lays out, and an embedded control — an
        inline image, a rendered formula — occupies one position in it. The
        block's TEXT counts what a reader could copy, and an embedded control
        contributes nothing: it is a picture, and search and the model's own
        projection both leave it out. So a paragraph with an inline image has
        one more layout position than it has characters, from the image onward.

        Everything that hit-tests or paints therefore converts. Without this a
        selection in a paragraph containing an image would copy a slice shifted
        by one character per image before it, which is exactly the kind of
        off-by-one that looks like a rendering bug. */
    private static int ToTextOffset(TextBlock block, int layoutIndex)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
        {
            return layoutIndex;
        }

        var layoutSeen = 0;
        var textSeen = 0;
        Convert(inlines);
        return Math.Min(textSeen, layoutIndex - layoutSeen + textSeen);

        void Convert(InlineCollection collection)
        {
            foreach (var inline in collection)
            {
                if (layoutSeen >= layoutIndex)
                {
                    return;
                }
                switch (inline)
                {
                    case Span span when span.Inlines is { } children:
                        Convert(children);
                        break;
                    default:
                    {
                        var (layoutLength, textLength) = Lengths(inline);
                        if (layoutSeen + layoutLength > layoutIndex)
                        {
                            //  Inside this inline: clamp the remainder to what
                            //  it actually contributes as text.
                            textSeen += Math.Min(layoutIndex - layoutSeen, textLength);
                            layoutSeen = layoutIndex;
                            return;
                        }
                        layoutSeen += layoutLength;
                        textSeen += textLength;
                        break;
                    }
                }
            }
        }
    }

    private static int ToLayoutIndex(TextBlock block, int textOffset)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
        {
            return textOffset;
        }

        var layoutSeen = 0;
        var textSeen = 0;
        Convert(inlines);
        return layoutSeen + (textOffset - textSeen);

        void Convert(InlineCollection collection)
        {
            foreach (var inline in collection)
            {
                if (textSeen >= textOffset)
                {
                    return;
                }
                switch (inline)
                {
                    case Span span when span.Inlines is { } children:
                        Convert(children);
                        break;
                    default:
                    {
                        var (layoutLength, textLength) = Lengths(inline);
                        if (textSeen + textLength > textOffset)
                        {
                            layoutSeen += textOffset - textSeen;
                            textSeen = textOffset;
                            return;
                        }
                        layoutSeen += layoutLength;
                        textSeen += textLength;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>(positions in the text layout, characters in the block's text) for one inline.
    /// They differ only for embedded controls, which draw but cannot be copied.</summary>
    private static (int Layout, int Text) Lengths(Inline inline) => inline switch
    {
        Run run => (run.Text?.Length ?? 0, run.Text?.Length ?? 0),
        LineBreak => (1, 1),
        InlineUIContainer => (1, 0),
        _ => (0, 0),
    };

    private static double VerticalDistance(Rect bounds, double y)
    {
        if (y < bounds.Y)
        {
            return bounds.Y - y;
        }
        if (y > bounds.Bottom)
        {
            return y - bounds.Bottom;
        }
        //  Vertically inside but horizontally outside: as close as it gets.
        return 0;
    }
}
