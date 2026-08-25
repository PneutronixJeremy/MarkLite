using System;
using System.Collections.Generic;

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

    /// <summary>Raised when a block gains controls, so a search can highlight it.</summary>
    public event Action<int, BlockContainer>? Realized;

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
        set => ScrollToBlock(value.Block, -value.OffsetWithin);
    }

    /// <summary>Replaces the document. Cached heights are kept: a reload of a lightly edited
    /// file re-uses every block whose text did not change.</summary>
    public void Load(MarkdownDocumentModel model, BlockRealizer realizer)
    {
        RecycleAll();

        _model = model;
        _realizer = realizer;
        _heights = new double[model.Blocks.Count];
        _measured = new bool[model.Blocks.Count];
        _offsets = new double[model.Blocks.Count + 1];
        _measuredTotal = 0;
        _measuredCount = 0;
        _offsetsDirty = true;

        SeedHeightsFromCache();
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
        _heightCache.Clear();
        RecycleAll();
        Array.Clear(_measured);
        _measuredTotal = 0;
        _measuredCount = 0;
        _offsetsDirty = true;
        SeedHeightsFromCache();
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

    /// <summary>Scrolls a block to the top of the viewport, plus a margin (negative lifts the
    /// block down from the very edge).</summary>
    public void ScrollToBlock(int index, double margin = 0)
    {
        if (_scroller is null || _model is null || _model.Blocks.Count == 0)
        {
            return;
        }
        var index2 = Math.Clamp(index, 0, _model.Blocks.Count - 1);
        var target = Math.Max(0, BlockOffset(index2) + margin);
        _scroller.Offset = _scroller.Offset.WithY(target);
        UpdateRealization();
    }

    /// <summary>The realized controls for a block, or null when it is not realized.</summary>
    public BlockContainer? GetRealized(int index) =>
        _realized.TryGetValue(index, out var container) ? container : null;

    /// <summary>Every realized block, for callers that need to walk what is on screen.</summary>
    public IReadOnlyDictionary<int, BlockContainer> RealizedBlocks => _realized;

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
            _stickyAnchorBlock = block;
            _stickyAnchorWithin = within;
            _stickyAnchorLastOffset = double.NaN;
            //  Generous cap: a safety net, not the normal exit — the anchor is
            //  released as soon as its offset stops moving.
            _stickyAnchorPasses = 40;
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

        var childSize = new Size(width, double.PositiveInfinity);
        foreach (var (index, container) in _realized)
        {
            container.Measure(childSize);
            RecordHeight(index, container.DesiredSize.Height);
        }

        EnsureOffsets();

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
        foreach (var (index, container) in _realized)
        {
            container.Arrange(new Rect(0, _offsets[index], finalSize.Width, _heights[index]));
        }

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
    }

    private void RecycleAll()
    {
        foreach (var container in _realized.Values)
        {
            container.SizeChanged -= OnBlockSizeChanged;
        }
        _realized.Clear();
        Children.Clear();
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
        for (var index = 0; index < _heights.Length; index++)
        {
            _offsets[index] = running;
            running += (_measured[index] ? _heights[index] : estimate) + BlockSpacing;
        }
        //  One past the end: the extent, without a trailing gap.
        _offsets[^1] = Math.Max(0, running - BlockSpacing);
    }

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
