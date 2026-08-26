using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using MarkView.Avalonia;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Inlines;

namespace MarkLite.Rendering.Virtual;

/*  A MarkdownViewer that never renders a whole document.

    It subclasses MarkdownViewer purely to inherit the chrome: the control
    template (and with it PART_ScrollViewer), MarkView's theme, and the
    LinkClicked routed event the window already listens to. Everything the base
    class does with content is bypassed — Content is a VirtualBlockPanel that
    realizes blocks as they come near the viewport.

    Because of that bypass, the base class's content properties must never be
    assigned on an instance of this type: Markdown, Pipeline, BaseUri and
    Source all trigger the base render path, which sets Content to its own tree
    and throws the panel away. Markdown is guarded outright; the others are
    simply not used — the pipeline and base URI are passed to Load instead. */
internal sealed class VirtualMarkdownView : MarkdownViewer
{
    private readonly VirtualBlockPanel _panel = new();
    private BlockRealizer? _realizer;

    /*  Selection and pointer state. MarkView keeps both inside the viewer too,
        but its selection layer indexes the WHOLE rendered document, which is the
        one thing this view never has — so the model-addressed replacement lives
        here and the base class's copy stays empty and unused. */
    private readonly DocumentSelection _selection;
    private readonly DispatcherTimer _autoScroll;
    private bool _dragging;
    private Point _dragPointInPanel;
    private Point _dragPointInViewport;
    private Point _lastHoverPoint = new(double.NaN, double.NaN);

    static VirtualMarkdownView()
    {
        /*  Loud rather than mysterious: assigning Markdown here silently
            replaced the virtual panel with a fully realized document, which
            looks like "virtualization does not work" rather than like a bug at
            the call site. */
        MarkdownProperty.Changed.AddClassHandler<VirtualMarkdownView>((_, e) =>
        {
            if (e.NewValue is not null)
            {
                throw new InvalidOperationException(
                    "VirtualMarkdownView renders through Load(text); setting Markdown would " +
                    "replace the virtualizing panel with a fully realized document.");
            }
        });
    }

    public VirtualMarkdownView()
    {
        Content = _panel;
        _selection = new DocumentSelection(_panel, () => Model);
        _panel.AttachSelection(_selection);

        /*  Dragging past the edge of the window keeps selecting: the timer
            scrolls, the panel realizes what comes into view, and the focus is
            recomputed from the pointer position the reader is still holding. A
            selection that stopped at the last visible line would make selecting
            more than a screenful impossible. */
        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _autoScroll.Tick += (_, _) => AutoScrollStep();

        /*  Tunnelling, not bubbling. MarkView's own viewer subscribes to the
            same events on its content and would otherwise get first refusal —
            and its handlers act on a selection layer that has no entries here. */
        AddHandler(PointerPressedEvent, OnViewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnViewPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnViewPointerReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>What the reader has selected, addressed by block and character.</summary>
    public DocumentSelection Selection => _selection;

    /// <summary>The ScrollViewer the document scrolls in. Null until the template has been
    /// applied, which happens on first attachment to the visual tree.</summary>
    public ScrollViewer? Scroller => _panel.Scroller;

    /// <summary>Raised whenever the reader scrolls. Rides the template's ScrollViewer, so it
    /// starts firing only once that exists; callers must tolerate the gap.</summary>
    public event EventHandler? ViewScrollChanged;

    /// <summary>The parsed document: blocks, headings, anchors, table of contents.</summary>
    public MarkdownDocumentModel? Model { get; private set; }

    public VirtualBlockPanel Panel => _panel;

    /// <summary>Parses and shows a document. Cheap by design — no controls are built until the
    /// panel decides which blocks are near the viewport.</summary>
    public void Load(string text, Uri? baseUri = null)
    {
        var timer = Stopwatch.StartNew();

        var model = MarkdownDocumentModel.Parse(text, MarkLitePipeline.Shared, TableOfContentsMaxDepth);
        var parsedMs = timer.ElapsedMilliseconds;

        /*  One realizer for the life of the view. A reload re-points it at the
            new model rather than building a second one, so controls carried
            over by the panel keep working against the renderer that made
            them. */
        if (_realizer is null)
        {
            _realizer = new BlockRealizer(model, MarkLitePipeline.Shared, GetExtensions(), baseUri);
            _realizer.LinkClicked += OnRealizerLinkClicked;
        }
        else
        {
            _realizer.Rebind(model);
        }

        Model = model;
        //  Block indices and rendered text both change; a selection or an index
        //  cached against the old document means nothing now.
        _selection.Clear();
        _selection.InvalidateIndexes();
        _panel.Load(model, _realizer);

        DebugLog.Write($"render: parsed {model.Blocks.Count} blocks in {parsedMs} ms, "
            + $"{model.Headings.Count} headings");
    }

    /// <summary>Drops the document and everything realized from it.</summary>
    public void Clear()
    {
        Model = null;
        _realizer = null;
        _selection.Clear();
        _selection.InvalidateIndexes();
        EndDrag();
        _panel.Clear();
    }

    /// <summary>Drops every realized control and every measured height, keeping the document —
    /// for a theme or font change, where the same text lays out differently.</summary>
    public void ResetLayout()
    {
        /*  The text is the same but the controls are not, so the per-block
            indexes have to be rebuilt; the RANGE is still meaningful, because it
            is addressed by block and character. */
        _selection.InvalidateIndexes();
        _panel.ResetLayout();
    }

    /// <summary>Scrolls to an anchor slug using the model's anchor table. Returns false when the
    /// document has no such anchor.</summary>
    public bool ScrollToModelAnchor(string slug, double margin = -8)
    {
        if (Model is null || !Model.Anchors.TryGetValue(slug, out var blockIndex))
        {
            return false;
        }
        _panel.ScrollToBlock(blockIndex, margin);
        return true;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        /*  Re-templating (a theme switch does it) hands over a different
            ScrollViewer, so the old subscription is dropped rather than left
            firing from a control nothing scrolls any more. */
        if (_panel.Scroller is { } previous)
        {
            previous.ScrollChanged -= OnScrollerScrollChanged;
        }
        //  The panel virtualizes against this ScrollViewer's offset and viewport.
        _panel.Scroller = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        if (_panel.Scroller is { } scroller)
        {
            scroller.ScrollChanged += OnScrollerScrollChanged;
        }
    }

    private void OnScrollerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        ViewScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    // ────────────────────────────────────────────── pointer and links

    /// <summary>Follows a link the way a click on it would, from a point in this view's
    /// coordinates. The scripted counterpart of a mouse click — no input is injected.</summary>
    public bool ClickAt(Point pointInView)
    {
        if (FindLink(pointInView) is not { } link)
        {
            DebugLog.Write($"click at {pointInView.X:F0},{pointInView.Y:F0}: no link");
            return false;
        }
        Follow(link);
        return true;
    }

    /// <summary>Clicks the nth link of a realized block, from the middle of the rectangle its own
    /// text layout says it occupies. The scripted stand-in for a mouse click: the point is worked
    /// out from the layout and then fed through the same hit test a real click uses, so the hit
    /// test is exercised rather than bypassed. Returns the URL followed, or null.</summary>
    public string? ClickLinkInBlock(int blockIndex, int ordinal)
    {
        if (_panel.GetRealized(blockIndex) is not { } container)
        {
            return null;
        }

        var seen = 0;
        foreach (var textBlock in container.GetVisualDescendants().OfType<TextBlock>())
        {
            if (textBlock.TextLayout is not { } layout)
            {
                continue;
            }
            foreach (var (link, start, length) in HyperlinkHitTest.Links(textBlock))
            {
                if (seen++ != ordinal)
                {
                    continue;
                }
                var rects = layout.HitTestTextRange(start, length);
                foreach (var rect in rects)
                {
                    var middle = new Point(
                        rect.X + textBlock.Padding.Left + (rect.Width / 2),
                        rect.Y + textBlock.Padding.Top + (rect.Height / 2));
                    if (textBlock.TranslatePoint(middle, this) is not { } inView)
                    {
                        continue;
                    }
                    var url = link.NavigateUri?.ToString();
                    DebugLog.Write($"click-link block {blockIndex} #{ordinal} at "
                        + $"{inView.X:F0},{inView.Y:F0}");
                    return ClickAt(inView) ? url : null;
                }
            }
        }
        return null;
    }

    /*  Screen coordinates of things a pointer would aim at. Only the debug
        channel asks: a check that has to exercise the pointer plumbing itself —
        press, drag, release, hover — cannot compute a target from outside the
        process, because where a character ends up on screen is the outcome of
        wrapping, the theme's metrics and the panel's own layout. The app is the
        only thing that knows, so it says. */

    /// <summary>Where one character of a realized block is drawn, in screen pixels: just inside
    /// its left edge, vertically centred on its line. Aiming here and hit-testing back gives the
    /// same offset, which is what makes a synthesised drag assertable.</summary>
    public PixelPoint? ScreenPointOfText(int blockIndex, int offset)
    {
        if (_panel.GetRealized(blockIndex) is not { } container)
        {
            return null;
        }
        var index = _selection.IndexFor(blockIndex, container);
        if (!index.TryLocate(offset, out var entry, out var local))
        {
            return null;
        }
        foreach (var rect in BlockTextIndex.HighlightRects(entry.Block, local, local + 1))
        {
            return ToScreen(entry.Block, new Point(rect.X + 1, rect.Y + (rect.Height / 2)));
        }
        return null;
    }

    /// <summary>Where the nth link of a realized block is drawn, in screen pixels: the middle of
    /// the first rectangle its own text layout reports for it.</summary>
    public PixelPoint? ScreenPointOfLink(int blockIndex, int ordinal)
    {
        if (_panel.GetRealized(blockIndex) is not { } container)
        {
            return null;
        }

        var seen = 0;
        foreach (var textBlock in container.GetVisualDescendants().OfType<TextBlock>())
        {
            if (textBlock.TextLayout is not { } layout)
            {
                continue;
            }
            foreach (var (_, start, length) in HyperlinkHitTest.Links(textBlock))
            {
                if (seen++ != ordinal)
                {
                    continue;
                }
                foreach (var rect in layout.HitTestTextRange(start, length))
                {
                    return ToScreen(textBlock, new Point(
                        rect.X + textBlock.Padding.Left + (rect.Width / 2),
                        rect.Y + textBlock.Padding.Top + (rect.Height / 2)));
                }
            }
        }
        return null;
    }

    private PixelPoint? ToScreen(Visual from, Point pointInFrom) =>
        from.TranslatePoint(pointInFrom, this) is { } inView ? this.PointToScreen(inView) : null;

    private void OnViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        /*  The scrollbar is INSIDE this view, so a press on its thumb, track or
            arrows tunnels through here first. Claiming it would start a text
            drag and capture the pointer, leaving the thumb unable to move — the
            scrollbar looks dead to the mouse while the wheel still works. The
            selection is deliberately NOT cleared: dragging the scrollbar to see
            the far end of a selection must not destroy it. */
        if (e.Source is Visual scrollPart && scrollPart.FindAncestorOfType<ScrollBar>() is not null)
        {
            return;
        }

        /*  A code block keeps its own selection: the renderer puts a
            SelectableTextBlock inside it, which handles dragging, Ctrl+C and the
            context menu for its own text. Starting a document drag there would
            take that away — so the event is left alone, and the document
            selection only ever treats a code block as something a range passes
            THROUGH. */
        if (e.Source is Visual source && source.FindAncestorOfType<SelectableTextBlock>() is not null)
        {
            _selection.Clear();
            return;
        }

        if (FindLink(e.GetPosition(this)) is not null)
        {
            //  Left for the release: a press on a link must not clear a selection
            //  the reader is about to copy, and a click is press AND release on
            //  the same link.
            e.Handled = true;
            return;
        }

        if (!TryResolve(e, out var point))
        {
            _selection.Clear();
            return;
        }

        _selection.Set(point, point);
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnViewPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdateHoverCursor(e.GetPosition(this));

        if (!_dragging)
        {
            return;
        }

        _dragPointInPanel = e.GetPosition(_panel);
        _dragPointInViewport = e.GetPosition(this);
        if (TryResolve(e, out var point))
        {
            _selection.SetFocus(point);
        }

        /*  The timer runs only while the pointer is outside the viewport, so a
            drag that stays on screen costs nothing.
            Deliberately not a one-shot scroll per move event: a reader holding
            the pointer still below the window expects the document to keep
            coming. */
        _autoScroll.IsEnabled = OverscrollSpeed() != 0;
    }

    private void OnViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        var wasDragging = _dragging;
        EndDrag();
        e.Pointer.Capture(null);

        if (!wasDragging && FindLink(e.GetPosition(this)) is { } link)
        {
            Follow(link);
            e.Handled = true;
            return;
        }

        if (wasDragging)
        {
            /*  A click that never moved is not a selection: collapse it so the
                highlight does not linger as a zero-width sliver, and so the next
                Ctrl+C copies nothing rather than one stale character.  */
            if (_selection.IsEmpty)
            {
                _selection.Clear();
            }
            e.Handled = true;
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        _autoScroll.IsEnabled = false;
    }

    /*  One step of a drag that has left the window. The pointer has not moved,
        but the content under it has, so the focus is recomputed from the panel
        position the pointer now corresponds to — which is why the drag point is
        kept in BOTH coordinate spaces. */
    private void AutoScrollStep()
    {
        var speed = OverscrollSpeed();
        if (_panel.Scroller is not { } scroller || speed == 0)
        {
            _autoScroll.IsEnabled = false;
            return;
        }

        var before = scroller.Offset.Y;
        scroller.Offset = scroller.Offset.WithY(Math.Max(0, before + speed));
        var moved = scroller.Offset.Y - before;
        if (moved == 0)
        {
            //  At an end of the document: nothing more to select in that
            //  direction, and no reason to keep waking up.
            _autoScroll.IsEnabled = false;
            return;
        }

        _dragPointInPanel = _dragPointInPanel.WithY(_dragPointInPanel.Y + moved);
        if (_selection.TryResolve(_dragPointInPanel, out var point))
        {
            _selection.SetFocus(point);
        }
    }

    /*  Pixels per tick, and which way. Proportional to how far past the edge the
        pointer is, capped, so a small overshoot creeps and a big one moves. */
    private double OverscrollSpeed()
    {
        if (!_dragging || _panel.Scroller is not { } scroller)
        {
            return 0;
        }
        const double MaxStep = 40;
        var y = _dragPointInViewport.Y;
        if (y < 0)
        {
            return -Math.Min(MaxStep, 4 + (-y / 4));
        }
        if (y > scroller.Viewport.Height)
        {
            return Math.Min(MaxStep, 4 + ((y - scroller.Viewport.Height) / 4));
        }
        return 0;
    }

    private bool TryResolve(PointerEventArgs e, out SelectionPoint point) =>
        _selection.TryResolve(e.GetPosition(_panel), out point);

    /*  The hand cursor is set on the PANEL rather than on this view, and that is
        not incidental: MarkView's own pointer handler also writes a cursor onto
        the viewer on every move, and would reset ours. Avalonia resolves the
        cursor from the element under the pointer upward, so the panel — which is
        below the viewer and above every block — wins outright. */
    private void UpdateHoverCursor(Point pointInView)
    {
        /*  Throttled by distance: a hit test per pointer move is a text layout
            hit test per move, and a link's boundary is characters wide, so
            re-testing after a couple of pixels is as accurate as the reader can
            perceive and a fraction of the work. */
        if (Math.Abs(pointInView.X - _lastHoverPoint.X) < 2
            && Math.Abs(pointInView.Y - _lastHoverPoint.Y) < 2)
        {
            return;
        }
        _lastHoverPoint = pointInView;
        SetHandCursor(FindLink(pointInView) is not null);
    }

    private void SetHandCursor(bool hand)
    {
        var wanted = hand ? new Cursor(StandardCursorType.Hand) : null;
        if ((_panel.Cursor is null) != (wanted is null))
        {
            _panel.Cursor = wanted;
        }
    }

    private void Follow(MarkdownHyperlink link)
    {
        var url = link.NavigateUri?.ToString();
        if (string.IsNullOrEmpty(url))
        {
            return;
        }
        DebugLog.Write($"link clicked: {url}");
        OnRealizerLinkClicked(this, new LinkClickedEventArgs(url));
    }

    /*  MarkdownHyperlink is a Span, so there is no control to click and the text
        under the pointer has to be hit-tested. Only the block the pointer is
        actually over is examined — the whole reason this replaces MarkView's own
        hit test, which walks an index of the entire rendered document. */
    private MarkdownHyperlink? FindLink(Point pointInView)
    {
        if (FindTextBlock(pointInView, out var local) is { } textBlock)
        {
            return HyperlinkHitTest.At(textBlock, local);
        }
        //  An image link: a control was hit rather than text.
        if (FindControl<Image>(pointInView) is { } image)
        {
            return HyperlinkHitTest.AtControl(image);
        }
        return null;
    }

    /*  Which drawn text is under a point, found through the PANEL'S OWN
        geometry rather than through visual hit testing.

        Not incidental: the panel already knows which block owns a content
        offset, and that answer is the one the selection uses, so the link hit
        test and the selection agree on where the pointer is by construction. A
        hit test would also have to reckon with the blocks that are transparent
        to it — a Border with no background, the adorner, the gutter — and would
        answer differently depending on which of those happened to be on top. */
    private TextBlock? FindTextBlock(Point pointInView, out Point pointInBlock) =>
        FindControl<TextBlock>(pointInView, out pointInBlock);

    private T? FindControl<T>(Point pointInView) where T : Control =>
        FindControl<T>(pointInView, out _);

    private T? FindControl<T>(Point pointInView, out Point pointInControl) where T : Control
    {
        pointInControl = default;
        if (this.TranslatePoint(pointInView, _panel) is not { } inPanel)
        {
            return null;
        }
        var block = _panel.BlockAtContentOffset(inPanel.Y);
        if (block < 0 || _panel.GetRealized(block) is not { } container)
        {
            return null;
        }

        foreach (var candidate in container.GetVisualDescendants().OfType<T>())
        {
            if (candidate.TranslatePoint(new Point(0, 0), _panel) is not { } origin)
            {
                continue;
            }
            if (new Rect(origin, candidate.Bounds.Size).Contains(inPanel))
            {
                pointInControl = inPanel - origin;
                return candidate;
            }
        }
        return null;
    }

    /*  MarkView's own viewer resolves same-document links itself and re-raises
        everything else as the routed event. Same contract here, against the
        model's anchors instead of a rendered anchor table. */
    private void OnRealizerLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        if (e.Url.StartsWith('#'))
        {
            if (ScrollToModelAnchor(e.Url[1..]))
            {
                /*  Same wording the window logs when an anchor arrives through
                    the hyperlink command, so one log line covers both routes —
                    the fragment links a click resolves here, and the ones a
                    sidebar entry or a scripted "anchor" command sends. */
                DebugLog.Write($"anchor link: {e.Url}");
                return;
            }
            DebugLog.Write($"anchor not found: {e.Url}");
            return;
        }
        e.RoutedEvent = LinkClickedEvent;
        RaiseEvent(e);
    }

    private IReadOnlyList<IMarkViewExtension> GetExtensions()
    {
        var extensions = new List<IMarkViewExtension>(
            MarkdownViewerDefaults.Extensions.Count + Extensions.Count);
        /*  Same order as the base viewer's render path: global defaults first,
            then per-instance, skipping exact duplicates. Registration order
            decides which renderer claims a block type. */
        var seen = new HashSet<IMarkViewExtension>(ReferenceEqualityComparer.Instance);
        foreach (var extension in MarkdownViewerDefaults.Extensions)
        {
            if (seen.Add(extension))
            {
                extensions.Add(extension);
            }
        }
        foreach (var extension in Extensions)
        {
            if (seen.Add(extension))
            {
                extensions.Add(extension);
            }
        }
        return extensions;
    }
}
