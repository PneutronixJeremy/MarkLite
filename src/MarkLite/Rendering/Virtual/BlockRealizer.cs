using System;
using System.Collections.Generic;

using Avalonia.Controls;

using Markdig;

using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;

namespace MarkLite.Rendering.Virtual;

/*  Builds the controls for ONE top-level block on demand.

    MarkView renders a whole document in a single pass into
    AvaloniaRenderer.RootPanel. Nothing stops that renderer being asked for one
    block at a time instead — Write(block) appends the controls for just that
    block — so realization is: write one block, take whatever landed in the
    root panel, and move it into a container of its own.

    One renderer serves the whole document: every extension registration and
    pipeline.Setup must happen before the FIRST Write, because Markdig caches
    the renderer it picks per object type on first use. */
internal sealed class BlockRealizer
{
    private readonly AvaloniaRenderer _renderer;
    private readonly Dictionary<int, List<MarkdownDocumentModel.HeadingInfo>> _headingsByBlock = [];
    private readonly List<Control> _spill = [];
    private MarkdownDocumentModel _model;

    public BlockRealizer(
        MarkdownDocumentModel model,
        MarkdownPipeline pipeline,
        IReadOnlyList<IMarkViewExtension> extensions,
        Uri? baseUri)
    {
        _model = model;
        _renderer = new AvaloniaRenderer { BaseUri = baseUri };
        IndexHeadings();

        foreach (var extension in extensions)
        {
            extension.Register(_renderer);
        }
        pipeline.Setup(_renderer);
        /*  Extension count is worth a line: a viewer that silently lost one
            renders a plausible-looking document with the wrong renderers, which
            is far harder to spot than a crash. */
        DebugLog.Write($"realizer: {extensions.Count} extensions registered");

        _renderer.LinkClicked += (_, e) => LinkClicked?.Invoke(this, e);
    }

    /*  Points the same renderer at a re-parsed version of the same document.
        The renderer holds no per-document state — it is a set of block
        renderers and a base URI — so a live reload does not need a new one,
        and reusing it keeps the LinkClicked wiring (and the controls already
        built from it) valid instead of leaving an orphaned renderer alive
        behind every carried-over container. */
    public void Rebind(MarkdownDocumentModel model)
    {
        _model = model;
        _headingsByBlock.Clear();
        IndexHeadings();
    }

    private void IndexHeadings()
    {
        foreach (var heading in _model.Headings)
        {
            if (!_headingsByBlock.TryGetValue(heading.BlockIndex, out var list))
            {
                list = [];
                _headingsByBlock[heading.BlockIndex] = list;
            }
            list.Add(heading);
        }
    }

    /// <summary>Raised when a hyperlink inside any realized block is clicked.</summary>
    public event EventHandler<LinkClickedEventArgs>? LinkClicked;

    /// <summary>Builds the controls for one top-level block. Never returns null; a block that
    /// renders to nothing (raw HTML, front matter) gives an empty container.</summary>
    public BlockContainer Realize(int index)
    {
        var container = new BlockContainer { BlockIndex = index };
        var root = _renderer.RootPanel;

        _renderer.Write(_model.Blocks[index].Block);

        /*  A block can produce 0, 1 or 2 top-level controls (a footnote group
            writes a separator and the group), so take everything that landed
            rather than assuming one. The children have to leave the root panel
            before they can join another — a control with a parent cannot be
            added elsewhere. */
        _spill.Clear();
        _spill.AddRange(root.Children);
        root.Children.Clear();
        foreach (var control in _spill)
        {
            container.Children.Add(control);
        }
        _spill.Clear();

        RetagHeadings(container, index);
        return container;
    }

    /*  MarkView's HeadingRenderer slugs a heading through the renderer's
        SlugGenerator, which counts repeats. That counter assumes one pass over
        the document in order; here blocks are written in scroll order and the
        same block can be written many times, so the generator's numbering
        drifts and a heading realized twice would carry two different anchors.

        The model already knows every heading's real slug, so the freshly built
        heading controls are re-tagged from it. Order within a block is
        document order on both sides. */
    private void RetagHeadings(BlockContainer container, int index)
    {
        if (!_headingsByBlock.TryGetValue(index, out var headings))
        {
            return;
        }

        var position = 0;
        Retag(container);

        void Retag(Control control)
        {
            if (control is TextBlock textBlock && textBlock.Classes.Contains("markdown-heading"))
            {
                if (position < headings.Count)
                {
                    textBlock.Tag = headings[position].Slug;
                    position++;
                }
                return;
            }

            switch (control)
            {
                case Panel panel:
                    foreach (var child in panel.Children)
                    {
                        Retag(child);
                    }
                    break;
                case Border { Child: Control inner }:
                    Retag(inner);
                    break;
                case ContentControl { Content: Control content }:
                    Retag(content);
                    break;
                case Decorator { Child: Control child }:
                    Retag(child);
                    break;
            }
        }
    }
}

/*  Holds one top-level block's controls and remembers which block it is.
    A StackPanel with MarkView's own 8 px block spacing, so a block that
    renders to two controls looks exactly as it does in a full-document
    render; with the usual single control the spacing never applies. */
internal sealed class BlockContainer : StackPanel
{
    public BlockContainer()
    {
        Spacing = 8;
    }

    /*  Settable, not init-only: a live reload re-parses into new indices and
        carries unchanged containers across to whatever their block is called
        now. */
    /// <summary>Index into <see cref="MarkdownDocumentModel.Blocks"/>.</summary>
    public int BlockIndex { get; set; }
}
