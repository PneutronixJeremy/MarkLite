using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MarkLite.Rendering.Virtual;

/*  Lays out a whole document while only ever holding the blocks near the
    viewport.

    The panel knows a height for every block: a real one for blocks it has
    measured, an estimate for the rest. Those heights add up to the scroll
    extent, so the scrollbar is the right length from the first frame and gets
    more accurate as blocks are realized. Only blocks inside the realization
    window — the viewport grown by one viewport height in each direction — have
    controls at all; everything else is a number in an array.

    Two things make that honest rather than merely cheap:

    - heights are cached per (block hash, layout width), so a block that has
      been seen once keeps its true height even after it is recycled, and a
      live reload of a lightly edited document re-uses almost every entry;
    - when a block ABOVE the viewport turns out to be taller or shorter than
      estimated, the scroll offset is corrected by the same amount, so what the
      reader is looking at does not jump. */
internal sealed class VirtualBlockPanel : Panel
{
    /// <summary>Vertical gap between blocks — MarkView's own RootPanel spacing.</summary>
    private const double BlockSpacing = 8;

    /// <summary>Height assumed for a block before anything has been measured.</summary>
    private const double InitialHeightGuess = 40;

    /// <summary>How far beyond the viewport blocks are kept realized, in viewports.</summary>
    private const double OvershootViewports = 1.0;

    private MarkdownDocumentModel? _model;
    private BlockRealizer? _realizer;

    private double[] _heights = [];
    private bool[] _measured = [];
    private double[] _offsets = [];
    private bool _offsetsDirty = true;

    private readonly Dictionary<int, BlockContainer> _realized = [];
    private readonly List<int> _scratch = [];

    /*  Real heights survive recycling and re-parsing here. Width is part of the
        key because a narrower column wraps more lines: the same block is a
        different height at a different width. */
    private readonly Dictionary<(ulong Hash, int Width), double> _heightCache = [];

    private double _measuredTotal;
    private int _measuredCount;
    private double _lastWidth;

    /*  The line-number strip. A permanent child: it is never recycled, and the
        width it reserves is held on BOTH sides of the document whether the
        numbers are showing or not — so the document stays centred, and turning
        the numbers on re-wraps nothing and invalidates no cached height. */
    private readonly GutterPanel _gutter;

    private ScrollViewer? _scroller;
    private double _pendingScrollCorrection;
    private double? _pendingScrollTarget;
    private bool _scrollUpdateQueued;

    /*  A resize invalidates every height at once, so the offsets above the
        viewport keep moving for several passes as blocks are re-measured and
        the running estimate settles. Correcting once is not enough — the block
        the reader was on is held until its offset stops moving. */
    private int _stickyAnchorBlock = -1;
    private double _stickyAnchorWithin;
    private double _stickyAnchorLastOffset;
    private int _stickyAnchorPasses;

    private int _lastScrollTargetBlock = -1;

    /*  Old block index to new, from the most recent reload. Empty whenever the
        document did not come from a reload — a tab switch loads into a viewer
        that was cleared, and there is nothing to align against. */
    private int[] _alignment = [];

    public VirtualBlockPanel()
    {
        _gutter = new GutterPanel(this);
        Children.Add(_gutter);
    }

    /// <summary>Raised when a block gains controls, so a search can highlight it.</summary>
    public event Action<int, BlockContainer>? Realized;

    /// <summary>Raised when a block loses its controls, so a search can forget the highlights it
    /// had put in them. Nothing needs undoing — the controls left the tree.</summary>
    public event Action<int>? Recycled;

    /// <summary>The ScrollViewer this panel is scrolled by; realization follows its offset.</summary>
    public ScrollViewer? Scroller
    {
        get => _scroller;
        set
        {
            if (ReferenceEquals(_scroller, value))
            {
                return;
            }
            if (_scroller is not null)
            {
                _scroller.ScrollChanged -= OnScrollChanged;
            }
            _scroller = value;
            if (_scroller is not null)
            {
                _scroller.ScrollChanged += OnScrollChanged;
            }
        }
    }

    /// <summary>The document, for callers that need its blocks' source lines.</summary>
    public MarkdownDocumentModel? Model => _model;

    /// <summary>Redraws the line-number strip — after the toggle changes, or after anything
    /// that moves blocks without re-laying out the panel.</summary>
    public void InvalidateGutter() => _gutter.InvalidateVisual();

    /// <summary>1-based source lines of the first and last block intersecting the viewport, or
    /// (0, 0) when there is no document.</summary>
    public (int First, int Last) VisibleSourceLines
    {
        get
        {
            if (_model is null || _model.Blocks.Count == 0 || _scroller is null)
            {
                return (0, 0);
            }
            var top = BlockAtOffset(_scroller.Offset.Y);
            var bottom = BlockAtOffset(_scroller.Offset.Y + _scroller.Viewport.Height);
            return (_model.Blocks[Math.Max(0, top)].StartLine,
                _model.Blocks[Math.Max(0, bottom)].EndLine);
        }
    }

    public int BlockCount => _model?.Blocks.Count ?? 0;

    /// <summary>How many blocks have a real measured height rather than an estimate.</summary>
    public int MeasuredBlockCount => _measuredCount;

    public int RealizedBlockCount => _realized.Count;

    /// <summary>Inclusive index range currently holding controls, or (-1, -1) when empty.</summary>
    public (int First, int Last) RealizedRange
    {
        get
        {
            var first = int.MaxValue;
            var last = -1;
            foreach (var index in _realized.Keys)
            {
                first = Math.Min(first, index);
                last = Math.Max(last, index);
            }
            return last < 0 ? (-1, -1) : (first, last);
        }
    }

    /// <summary>Index of the topmost block intersecting the viewport.</summary>
    public int FirstVisibleBlock => BlockAtOffset(_scroller?.Offset.Y ?? 0);

    /// <summary>Index of the block at a point <paramref name="tolerance"/> pixels below the
    /// viewport top. A jump to a heading deliberately leaves a small margin above it, so
    /// "which section am I in" has to look just past that margin or it answers with the
    /// section before.</summary>
    public int BlockNearViewportTop(double tolerance) =>
        BlockAtOffset((_scroller?.Offset.Y ?? 0) + tolerance);

    /// <summary>Where the reader is, in a form that survives re-parsing: which block, and how
    /// far into it. Restoring it after an edit elsewhere in the file leaves the view still.</summary>
    public (int Block, double OffsetWithin) ScrollAnchor
    {
        get
        {
            var index = FirstVisibleBlock;
            return index < 0 ? (0, 0) : (index, (_scroller?.Offset.Y ?? 0) - BlockOffset(index));
        }
        /*  Positive margin: the reader was that far INTO the block, so the
            block's top sits that far above the viewport top. */
        set => ScrollToBlock(value.Block, value.OffsetWithin);
    }

    /// <summary>Replaces the document. Cached heights are kept, and every realized block whose
    /// source text did not change keeps its controls: a reload of a lightly edited file leaves
    /// the screen alone except where the edit actually landed.</summary>
    public void Load(MarkdownDocumentModel model, BlockRealizer realizer)
    {
        /*  What is on screen right now, and the map that says where each of
            those blocks went. Both have to be taken while the OLD model is
            still installed: a reload re-parses into a new block list, and an
            index means nothing across that boundary on its own. */
        var carried = new List<(int OldIndex, BlockContainer Container)>(_realized.Count);
        if (_model is not null)
        {
            foreach (var (index, container) in _realized)
            {
                if (index < _model.Blocks.Count)
                {
                    carried.Add((index, container));
                }
            }
        }
        _realized.Clear();

        /*  Every block index in play a moment ago now means something else, so
            anything keyed by one has to let go. Carried containers are NOT
            re-announced as realized: their controls were built before, and a
            listener that re-decorated them would decorate them twice. The
            window re-applies the search after a load for that reason. */
        foreach (var (oldIndex, _) in carried)
        {
            Recycled?.Invoke(oldIndex);
        }

        _alignment = _model is null ? [] : model.AlignFrom(_model);

        _model = model;
        _realizer = realizer;
        _heights = new double[model.Blocks.Count];
        _measured = new bool[model.Blocks.Count];
        _offsets = new double[model.Blocks.Count + 1];
        _measuredTotal = 0;
        _measuredCount = 0;
        _offsetsDirty = true;

        SeedHeightsFromCache();

        var reused = 0;
        foreach (var (oldIndex, container) in carried)
        {
            /*  The alignment, never a hash search: a document that repeats
                itself would otherwise hand a container to some identical block
                elsewhere in the file, and the block that really changed would
                still count as re-used. */
            var newIndex = TranslateFromPreviousLoad(oldIndex);
            if (newIndex >= 0 && !_realized.ContainsKey(newIndex))
            {
                container.BlockIndex = newIndex;
                _realized[newIndex] = container;
                reused++;
            }
            else
            {
                container.SizeChanged -= OnBlockSizeChanged;
                Children.Remove(container);
            }
        }
        if (carried.Count > 0)
        {
            var aligned = 0;
            foreach (var mapped in _alignment)
            {
                if (mapped >= 0)
                {
                    aligned++;
                }
            }
            DebugLog.Write($"reload: reused {reused} of {carried.Count} containers, "
                + $"{aligned} of {_alignment.Length} blocks aligned");
        }

        InvalidateMeasure();
    }

    /// <summary>Drops the document entirely — every control and the model with it. The height
    /// cache is KEPT: it is keyed by block hash, so re-loading the same document comes back with
    /// its real heights and the scrollbar does not jump.</summary>
    public void Clear()
    {
        RecycleAll();
        _model = null;
        _realizer = null;
        _alignment = [];
        _heights = [];
        _measured = [];
        _offsets = [];
        _measuredTotal = 0;
        _measuredCount = 0;
        _offsetsDirty = true;
        InvalidateMeasure();
    }

    /// <summary>Drops every realized control and every measured height, keeping the document.
    /// For a theme or font change, where the same text lays out differently.</summary>
    public void ResetLayout()
    {
        //  Captured against the OLD heights, put back once the new ones exist.
        var (block, within) = ScrollAnchor;

        _heightCache.Clear();
        RecycleAll();
        Array.Clear(_measured);
        _measuredTotal = 0;
        _measuredCount = 0;
        _offsetsDirty = true;
        SeedHeightsFromCache();
        HoldAnchor(block, within);
        InvalidateMeasure();
    }

    /// <summary>Top of a block in content coordinates. Exact when every block above it has been
    /// measured, estimated otherwise.</summary>
    public double BlockOffset(int index)
    {
        if (_model is null || _model.Blocks.Count == 0)
        {
            return 0;
        }
        EnsureOffsets();
        return _offsets[Math.Clamp(index, 0, _offsets.Length - 1)];
    }

    /// <summary>How tall a block is: measured where it has been measured, the running estimate
    /// otherwise. Enough to place something vertically INSIDE a block — a search match halfway
    /// down a long code fence — without needing the block realized first.</summary>
    public double BlockHeight(int index)
    {
        if (index < 0 || index >= _heights.Length)
        {
            return 0;
        }
        return _measured[index] ? _heights[index] : EstimatedHeight;
    }

    /// <summary>The current scroll offset, for callers that only want to report it.</summary>
    public double ScrollOffset => _scroller?.Offset.Y ?? 0;

    /// <summary>Scrolls a block to the top of the viewport, plus a margin (negative lifts the
    /// block down from the very edge).</summary>
    public void ScrollToBlock(int index, double margin = 0) => ScrollToBlock(index, margin, 2);

    /*  A jump aims at an offset that is partly guesswork: every unmeasured
        block above the target contributes an estimate. Landing there realizes
        and measures blocks, which moves the target's real offset — so the jump
        is repeated once layout has settled, and again if it is still moving.
        Bounded by the pass count, and skipped entirely when every block above
        the target already has a measured height, because then nothing can
        move. */
    private void ScrollToBlock(int index, double margin, int correctionPasses)
    {
        if (_scroller is null || _model is null || _model.Blocks.Count == 0)
        {
            return;
        }
        var target = Math.Clamp(index, 0, _model.Blocks.Count - 1);
        _lastScrollTargetBlock = target;

        var offset = Math.Max(0, BlockOffset(target) + margin);
        _scroller.Offset = _scroller.Offset.WithY(offset);
        UpdateRealization();

        if (correctionPasses <= 0 || IsMeasuredThrough(target))
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_scroller is null || _model is null || target >= _model.Blocks.Count)
            {
                return;
            }
            var corrected = Math.Max(0, BlockOffset(target) + margin);
            if (Math.Abs(corrected - _scroller.Offset.Y) > 0.5)
            {
                ScrollToBlock(target, margin, correctionPasses - 1);
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>True when the block and everything above it have real measured heights, so its
    /// offset cannot move any further.</summary>
    public bool IsMeasuredThrough(int index)
    {
        var last = Math.Min(index, _measured.Length - 1);
        for (var i = 0; i <= last; i++)
        {
            if (!_measured[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether one block's height is measured rather than estimated.</summary>
    public bool IsMeasured(int index) =>
        index >= 0 && index < _measured.Length && _measured[index];

    /// <summary>The block the last <see cref="ScrollToBlock"/> aimed at, or -1. Paired with
    /// <see cref="BlockOffset"/> it says how far a jump actually landed from its target.</summary>
    public int LastScrollTargetBlock => _lastScrollTargetBlock;

    /// <summary>Where a block of the document loaded BEFORE the current one ended up, or -1 when
    /// it changed, was deleted, or there was no previous document.</summary>
    public int TranslateFromPreviousLoad(int previousIndex) =>
        previousIndex >= 0 && previousIndex < _alignment.Length ? _alignment[previousIndex] : -1;

    /// <summary>The realized controls for a block, or null when it is not realized.</summary>
    public BlockContainer? GetRealized(int index) =>
        _realized.TryGetValue(index, out var container) ? container : null;

    /// <summary>Every realized block, for callers that need to walk what is on screen.</summary>
    public IReadOnlyDictionary<int, BlockContainer> RealizedBlocks => _realized;

    /*  Pins the reader to one block across a run of layout passes. Used when
        every height in the document becomes a guess at once — a width change, a
        theme or font change — where correcting the offset a single time leaves
        the reader wherever the estimates happened to land. The generous pass
        cap is a safety net, not the normal exit: the hold is released as soon
        as the block's offset stops moving. */
    private void HoldAnchor(int block, double within)
    {
        _stickyAnchorBlock = block;
        _stickyAnchorWithin = within;
        _stickyAnchorLastOffset = double.NaN;
        _stickyAnchorPasses = 40;
    }

    // ────────────────────────────────────────────────────────── layout

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_model is null || _model.Blocks.Count == 0)
        {
            return default;
        }

        var width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? _lastWidth
            : availableSize.Width;
        if (width <= 0)
        {
            return default;
        }

        var widthChanged = Math.Abs(width - _lastWidth) > 0.5;
        if (widthChanged && _lastWidth > 0)
        {
            var (block, within) = ScrollAnchor;
            HoldAnchor(block, within);
        }
        if (widthChanged)
        {
            /*  A different column width wraps text differently, so every
                measured height is now a guess again — which moves every offset
                in the document, including the one the reader is sitting at. The
                anchor captured above (against the OLD offsets) is put back once
                the new ones exist. The cache is keyed by width, so a width seen
                before comes back with its real heights instantly. */
            _lastWidth = width;
            Array.Clear(_measured);
            _measuredTotal = 0;
            _measuredCount = 0;
            SeedHeightsFromCache();
            _offsetsDirty = true;
        }

        UpdateRealization();

        //  Growth above the viewport has to be given back as scroll offset, or
        //  the text under the reader's eyes slides away.
        var anchorIndex = FirstVisibleBlock;
        var anchorOffsetBefore = anchorIndex >= 0 ? BlockOffset(anchorIndex) : 0;

        var childSize = new Size(ContentWidth(width), double.PositiveInfinity);
        foreach (var (index, container) in _realized)
        {
            container.Measure(childSize);
            RecordHeight(index, container.DesiredSize.Height);
        }

        EnsureOffsets();
        _gutter.Measure(new Size(GutterPanel.Reserve, TotalExtent));

        if (_stickyAnchorPasses > 0 && _stickyAnchorBlock >= 0 && _heights.Length > 0)
        {
            /*  Hold the reader on the block they were on. The offset WITHIN it
                is kept only as far as its new height allows — at a different
                width it is a different block. */
            var index = Math.Clamp(_stickyAnchorBlock, 0, _heights.Length - 1);
            var within = Math.Clamp(_stickyAnchorWithin, 0, Math.Max(0, _heights[index]));
            var target = Math.Max(0, _offsets[index] + within);

            _stickyAnchorPasses--;
            if (!double.IsNaN(_stickyAnchorLastOffset)
                && Math.Abs(target - _stickyAnchorLastOffset) < 1)
            {
                //  Settled: the offsets above the viewport have stopped moving.
                _stickyAnchorPasses = 0;
                _stickyAnchorBlock = -1;
            }
            _stickyAnchorLastOffset = target;
            _pendingScrollTarget = target;
        }
        else if (anchorIndex >= 0)
        {
            var shift = _offsets[anchorIndex] - anchorOffsetBefore;
            if (Math.Abs(shift) > 0.5)
            {
                _pendingScrollCorrection += shift;
            }
        }

        return new Size(width, TotalExtent);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_model is null)
        {
            return finalSize;
        }

        EnsureOffsets();
        var contentWidth = ContentWidth(finalSize.Width);
        foreach (var (index, container) in _realized)
        {
            container.Arrange(new Rect(
                GutterPanel.Reserve, _offsets[index], contentWidth, _heights[index]));
        }
        //  As tall as the whole document, so a block's offset is the same number
        //  in the gutter's coordinates as in the panel's.
        _gutter.Arrange(new Rect(0, 0, GutterPanel.Reserve, TotalExtent));
        _gutter.InvalidateVisual();

        SchedulePendingScroll();

        return new Size(finalSize.Width, Math.Max(finalSize.Height, TotalExtent));
    }

    /*  The scroll correction cannot be applied from inside the layout pass that
        computed it: the ScrollViewer arranges AROUND its content and re-clamps
        Offset against the extent it works out afterwards, so an offset written
        during ArrangeOverride is simply overwritten. Posting it means the write
        lands once layout has settled, on an extent that already reflects the
        new heights.

        An absolute target (a resize putting the reader back on their block)
        wins over the running correction, which is only ever a nudge. */
    private void SchedulePendingScroll()
    {
        if (_scrollUpdateQueued || _scroller is null)
        {
            return;
        }
        if (_pendingScrollTarget is null && _pendingScrollCorrection == 0)
        {
            return;
        }

        _scrollUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollUpdateQueued = false;
            if (_scroller is null)
            {
                return;
            }

            var target = _pendingScrollTarget
                ?? (_pendingScrollCorrection != 0
                    ? _scroller.Offset.Y + _pendingScrollCorrection
                    : (double?)null);
            _pendingScrollTarget = null;
            _pendingScrollCorrection = 0;

            if (target is { } value && Math.Abs(value - _scroller.Offset.Y) > 0.5)
            {
                _scroller.Offset = _scroller.Offset.WithY(Math.Max(0, value));
                UpdateRealization();
            }
        }, DispatcherPriority.Loaded);
    }

    // ──────────────────────────────────────────────────── realization

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateRealization();
    }

    private void UpdateRealization()
    {
        if (_model is null || _realizer is null || _scroller is null || _model.Blocks.Count == 0)
        {
            return;
        }

        var viewport = _scroller.Viewport.Height;
        if (viewport <= 0)
        {
            return;
        }

        var top = _scroller.Offset.Y - (viewport * OvershootViewports);
        var bottom = _scroller.Offset.Y + viewport + (viewport * OvershootViewports);

        var first = BlockAtOffset(top);
        var last = BlockAtOffset(bottom);
        if (first < 0)
        {
            first = 0;
        }
        if (last < first)
        {
            last = first;
        }

        //  Recycle first: dropping what left the window keeps the child count
        //  bounded even when a jump moves the window a long way.
        _scratch.Clear();
        foreach (var index in _realized.Keys)
        {
            if (index < first || index > last)
            {
                _scratch.Add(index);
            }
        }
        foreach (var index in _scratch)
        {
            RecycleBlock(index);
        }
        _scratch.Clear();

        for (var index = first; index <= last; index++)
        {
            if (_realized.ContainsKey(index))
            {
                continue;
            }
            var container = _realizer.Realize(index);
            _realized[index] = container;
            Children.Add(container);
            container.SizeChanged += OnBlockSizeChanged;
            Realized?.Invoke(index, container);
        }
    }

    /*  Images, diagrams and maths arrive after their block was first measured;
        when they land the block changes height and everything below it moves. */
    private void OnBlockSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not BlockContainer container || !_realized.ContainsKey(container.BlockIndex))
        {
            return;
        }
        if (RecordHeight(container.BlockIndex, container.DesiredSize.Height))
        {
            InvalidateMeasure();
        }
    }

    private void RecycleBlock(int index)
    {
        if (!_realized.Remove(index, out var container))
        {
            return;
        }
        container.SizeChanged -= OnBlockSizeChanged;
        Children.Remove(container);
        Recycled?.Invoke(index);
    }

    private void RecycleAll()
    {
        //  Removed one by one, not Children.Clear(): the gutter is a child too
        //  and it outlives every document.
        var indices = _realized.Keys.ToArray();
        foreach (var container in _realized.Values)
        {
            container.SizeChanged -= OnBlockSizeChanged;
            Children.Remove(container);
        }
        _realized.Clear();
        foreach (var index in indices)
        {
            Recycled?.Invoke(index);
        }
    }

    // ─────────────────────────────────────────────────────── heights

    private bool RecordHeight(int index, double height)
    {
        if (double.IsNaN(height) || double.IsInfinity(height) || height < 0)
        {
            return false;
        }

        var changed = !_measured[index] || Math.Abs(_heights[index] - height) > 0.5;
        if (!_measured[index])
        {
            _measured[index] = true;
            _measuredCount++;
            _measuredTotal += height;
        }
        else if (changed)
        {
            _measuredTotal += height - _heights[index];
        }

        if (changed)
        {
            _heights[index] = height;
            _offsetsDirty = true;
            _heightCache[(_model!.Blocks[index].Hash, (int)Math.Round(_lastWidth))] = height;
        }
        return changed;
    }

    /*  Everything not measured yet gets the best guess available: its own
        cached height from a previous life at this width, otherwise the average
        of what has been measured, otherwise a fixed starting figure. */
    private void SeedHeightsFromCache()
    {
        if (_model is null)
        {
            return;
        }

        var width = (int)Math.Round(_lastWidth);
        for (var index = 0; index < _model.Blocks.Count; index++)
        {
            if (_measured[index])
            {
                continue;
            }
            if (_heightCache.TryGetValue((_model.Blocks[index].Hash, width), out var cached))
            {
                _heights[index] = cached;
                _measured[index] = true;
                _measuredCount++;
                _measuredTotal += cached;
            }
            else
            {
                _heights[index] = EstimatedHeight;
            }
        }
        _offsetsDirty = true;
    }

    private double EstimatedHeight =>
        _measuredCount > 0 ? _measuredTotal / _measuredCount : InitialHeightGuess;

    private double TotalExtent
    {
        get
        {
            EnsureOffsets();
            return _offsets.Length > 0 ? _offsets[^1] : 0;
        }
    }

    private void EnsureOffsets()
    {
        if (!_offsetsDirty || _model is null)
        {
            return;
        }
        _offsetsDirty = false;

        var estimate = EstimatedHeight;
        var running = 0.0;
        var previousDrawn = false;
        for (var index = 0; index < _heights.Length; index++)
        {
            var height = _measured[index] ? _heights[index] : estimate;
            /*  The gap goes BETWEEN drawn blocks, never around an empty one.
                Some top-level blocks render to no controls at all — raw HTML,
                YAML front matter, a link reference definition group — and
                MarkView's own root panel spaces its CHILDREN, so a block that
                contributes none costs nothing. Charging it the gap anyway
                pushed everything below it 8 px down, which is exactly what a
                capture comparison against the classic renderer caught on a
                document that opens with an <img> tag. */
            if (previousDrawn && height > 0)
            {
                running += BlockSpacing;
            }
            _offsets[index] = running;
            running += height;
            previousDrawn = previousDrawn || height > 0;
        }
        //  One past the end: the extent.
        _offsets[^1] = running;
    }

    /*  The document's own width: the panel's width less the strip reserved on
        each side. Never negative, and never so small that a block would be
        measured at nothing — a very narrow window keeps the text readable and
        lets the numbers overlap instead. */
    private static double ContentWidth(double panelWidth) =>
        Math.Max(120, panelWidth - (2 * GutterPanel.Reserve));

    /// <summary>Index of the block containing a content-space offset.</summary>
    private int BlockAtOffset(double y)
    {
        if (_model is null || _model.Blocks.Count == 0)
        {
            return -1;
        }
        EnsureOffsets();

        if (y <= 0)
        {
            return 0;
        }

        var low = 0;
        var high = _model.Blocks.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_offsets[middle] <= y)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        return low;
    }
}
