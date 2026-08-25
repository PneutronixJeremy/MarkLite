using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.VisualTree;

namespace MarkLite.Rendering.Virtual;

/// <summary>One end of a selection: which block, and how far into that block's text.</summary>
/*  Addressed by block and character rather than by pixels or by control, for the
    same reason the scroll anchor is: the block a reader selected keeps its
    identity when the panel recycles its controls, re-measures it at a new width
    or re-parses the document underneath it. A selection that pointed at a
    TextBlock would evaporate the moment the reader scrolled far enough for that
    block to be recycled. */
internal readonly record struct SelectionPoint(int Block, int Offset)
    : IComparable<SelectionPoint>
{
    public int CompareTo(SelectionPoint other) =>
        Block != other.Block ? Block.CompareTo(other.Block) : Offset.CompareTo(other.Offset);

    public override string ToString() => $"{Block}:{Offset}";
}

/*  What the reader has selected, and what Ctrl+C gives them.

    Two decisions shape the whole class:

    - the selection is a MODEL range, not a set of highlighted controls, so it
      can cover blocks that have never been rendered. Dragging past the bottom
      of the window selects into a part of the document that has no controls at
      all, and copying it works anyway;
    - copy produces the MARKDOWN SOURCE between the endpoints, not the rendered
      text, which is what a reader wants to paste into another document: a
      selection crossing a table, a link or a code fence comes back as markdown
      rather than as flattened prose. There is no plain-text copy path.

    The endpoints themselves land on the nearest real character, so selecting
    just the words of a heading gives the words and not the "## " a reader never
    saw. Everything BETWEEN them is the file verbatim. */
internal sealed class DocumentSelection
{
    private readonly VirtualBlockPanel _panel;
    private readonly Func<MarkdownDocumentModel?> _model;

    /*  Rendered-text index per realized block, built on demand and dropped when
        the block is recycled. Only the blocks that are actually on screen ever
        get one: an offset is all the rest need. */
    private readonly Dictionary<int, BlockTextIndex> _indexes = [];

    private SelectionPoint? _anchor;
    private SelectionPoint? _focus;

    public DocumentSelection(VirtualBlockPanel panel, Func<MarkdownDocumentModel?> model)
    {
        _panel = panel;
        _model = model;
        _panel.Recycled += index => _indexes.Remove(index);
    }

    /// <summary>Raised whenever the range changed, so the adorner can repaint.</summary>
    public event Action? Changed;

    public bool IsEmpty => _anchor is null || _focus is null || _anchor.Value == _focus.Value;

    /// <summary>The lower endpoint in document order.</summary>
    public SelectionPoint Start =>
        _anchor is { } a && _focus is { } f ? (a.CompareTo(f) <= 0 ? a : f) : default;

    /// <summary>The upper endpoint in document order.</summary>
    public SelectionPoint End =>
        _anchor is { } a && _focus is { } f ? (a.CompareTo(f) <= 0 ? f : a) : default;

    public void Set(SelectionPoint anchor, SelectionPoint focus)
    {
        _anchor = anchor;
        _focus = focus;
        Changed?.Invoke();
    }

    public void SetFocus(SelectionPoint focus)
    {
        if (_anchor is null)
        {
            _anchor = focus;
        }
        _focus = focus;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_anchor is null && _focus is null)
        {
            return;
        }
        _anchor = null;
        _focus = null;
        Changed?.Invoke();
    }

    /// <summary>Drops the cached per-block indexes — for a document that was re-parsed or a tree
    /// that was rebuilt under an unchanged model.</summary>
    public void InvalidateIndexes() => _indexes.Clear();

    /// <summary>Selects the whole document. The last block's length comes from the model, so this
    /// does not need the end of the document rendered.</summary>
    public void SelectAll()
    {
        if (_model() is not { } model || model.Blocks.Count == 0)
        {
            return;
        }
        var last = model.Blocks.Count - 1;
        Set(new SelectionPoint(0, 0), new SelectionPoint(last, BlockLength(last)));
    }

    /// <summary>Where a point in the panel's content coordinates falls in the document. False
    /// when the block under it has no controls to measure against — which cannot happen for a
    /// point the reader can actually see, because everything visible is realized.</summary>
    public bool TryResolve(Point pointInPanel, out SelectionPoint result)
    {
        result = default;
        if (_model() is not { } model || model.Blocks.Count == 0)
        {
            return false;
        }

        var block = _panel.BlockAtContentOffset(pointInPanel.Y);
        if (block < 0)
        {
            return false;
        }
        if (_panel.GetRealized(block) is not { } container)
        {
            //  Above or below everything realized: the nearest end of that block
            //  is still a truthful answer for a drag in progress.
            result = new SelectionPoint(block, pointInPanel.Y < _panel.BlockOffset(block) ? 0 : BlockLength(block));
            return true;
        }

        if (container.TranslatePoint(new Point(0, 0), _panel) is not { } origin)
        {
            return false;
        }
        var index = IndexFor(block, container);
        result = new SelectionPoint(block, index.OffsetAt(container, pointInPanel - origin));
        return true;
    }

    /// <summary>The rendered-text index of a realized block, built once per realization.</summary>
    public BlockTextIndex IndexFor(int block, BlockContainer container)
    {
        if (_indexes.TryGetValue(block, out var existing))
        {
            return existing;
        }
        var built = BlockTextIndex.Build(container);
        _indexes[block] = built;
        return built;
    }

    /// <summary>Characters in a block's text, whether or not it is realized.</summary>
    public int BlockLength(int block)
    {
        if (_panel.GetRealized(block) is { } container)
        {
            return IndexFor(block, container).Length;
        }
        return _model()?.BlockText(block, HtmlComments.Visible).Length ?? 0;
    }

    /// <summary>The markdown source the selection covers. Empty when nothing is selected.</summary>
    public string CopyText()
    {
        if (IsEmpty || _model() is not { } model || model.Blocks.Count == 0)
        {
            return string.Empty;
        }

        var start = Start;
        var end = End;
        var last = model.Blocks.Count - 1;

        /*  A selection that reaches an END of the document takes that end of the
            file with it, rather than the first and last block's own extents:
            front matter, a trailing newline and anything else the blocks do not
            claim is still part of what the reader asked for when they pressed
            Ctrl+A. */
        var from = start.Block == 0 && start.Offset == 0
            ? 0
            : model.SourceOffset(start.Block, start.Offset, atEnd: false, HtmlComments.Visible);
        var to = end.Block >= last && end.Offset >= BlockLength(last)
            ? model.Text.Length
            : model.SourceOffset(end.Block, end.Offset, atEnd: true, HtmlComments.Visible);

        from = Math.Clamp(from, 0, model.Text.Length);
        to = Math.Clamp(to, from, model.Text.Length);
        return model.Text[from..to];
    }

    /// <summary>Compact description for the debug channel: "12:5-14:80 (203 chars)".</summary>
    public string Describe()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }
        return $"{Start}-{End} ({CopyText().Length} chars)";
    }
}
