using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MarkLite.Rendering.Virtual;

/*  Search for a viewer that renders a fraction of its document.

    Two layers, and the split is the whole point:

    - the COUNT and the CURRENT MATCH come from the model (ModelSearch), so they
      describe the document. "3 of 373" means what it says however little of the
      file has controls, and F3 walks all 373 whether they are on screen or not;
    - the HIGHLIGHT is applied to realized blocks only, and a block realized
      later is highlighted as it arrives. Nothing has to be undone when a block
      is recycled — its controls are gone.

    The highlight deliberately re-runs the substring search on the block's own
    RENDERED text rather than trusting the projection's offsets. The two agree
    on every fixture, but the projection is a description of what the renderers
    do and the renderers are the authority; searching the real inline text means
    a divergence shows up as a logged count mismatch instead of as a highlight
    drawn over the wrong characters. */
internal sealed class VirtualDocumentSearch : IDocumentSearch
{
    /// <summary>Pixels between the viewport top and the current match after a jump.</summary>
    private const double CurrentMatchMargin = 100;

    private readonly VirtualMarkdownView _viewer;
    private readonly VirtualBlockPanel _panel;

    private ModelSearch? _search;
    private string _term = string.Empty;
    private int _current = -1;

    /*  One highlight session per realized block, so recycling a block is
        Remove() and nothing else. Pieces are indexed by match ordinal, which is
        what moving the current match needs and what a recycle has to null out. */
    private readonly Dictionary<int, HighlightSession> _sessions = [];
    private List<HighlightPiece>?[] _pieces = [];

    private readonly HashSet<int> _pending = [];
    private readonly HashSet<int> _mismatchLogged = [];
    private bool _flushQueued;

    private IBrush _matchBrush = Brushes.Yellow;
    private IBrush _currentBrush = Brushes.Orange;
    private IBrush _currentForeground = Brushes.Black;

    public VirtualDocumentSearch(VirtualMarkdownView viewer)
    {
        _viewer = viewer;
        /*  The panel outlives every document the viewer shows, so subscribing
            once here is enough; there is no per-render re-wiring to get wrong. */
        _panel = viewer.Panel;
        _panel.Realized += OnRealized;
        _panel.Recycled += OnRecycled;
    }

    public int Count => _search?.Count ?? 0;

    public int CurrentOrdinal => _current;

    public int HighlightedCount => _pieces.Count(static pieces => pieces is { Count: > 0 });

    public void Apply(string term, IBrush matchBrush, IBrush currentBrush, IBrush currentForeground,
        bool scrollToCurrent)
    {
        Clear();
        _matchBrush = matchBrush;
        _currentBrush = currentBrush;
        _currentForeground = currentForeground;

        if (string.IsNullOrEmpty(term) || _viewer.Model is not { } model)
        {
            return;
        }

        _term = term;
        /*  Comment visibility is passed in, not read inside the model: the
            projection has to describe the tree that is on screen, and comments
            are drawn or dropped while a control is BUILT. */
        _search = new ModelSearch(model, term, HtmlComments.Visible);
        _pieces = new List<HighlightPiece>?[_search.Count];

        /*  Whatever is realized right now, highlighted immediately: this runs
            from the find bar, not from inside a layout pass, so the containers
            are built and arranged and their text can be walked directly. */
        foreach (var index in _search.BlocksWithMatches)
        {
            HighlightBlock(index);
        }

        if (_search.Count > 0)
        {
            SetCurrent(0, scrollToCurrent);
        }
    }

    public void Clear()
    {
        foreach (var session in _sessions.Values)
        {
            session.Undo();
        }
        Forget();
    }

    /*  Same as Clear(), on purpose. Detach exists for a tree that is about to be
        thrown away — but a reload of this viewer CARRIES realized containers
        over to the new model (that is what keeps the screen still), so their
        split runs would survive into a document that no longer knows about
        them, and the next Apply would split the split. Undoing costs one pass
        over the handful of realized blocks; on containers that really are being
        discarded it is a no-op nobody can see. */
    public void Detach() => Clear();

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
        _sessions.Clear();
        _pieces = [];
        _pending.Clear();
        _mismatchLogged.Clear();
        _search = null;
        _term = string.Empty;
        _current = -1;
    }

    // ─────────────────────────────────────────────────── realization

    /*  A block gains controls during a layout pass, so the highlight is queued
        rather than applied: the code panel's inner TextBlock only appears once
        the ScrollViewer around it has been templated, and mutating inlines
        inside the measure that created them fights the pass that is running. */
    private void OnRealized(int index, BlockContainer container)
    {
        if (_search is null || !_search.TryGetBlockRange(index, out _, out _))
        {
            return;
        }
        _pending.Add(index);
        if (_flushQueued)
        {
            return;
        }
        _flushQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _flushQueued = false;
            if (_search is null)
            {
                _pending.Clear();
                return;
            }
            foreach (var pending in _pending)
            {
                HighlightBlock(pending);
            }
            _pending.Clear();
            //  The block the reader was sent to may have just arrived.
            ApplyCurrentEmphasis();
        }, DispatcherPriority.Loaded);
    }

    private void OnRecycled(int index)
    {
        if (_search is null)
        {
            return;
        }
        _pending.Remove(index);
        //  No undo: the container and its runs left the tree together.
        _sessions.Remove(index);
        if (_search.TryGetBlockRange(index, out var first, out var count))
        {
            for (var ordinal = first; ordinal < first + count && ordinal < _pieces.Length; ordinal++)
            {
                _pieces[ordinal] = null;
            }
        }
    }

    private void HighlightBlock(int index)
    {
        if (_search is null
            || !_search.TryGetBlockRange(index, out var first, out var expected)
            || _sessions.ContainsKey(index)
            || _panel.GetRealized(index) is not { } container)
        {
            return;
        }

        var limit = first + expected;
        var session = new HighlightSession();
        var ordinal = first;

        /*  Visual order is document order here: every renderer appends its
            controls in the order the source has them, so numbering the matches
            by the order the text blocks come out of the walk agrees with the
            model's numbering. */
        foreach (var textBlock in container.GetVisualDescendants().OfType<TextBlock>())
        {
            if (HighlightSession.IsChrome(textBlock))
            {
                continue;
            }
            var text = HighlightSession.RenderedText(textBlock);
            if (text.Length == 0)
            {
                continue;
            }
            var ranges = HighlightSession.FindRanges(text, _term);
            if (ranges.Count == 0)
            {
                continue;
            }
            session.Split(textBlock, text, ranges, ordinal, _matchBrush,
                (at, piece) => Record(at, limit, piece));
            ordinal += ranges.Count;
        }

        _sessions[index] = session;

        var rendered = ordinal - first;
        if (rendered != expected && _mismatchLogged.Add(index))
        {
            /*  Debug only, and once per block: the count the user sees is the
                model's either way. A run of these says the projection and the
                renderers have drifted apart on some construct. */
            DebugLog.Write($"search: block {index} projects {expected} matches, renders {rendered}");
        }

        if (_current >= first && _current < limit)
        {
            ApplyEmphasis(_current, isCurrent: true);
        }
    }

    /*  Pieces past the block's own ordinal range are dropped rather than
        recorded: they would otherwise be filed under the NEXT block's matches
        and light up the wrong one. They keep the plain match brush, so a block
        that renders more matches than the projection predicted still shows all
        of them. */
    private void Record(int ordinal, int limit, HighlightPiece piece)
    {
        if (ordinal < 0 || ordinal >= limit || ordinal >= _pieces.Length)
        {
            return;
        }
        (_pieces[ordinal] ??= []).Add(piece);
    }

    // ───────────────────────────────────────────────── current match

    private void SetCurrent(int ordinal, bool scroll)
    {
        ApplyEmphasis(_current, isCurrent: false);
        _current = ordinal;

        if (scroll)
        {
            ScrollToCurrent();
        }
        /*  The match may be in a block that has no controls yet. Scrolling to it
            realizes the block, the queued highlight splits its runs, and the
            emphasis lands on the pass after that — the same two-pass shape an
            anchor jump uses. */
        ApplyCurrentEmphasis(passes: 2);
    }

    private void ApplyCurrentEmphasis(int passes = 0)
    {
        if (_current < 0)
        {
            return;
        }
        if (_current < _pieces.Length && _pieces[_current] is { Count: > 0 })
        {
            ApplyEmphasis(_current, isCurrent: true);
            return;
        }
        if (passes <= 0)
        {
            return;
        }

        var ordinal = _current;
        Dispatcher.UIThread.Post(() =>
        {
            if (ordinal == _current)
            {
                ApplyCurrentEmphasis(passes - 1);
            }
        }, DispatcherPriority.Background);
    }

    private void ApplyEmphasis(int ordinal, bool isCurrent)
    {
        if (ordinal < 0 || ordinal >= _pieces.Length || _pieces[ordinal] is not { } pieces)
        {
            return;
        }
        foreach (var piece in pieces)
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
    }

    /*  The scroll target is the match's block, offset down by where the match
        sits inside it — which is what makes a hit halfway down a 200-line code
        fence land on screen instead of scrolling the top of the fence into view
        and leaving the match below the fold. The block's height is measured
        where it has been measured and estimated otherwise, and the panel
        re-aims a jump once the blocks it realized have real heights, so an
        estimate here only costs a correction pass. */
    private void ScrollToCurrent()
    {
        if (_search is null || _current < 0 || _current >= _search.Count)
        {
            return;
        }

        var match = _search.Matches[_current];
        var lineOffset = match.LineCount > 1
            ? _panel.BlockHeight(match.Block) * match.LineIndex / match.LineCount
            : 0;
        _panel.ScrollToBlock(match.Block, lineOffset - CurrentMatchMargin);
        DebugLog.Write($"search scroll to match {_current + 1} block {match.Block} "
            + $"offset {_panel.ScrollOffset:F1}");
    }
}
