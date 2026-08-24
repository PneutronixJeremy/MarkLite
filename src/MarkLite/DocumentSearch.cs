using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MarkView.Avalonia;

namespace MarkLite;

/*  Case-insensitive substring search over the rendered document. Matches are
    highlighted by splitting text runs at match boundaries and giving the match
    pieces a background brush — the highlight is part of the text layout, so it
    wraps, scrolls and reflows with the document; no overlay geometry and no
    scroll synchronization.

    Every renderable block is a TextBlock (prose blocks are
    MarkdownSelectableTextBlock, code blocks the SelectableTextBlock inside
    MarkLite's code panel) carrying standard Avalonia Inlines, so prose and code
    share one code path. Spans — including MarkdownHyperlink — are recursed into
    and kept: only their children are replaced, which leaves link hit-testing
    (index-based over the inline collection) intact.

    Clear() reverts highlighting by restoring the recorded inline snapshots.
    After a re-render those records point at discarded controls, so the owner
    must call Detach() (forget without undoing) and Apply() again on the fresh
    tree. Moving the current match only swaps Background/Foreground on
    already-split pieces, which repaints without re-measuring. */
internal sealed class DocumentSearch
{
    private sealed record Piece(Run Run, bool HadForeground, IBrush? BaseForeground);

    /*  Scroll target for a match: the containing block, plus — for code blocks,
        which can be hundreds of lines tall — the match's line position, so the
        scroll lands near the line rather than at the block top. */
    private sealed record Anchor(TextBlock Block, int LineIndex, int TotalLines);

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
        every ordered-list marker. */
    private static readonly string[] SkippedClasses =
    [
        "markdown-list-marker",
        "CodeLangLabel",
        "TaskCheckGlyph",
    ];

    private readonly MarkdownViewer _viewer;

    private readonly List<List<Piece>> _matches = [];
    private readonly List<Anchor> _anchors = [];
    private readonly List<Snapshot> _undo = [];
    private int _current = -1;

    private IBrush _matchBrush = Brushes.Yellow;
    private IBrush _currentBrush = Brushes.Orange;
    private IBrush _currentForeground = Brushes.Black;

    public DocumentSearch(MarkdownViewer viewer)
    {
        _viewer = viewer;
    }

    public int Count => _matches.Count;

    /// <summary>Zero-based index of the current match; -1 when there are none.</summary>
    public int CurrentOrdinal => _current;

    public void Apply(string term, IBrush matchBrush, IBrush currentBrush, IBrush currentForeground, bool scrollToCurrent)
    {
        Clear();
        _matchBrush = matchBrush;
        _currentBrush = currentBrush;
        _currentForeground = currentForeground;

        if (string.IsNullOrEmpty(term))
        {
            return;
        }

        /*  Materialize the block list before mutating: highlighting replaces
            inline collections, which changes the tree that
            GetVisualDescendants is lazily walking. */
        var blocks = _viewer.GetVisualDescendants().OfType<TextBlock>()
            .Where(static block => !block.Classes.Any(static name => SkippedClasses.Contains(name)))
            .ToList();

        foreach (var block in blocks)
        {
            var text = BlockText(block);
            if (text.Length == 0)
            {
                continue;
            }

            var ranges = FindRanges(text, term);
            if (ranges.Count == 0)
            {
                continue;
            }

            var firstOrdinal = _matches.Count;
            var totalLines = CountNewlines(text, text.Length) + 1;
            foreach (var range in ranges)
            {
                _matches.Add([]);
                _anchors.Add(new Anchor(block, CountNewlines(text, range.Start), totalLines));
            }
            HighlightBlock(block, text, ranges, firstOrdinal);
        }

        if (_matches.Count > 0)
        {
            SetCurrent(0, scrollToCurrent);
        }
    }

    /// <summary>Reverts all highlighting by restoring the recorded inline snapshots.</summary>
    public void Clear()
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
        Detach();
    }

    /// <summary>Forgets all state without undoing — call when the document was re-rendered
    /// and the recorded controls no longer exist in the tree.</summary>
    public void Detach()
    {
        _undo.Clear();
        _matches.Clear();
        _anchors.Clear();
        _current = -1;
    }

    public void MoveNext()
    {
        if (Count > 0)
        {
            SetCurrent((_current + 1) % Count, scroll: true);
        }
    }

    public void MovePrevious()
    {
        if (Count > 0)
        {
            SetCurrent((_current - 1 + Count) % Count, scroll: true);
        }
    }

    /*  A block's searchable text. Inline-based blocks are walked with the same
        rules the splitter uses, so match offsets and split offsets always
        agree; a block with no inlines falls back to its Text property. */
    private static string BlockText(TextBlock block)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
        {
            return block.Text ?? string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        AppendInlineText(inlines, builder);
        return builder.ToString();
    }

    private static void AppendInlineText(InlineCollection inlines, System.Text.StringBuilder builder)
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

    private static List<(int Start, int End)> FindRanges(string text, string term)
    {
        var ranges = new List<(int, int)>();
        var at = 0;
        while ((at = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            ranges.Add((at, at + term.Length));
            at += term.Length;
        }
        return ranges;
    }

    private static int CountNewlines(string text, int upTo)
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

    private void HighlightBlock(TextBlock block, string text,
        List<(int Start, int End)> ranges, int firstOrdinal)
    {
        var inlines = block.Inlines;
        if (inlines is null)
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
            SplitRun(template, text, OverlappingSegments(ranges, firstOrdinal, 0, text.Length), replacement);
            _undo.Add(new Snapshot(inlines, []));
            inlines.AddRange(replacement);
            return;
        }

        var offset = 0;
        ProcessCollection(inlines, ranges, firstOrdinal, ref offset);
    }

    /*  Rebuilds one inline collection with match pieces split out, recursing
        into spans; returns whether anything below it changed. Untouched inline
        instances are reused, and a collection is replaced wholesale (Clear +
        AddRange) whenever a descendant changed — the top-level replacement is
        what reliably makes the owning TextBlock rebuild its text geometry, so
        a split deep inside a span must propagate upward. */
    private bool ProcessCollection(InlineCollection inlines,
        List<(int Start, int End)> ranges, int firstOrdinal, ref int offset)
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
                        SplitRun(run, text, segments, replacement);
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
                    changed |= ProcessCollection(children, ranges, firstOrdinal, ref offset);
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

    private void SplitRun(Run source, string text,
        List<(int Start, int End, int Ordinal)> segments, List<Inline> output)
    {
        var cursor = 0;
        foreach (var (start, end, ordinal) in segments)
        {
            if (start > cursor)
            {
                output.Add(CloneRun(source, text[cursor..start]));
            }

            var piece = CloneRun(source, text[start..end]);
            _matches[ordinal].Add(new Piece(piece, piece.IsSet(TextElement.ForegroundProperty), piece.Foreground));
            piece.Background = _matchBrush;
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

    private void SetCurrent(int ordinal, bool scroll)
    {
        if (_current >= 0 && _current < _matches.Count)
        {
            foreach (var piece in _matches[_current])
            {
                SetPieceState(piece, isCurrent: false);
            }
        }

        _current = ordinal;
        foreach (var piece in _matches[_current])
        {
            SetPieceState(piece, isCurrent: true);
        }

        if (scroll)
        {
            ScrollToCurrent();
        }
    }

    private void SetPieceState(Piece piece, bool isCurrent)
    {
        piece.Run.Background = isCurrent ? _currentBrush : _matchBrush;
        if (isCurrent)
        {
            piece.Run.Foreground = _currentForeground;
        }
        else if (piece.HadForeground)
        {
            piece.Run.Foreground = piece.BaseForeground;
        }
        else
        {
            piece.Run.ClearValue(TextElement.ForegroundProperty);
        }
    }

    /*  Deferred to Loaded priority so freshly split blocks have re-measured
        before their positions are read. Same TranslatePoint math as the TOC's
        scroll-to-heading; the viewer keeps its ScrollViewer as a template part,
        so it is looked up in the visual tree. */
    private void ScrollToCurrent()
    {
        var ordinal = _current;
        Dispatcher.UIThread.Post(() =>
        {
            if (ordinal != _current || ordinal < 0 || ordinal >= _anchors.Count)
            {
                return;
            }

            var scroller = _viewer.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroller is null)
            {
                return;
            }

            var anchor = _anchors[ordinal];
            var point = anchor.Block.TranslatePoint(new Point(0, 0), _viewer);
            if (point is null)
            {
                return;
            }

            var lineOffset = anchor.TotalLines > 1
                ? anchor.Block.Bounds.Height * anchor.LineIndex / anchor.TotalLines
                : 0;
            var target = Math.Max(0, scroller.Offset.Y + point.Value.Y + lineOffset - 100);
            scroller.Offset = scroller.Offset.WithY(target);
            DebugLog.Write($"search scroll to match {ordinal + 1} offset {target:F1}");
        }, DispatcherPriority.Loaded);
    }
}
