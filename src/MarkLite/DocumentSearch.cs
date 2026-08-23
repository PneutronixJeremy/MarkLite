using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;

namespace MarkLite;

/*  Case-insensitive substring search over the rendered document. Matches are
    highlighted by splitting text runs at match boundaries and giving the match
    pieces a background brush — the highlight is part of the text layout, so it
    wraps, scrolls and reflows with the document; no overlay geometry or scroll
    synchronization is involved. Two inline systems are handled: prose blocks
    are CTextBlock trees of CInline (split CRuns, recurse into CSpans), fenced
    code blocks are SelectableTextBlocks with Avalonia Run inlines.

    Clear() reverts highlighting by restoring the pre-split inline lists from
    undo records. After a re-render those records point at discarded controls,
    so the owner must call Detach() (forget without undoing) and Apply() again
    on the fresh tree. Moving the current match only swaps Background/Foreground
    on already-split pieces, which repaints without re-measuring. */
internal sealed class DocumentSearch
{
    private sealed record Piece(StyledElement Element, bool HadForeground, IBrush? BaseForeground);

    /*  Scroll target for a match: the containing block control, plus — for
        code blocks, which can be hundreds of lines tall — the match's line
        position so the scroll lands near the line, not the block top. */
    private sealed record Anchor(Control Block, int LineIndex, int TotalLines);

    private static readonly AvaloniaProperty[] CRunCopiedProperties =
    [
        CInline.ForegroundProperty,
        CInline.BackgroundProperty,
        CInline.FontFamilyProperty,
        CInline.FontSizeProperty,
        CInline.FontStyleProperty,
        CInline.FontWeightProperty,
        CInline.FontStretchProperty,
        CInline.IsUnderlineProperty,
        CInline.IsStrikethroughProperty,
        CInline.TextVerticalAlignmentProperty,
    ];

    private static readonly AvaloniaProperty[] RunCopiedProperties =
    [
        TextElement.ForegroundProperty,
        TextElement.BackgroundProperty,
        TextElement.FontFamilyProperty,
        TextElement.FontSizeProperty,
        TextElement.FontStyleProperty,
        TextElement.FontWeightProperty,
        TextElement.FontStretchProperty,
    ];

    private readonly MarkdownScrollViewer _viewer;

    private readonly List<List<Piece>> _matches = [];
    private readonly List<Anchor> _anchors = [];
    private readonly List<(CTextBlock Block, AvaloniaList<CInline> Content)> _blockUndo = [];
    private readonly List<(CSpan Span, IEnumerable<CInline> Content)> _spanUndo = [];
    private readonly List<(SelectableTextBlock Block, List<Inline> Inlines)> _codeUndo = [];
    private int _current = -1;

    private IBrush _matchBrush = Brushes.Yellow;
    private IBrush _currentBrush = Brushes.Orange;
    private IBrush _currentForeground = Brushes.Black;

    public DocumentSearch(MarkdownScrollViewer viewer)
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

        /*  Materialize the block list before mutating: highlighting swaps
            content collections, which changes the visual tree that
            GetVisualDescendants is lazily walking. */
        var blocks = _viewer.GetVisualDescendants().OfType<Control>()
            .Where(static c => c is CTextBlock
                || (c is SelectableTextBlock s && s.Classes.Contains("CodeBlockText")))
            .ToList();

        foreach (var block in blocks)
        {
            if (block is CTextBlock proseBlock)
            {
                var text = proseBlock.Text;
                var ranges = FindRanges(text, term);
                if (ranges.Count == 0)
                {
                    continue;
                }

                var firstOrdinal = _matches.Count;
                foreach (var _ in ranges)
                {
                    _matches.Add([]);
                    _anchors.Add(new Anchor(proseBlock, 0, 1));
                }
                HighlightProseBlock(proseBlock, ranges, firstOrdinal);
            }
            else if (block is SelectableTextBlock codeBlock)
            {
                var text = ExtractCodeText(codeBlock);
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
                    _anchors.Add(new Anchor(codeBlock, CountNewlines(text, range.Start), totalLines));
                }
                HighlightCodeBlock(codeBlock, ranges, firstOrdinal);
            }
        }

        if (_matches.Count > 0)
        {
            SetCurrent(0, scrollToCurrent);
        }
    }

    /// <summary>Reverts all highlighting by restoring the pre-split inline lists.</summary>
    public void Clear()
    {
        foreach (var (span, content) in _spanUndo)
        {
            span.Content = content;
        }
        foreach (var (block, content) in _blockUndo)
        {
            block.Content = content;
        }
        foreach (var (block, inlines) in _codeUndo)
        {
            if (block.Inlines is { } collection)
            {
                collection.Clear();
                collection.AddRange(inlines);
            }
        }
        Detach();
    }

    /// <summary>Forgets all state without undoing — call when the document was re-rendered
    /// and the recorded controls no longer exist in the tree.</summary>
    public void Detach()
    {
        _spanUndo.Clear();
        _blockUndo.Clear();
        _codeUndo.Clear();
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
        that spans [offset, offset + length), clipped to run-local indexes.
        Ranges arrive in ascending order, so iteration can stop at the first
        range starting past the run. */
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

    #region prose blocks (CTextBlock / CInline)

    private void HighlightProseBlock(CTextBlock block, List<(int Start, int End)> ranges, int firstOrdinal)
    {
        /*  The Content property setter is the only public path that makes
            CTextBlock rebuild its text geometry, so the top-level list is
            always replaced — reusing untouched inline instances — instead of
            being mutated in place. */
        var original = block.Content;
        var replacement = new List<CInline>();
        var offset = 0;
        foreach (var inline in original)
        {
            ProcessInline(inline, replacement, ranges, firstOrdinal, ref offset);
        }
        _blockUndo.Add((block, original));
        block.Content = new AvaloniaList<CInline>(replacement);
    }

    private void ProcessInline(CInline inline, List<CInline> output,
        List<(int Start, int End)> ranges, int firstOrdinal, ref int offset)
    {
        switch (inline)
        {
            /*  CLineBreak derives from CRun and must not be split; it
                contributes its "\n" to the concatenated text. */
            case CLineBreak lineBreak:
                offset += lineBreak.Text?.Length ?? 0;
                output.Add(lineBreak);
                break;

            case CRun run:
            {
                var text = run.Text ?? string.Empty;
                var segments = OverlappingSegments(ranges, firstOrdinal, offset, text.Length);
                if (segments.Count == 0)
                {
                    output.Add(run);
                }
                else
                {
                    SplitProseRun(run, text, segments, output);
                }
                offset += text.Length;
                break;
            }

            case CSpan span:
            {
                var originalChildren = span.Content;
                var replacement = new List<CInline>();
                var changed = false;
                foreach (var child in originalChildren)
                {
                    var before = replacement.Count;
                    ProcessInline(child, replacement, ranges, firstOrdinal, ref offset);
                    changed |= replacement.Count != before + 1 || !ReferenceEquals(replacement[before], child);
                }
                if (changed)
                {
                    _spanUndo.Add((span, originalChildren));
                    span.Content = replacement;
                }
                output.Add(span);
                break;
            }

            /*  CImage (" $$Image$$ ") and CInlineUIContainer ("") contribute
                their AsString length but are not highlightable — a match that
                falls inside one is counted yet has no visible pieces. */
            default:
                offset += inline.AsString().Length;
                output.Add(inline);
                break;
        }
    }

    private void SplitProseRun(CRun run, string text,
        List<(int Start, int End, int Ordinal)> segments, List<CInline> output)
    {
        var cursor = 0;
        foreach (var (start, end, ordinal) in segments)
        {
            if (start > cursor)
            {
                output.Add(CloneProseRun(run, text[cursor..start]));
            }

            var piece = CloneProseRun(run, text[start..end]);
            _matches[ordinal].Add(new Piece(piece, piece.IsSet(CInline.ForegroundProperty), piece.Foreground));
            piece.Background = _matchBrush;
            output.Add(piece);
            cursor = end;
        }
        if (cursor < text.Length)
        {
            output.Add(CloneProseRun(run, text[cursor..]));
        }
    }

    private static CRun CloneProseRun(CRun source, string text)
    {
        var clone = new CRun { Text = text };
        foreach (var property in CRunCopiedProperties)
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

    #endregion

    #region code blocks (SelectableTextBlock / Run)

    private static string ExtractCodeText(SelectableTextBlock block)
    {
        return block.Inlines is { Count: > 0 } inlines
            ? string.Concat(inlines.Select(InlineText))
            : string.Empty;
    }

    private static string InlineText(Inline inline)
    {
        return inline switch
        {
            Run run => run.Text ?? string.Empty,
            LineBreak => "\n",
            _ => string.Empty,
        };
    }

    private void HighlightCodeBlock(SelectableTextBlock block, List<(int Start, int End)> ranges, int firstOrdinal)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
        {
            return;
        }

        var snapshot = inlines.ToList();
        var replacement = new List<Inline>();
        var offset = 0;
        foreach (var item in snapshot)
        {
            if (item is Run run)
            {
                var text = run.Text ?? string.Empty;
                var segments = OverlappingSegments(ranges, firstOrdinal, offset, text.Length);
                if (segments.Count == 0)
                {
                    replacement.Add(run);
                }
                else
                {
                    SplitCodeRun(run, text, segments, replacement);
                }
                offset += text.Length;
            }
            else
            {
                offset += InlineText(item).Length;
                replacement.Add(item);
            }
        }

        _codeUndo.Add((block, snapshot));
        inlines.Clear();
        inlines.AddRange(replacement);
    }

    private void SplitCodeRun(Run run, string text,
        List<(int Start, int End, int Ordinal)> segments, List<Inline> output)
    {
        var cursor = 0;
        foreach (var (start, end, ordinal) in segments)
        {
            if (start > cursor)
            {
                output.Add(CloneCodeRun(run, text[cursor..start]));
            }

            var piece = CloneCodeRun(run, text[start..end]);
            _matches[ordinal].Add(new Piece(piece, piece.IsSet(TextElement.ForegroundProperty), piece.Foreground));
            piece.Background = _matchBrush;
            output.Add(piece);
            cursor = end;
        }
        if (cursor < text.Length)
        {
            output.Add(CloneCodeRun(run, text[cursor..]));
        }
    }

    private static Run CloneCodeRun(Run source, string text)
    {
        var clone = new Run(text);
        foreach (var property in RunCopiedProperties)
        {
            if (source.IsSet(property))
            {
                clone.SetValue(property, source.GetValue(property));
            }
        }
        return clone;
    }

    #endregion

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
        switch (piece.Element)
        {
            case CRun proseRun:
                proseRun.Background = isCurrent ? _currentBrush : _matchBrush;
                if (isCurrent)
                {
                    proseRun.Foreground = _currentForeground;
                }
                else if (piece.HadForeground)
                {
                    proseRun.Foreground = piece.BaseForeground;
                }
                else
                {
                    proseRun.ClearValue(CInline.ForegroundProperty);
                }
                break;

            case Run codeRun:
                codeRun.Background = isCurrent ? _currentBrush : _matchBrush;
                if (isCurrent)
                {
                    codeRun.Foreground = _currentForeground;
                }
                else if (piece.HadForeground)
                {
                    codeRun.Foreground = piece.BaseForeground;
                }
                else
                {
                    codeRun.ClearValue(TextElement.ForegroundProperty);
                }
                break;
        }
    }

    /*  Deferred to Loaded priority so freshly split blocks have re-measured
        before positions are read. Block positions are stable across the split
        (inline changes rarely change block height), so TranslatePoint math
        matches the TOC's scroll-to-heading approach. */
    private void ScrollToCurrent()
    {
        var ordinal = _current;
        Dispatcher.UIThread.Post(() =>
        {
            if (ordinal != _current || ordinal < 0 || ordinal >= _anchors.Count)
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
            var target = Math.Max(0, _viewer.ScrollValue.Y + point.Value.Y + lineOffset - 100);
            _viewer.ScrollValue = new Vector(_viewer.ScrollValue.X, target);
            DebugLog.Write($"search scroll to match {ordinal + 1} offset {target:F1}");
        }, DispatcherPriority.Loaded);
    }
}
