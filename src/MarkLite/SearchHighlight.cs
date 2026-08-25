using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MarkLite;

/// <summary>One highlighted slice of one match, and the foreground it carried before.</summary>
internal sealed record HighlightPiece(Run Run, bool HadForeground, IBrush? BaseForeground);

/*  How a match becomes visible: the text runs of a TextBlock are split at the
    match boundaries and the match pieces get a background brush. The highlight
    is part of the text layout, so it wraps, scrolls and reflows with the
    document — no overlay geometry and no scroll synchronization.

    Every renderable piece of text is a TextBlock (prose blocks are
    MarkdownSelectableTextBlock, code blocks the SelectableTextBlock inside
    MarkLite's code panel) carrying standard Avalonia Inlines, so prose and code
    share one code path. Spans — including MarkdownHyperlink — are recursed into
    and KEPT: only their children are replaced, which leaves link hit-testing
    (index-based over the inline collection) intact.

    One session records the undo snapshots for whatever it split. Undo() puts
    the recorded collections back; Forget() drops the records without undoing,
    for a tree that no longer exists. Both searches use this: the classic one
    with a single session over the whole rendered document, the virtualizing one
    with a session per realized block, so recycling a block is a matter of
    dropping its session. */
internal sealed class HighlightSession
{
    /*  Undo record: an inline collection and the exact list it held before
        splitting. A block that had no inlines at all (plain Text) gets an empty
        snapshot — clearing the collection makes the TextBlock fall back to its
        Text property. */
    private sealed record Snapshot(InlineCollection Collection, List<Inline> Original);

    private static readonly AvaloniaProperty[] CopiedProperties =
    [
        TextElement.ForegroundProperty,
        TextElement.BackgroundProperty,
        TextElement.FontFamilyProperty,
        TextElement.FontSizeProperty,
        TextElement.FontStyleProperty,
        TextElement.FontWeightProperty,
        TextElement.FontStretchProperty,
    ];

    /*  Blocks that carry chrome rather than document text: list bullets and
        numbers, the code panel's language label, the checkbox glyph. Browsers
        do not find these either, and searching digits would otherwise light up
        every ordered-list marker. The model's own text projection leaves the
        same things out, so the two agree on what a document says. */
    private static readonly string[] ChromeClasses =
    [
        "markdown-list-marker",
        "CodeLangLabel",
        "TaskCheckGlyph",
    ];

    private readonly List<Snapshot> _undo = [];

    /// <summary>Whether this session has split anything that would need undoing.</summary>
    public bool IsEmpty => _undo.Count == 0;

    /// <summary>True for the TextBlocks that draw chrome rather than the document's text.</summary>
    public static bool IsChrome(TextBlock block) =>
        block.Classes.Any(static name => ChromeClasses.Contains(name));

    /*  A block's searchable text. Inline-based blocks are walked with the same
        rules the splitter uses, so match offsets and split offsets always
        agree; a block with no inlines falls back to its Text property. */
    public static string RenderedText(TextBlock block)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
        {
            return block.Text ?? string.Empty;
        }

        var builder = new StringBuilder();
        AppendInlineText(inlines, builder);
        return builder.ToString();
    }

    public static List<(int Start, int End)> FindRanges(string text, string term)
    {
        var ranges = new List<(int, int)>();
        if (term.Length == 0)
        {
            return ranges;
        }
        var at = 0;
        while ((at = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            ranges.Add((at, at + term.Length));
            at += term.Length;
        }
        return ranges;
    }

    public static int CountNewlines(string text, int upTo)
    {
        var count = 0;
        for (var i = 0; i < upTo; ++i)
        {
            if (text[i] == '\n')
            {
                ++count;
            }
        }
        return count;
    }

    /// <summary>Splits <paramref name="ranges"/> (offsets into <paramref name="text"/>, which must
    /// be this block's <see cref="RenderedText"/>) out of the block's runs, giving each piece the
    /// match brush. Every piece is reported through <paramref name="record"/> with its match
    /// ordinal, counting up from <paramref name="firstOrdinal"/> in document order.</summary>
    public void Split(TextBlock block, string text, List<(int Start, int End)> ranges,
        int firstOrdinal, IBrush matchBrush, Action<int, HighlightPiece> record)
    {
        var inlines = block.Inlines;
        if (inlines is null || ranges.Count == 0)
        {
            return;
        }

        if (inlines.Count == 0)
        {
            /*  Text-only block: build the split runs from the Text property.
                Non-empty Inlines take precedence over Text when the TextBlock
                renders, and clearing them restores the original look, so no
                property assignment is needed either way. */
            var replacement = new List<Inline>();
            var template = new Run { Text = string.Empty };
            SplitRun(template, text, OverlappingSegments(ranges, firstOrdinal, 0, text.Length),
                matchBrush, record, replacement);
            _undo.Add(new Snapshot(inlines, []));
            inlines.AddRange(replacement);
            return;
        }

        var offset = 0;
        ProcessCollection(inlines, ranges, firstOrdinal, matchBrush, record, ref offset);
    }

    /// <summary>Puts every collection this session split back the way it was.</summary>
    public void Undo()
    {
        /*  Reverse of the recording order (children first, then their parent),
            so a collection is restored before the collection that contains it.
            Span instances are reused rather than rebuilt, so either order in
            fact works — this one just keeps the invariant obvious. */
        for (var i = _undo.Count - 1; i >= 0; --i)
        {
            var snapshot = _undo[i];
            snapshot.Collection.Clear();
            snapshot.Collection.AddRange(snapshot.Original);
        }
        _undo.Clear();
    }

    /// <summary>Drops the undo records without applying them — for a tree that has been
    /// discarded, where restoring collections nobody can see is pure work.</summary>
    public void Forget() => _undo.Clear();

    private static void AppendInlineText(InlineCollection inlines, StringBuilder builder)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    builder.Append(run.Text);
                    break;
                case LineBreak:
                    builder.Append('\n');
                    break;
                case Span span when span.Inlines is { } children:
                    AppendInlineText(children, builder);
                    break;
                /*  InlineUIContainer and anything else contribute no
                    searchable characters. */
                default:
                    break;
            }
        }
    }

    /*  Maps the match ranges (over the block's concatenated text) onto the run
        spanning [offset, offset + length), clipped to run-local indexes. Ranges
        arrive in ascending order, so iteration stops at the first range
        starting past the run. */
    private static List<(int Start, int End, int Ordinal)> OverlappingSegments(
        List<(int Start, int End)> ranges, int firstOrdinal, int offset, int length)
    {
        var result = new List<(int, int, int)>();
        for (var i = 0; i < ranges.Count; ++i)
        {
            if (ranges[i].End <= offset)
            {
                continue;
            }
            if (ranges[i].Start >= offset + length)
            {
                break;
            }
            result.Add((
                Math.Max(ranges[i].Start - offset, 0),
                Math.Min(ranges[i].End - offset, length),
                firstOrdinal + i));
        }
        return result;
    }

    /*  Rebuilds one inline collection with match pieces split out, recursing
        into spans; returns whether anything below it changed. Untouched inline
        instances are reused, and a collection is replaced wholesale (Clear +
        AddRange) whenever a descendant changed — the top-level replacement is
        what reliably makes the owning TextBlock rebuild its text geometry, so
        a split deep inside a span must propagate upward. */
    private bool ProcessCollection(InlineCollection inlines,
        List<(int Start, int End)> ranges, int firstOrdinal, IBrush matchBrush,
        Action<int, HighlightPiece> record, ref int offset)
    {
        var original = inlines.ToList();
        var replacement = new List<Inline>();
        var changed = false;

        foreach (var inline in original)
        {
            switch (inline)
            {
                case Run run:
                {
                    var text = run.Text ?? string.Empty;
                    var segments = OverlappingSegments(ranges, firstOrdinal, offset, text.Length);
                    if (segments.Count == 0)
                    {
                        replacement.Add(run);
                    }
                    else
                    {
                        SplitRun(run, text, segments, matchBrush, record, replacement);
                        changed = true;
                    }
                    offset += text.Length;
                    break;
                }

                case LineBreak lineBreak:
                    offset += 1;
                    replacement.Add(lineBreak);
                    break;

                case Span span when span.Inlines is { } children:
                    changed |= ProcessCollection(children, ranges, firstOrdinal, matchBrush,
                        record, ref offset);
                    replacement.Add(span);
                    break;

                default:
                    replacement.Add(inline);
                    break;
            }
        }

        if (!changed)
        {
            return false;
        }

        _undo.Add(new Snapshot(inlines, original));
        inlines.Clear();
        inlines.AddRange(replacement);
        return true;
    }

    private static void SplitRun(Run source, string text,
        List<(int Start, int End, int Ordinal)> segments, IBrush matchBrush,
        Action<int, HighlightPiece> record, List<Inline> output)
    {
        var cursor = 0;
        foreach (var (start, end, ordinal) in segments)
        {
            if (start > cursor)
            {
                output.Add(CloneRun(source, text[cursor..start]));
            }

            var piece = CloneRun(source, text[start..end]);
            record(ordinal, new HighlightPiece(
                piece, piece.IsSet(TextElement.ForegroundProperty), piece.Foreground));
            piece.Background = matchBrush;
            output.Add(piece);
            cursor = end;
        }
        if (cursor < text.Length)
        {
            output.Add(CloneRun(source, text[cursor..]));
        }
    }

    private static Run CloneRun(Run source, string text)
    {
        var clone = new Run(text);
        foreach (var property in CopiedProperties)
        {
            if (source.IsSet(property))
            {
                clone.SetValue(property, source.GetValue(property));
            }
        }
        foreach (var className in source.Classes)
        {
            if (!className.StartsWith(':'))
            {
                clone.Classes.Add(className);
            }
        }
        return clone;
    }
}
