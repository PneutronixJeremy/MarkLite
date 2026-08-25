using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MarkView.Avalonia;

namespace MarkLite;

/*  Case-insensitive substring search over the RENDERED document, for the
    classic viewer — which renders the whole document at once, so its control
    tree is the document. Highlighting is HighlightSession's run splitting; the
    work here is finding the text blocks, numbering the matches and scrolling to
    the current one.

    Clear() reverts highlighting by restoring the recorded inline snapshots.
    After a re-render those records point at discarded controls, so the owner
    must call Detach() (forget without undoing) and Apply() again on the fresh
    tree. Moving the current match only swaps Background/Foreground on
    already-split pieces, which repaints without re-measuring.

    Dies with the classic renderer at the cutover; VirtualDocumentSearch is the
    model-backed replacement. */
internal sealed class DocumentSearch : IDocumentSearch
{
    /*  Scroll target for a match: the containing block, plus — for code blocks,
        which can be hundreds of lines tall — the match's line position, so the
        scroll lands near the line rather than at the block top. */
    private sealed record Anchor(TextBlock Block, int LineIndex, int TotalLines);

    private readonly MarkdownViewer _viewer;

    private readonly List<List<HighlightPiece>> _matches = [];
    private readonly List<Anchor> _anchors = [];
    private HighlightSession _session = new();
    private int _current = -1;

    private IBrush _matchBrush = Brushes.Yellow;
    private IBrush _currentBrush = Brushes.Orange;
    private IBrush _currentForeground = Brushes.Black;

    public DocumentSearch(MarkdownViewer viewer)
    {
        _viewer = viewer;
    }

    public int Count => _matches.Count;

    public int CurrentOrdinal => _current;

    public int HighlightedCount => _matches.Count(static pieces => pieces.Count > 0);

    public void Apply(string term, IBrush matchBrush, IBrush currentBrush, IBrush currentForeground,
        bool scrollToCurrent)
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
            .Where(static block => !HighlightSession.IsChrome(block))
            .ToList();

        foreach (var block in blocks)
        {
            var text = HighlightSession.RenderedText(block);
            if (text.Length == 0)
            {
                continue;
            }

            var ranges = HighlightSession.FindRanges(text, term);
            if (ranges.Count == 0)
            {
                continue;
            }

            var firstOrdinal = _matches.Count;
            var totalLines = HighlightSession.CountNewlines(text, text.Length) + 1;
            foreach (var range in ranges)
            {
                _matches.Add([]);
                _anchors.Add(new Anchor(
                    block, HighlightSession.CountNewlines(text, range.Start), totalLines));
            }
            _session.Split(block, text, ranges, firstOrdinal, _matchBrush, Record);
        }

        if (_matches.Count > 0)
        {
            SetCurrent(0, scrollToCurrent);
        }
    }

    public void Clear()
    {
        _session.Undo();
        Forget();
    }

    public void Detach()
    {
        _session.Forget();
        Forget();
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

    private void Forget()
    {
        _session = new HighlightSession();
        _matches.Clear();
        _anchors.Clear();
        _current = -1;
    }

    private void Record(int ordinal, HighlightPiece piece)
    {
        if (ordinal >= 0 && ordinal < _matches.Count)
        {
            _matches[ordinal].Add(piece);
        }
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

    private void SetPieceState(HighlightPiece piece, bool isCurrent)
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
            piece.Run.ClearValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty);
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
