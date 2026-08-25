using System;
using System.Collections.Generic;
using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

using MarkView.Avalonia;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;

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
    }

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

        var realizer = new BlockRealizer(model, MarkLitePipeline.Shared, GetExtensions(), baseUri);
        realizer.LinkClicked += OnRealizerLinkClicked;

        Model = model;
        _panel.Load(model, realizer);

        DebugLog.Write($"render: parsed {model.Blocks.Count} blocks in {parsedMs} ms, "
            + $"{model.Headings.Count} headings");
    }

    /// <summary>Drops the document and everything realized from it.</summary>
    public void Clear()
    {
        Model = null;
        _panel.Clear();
    }

    /// <summary>Drops every realized control and every measured height, keeping the document —
    /// for a theme or font change, where the same text lays out differently.</summary>
    public void ResetLayout()
    {
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
        //  The panel virtualizes against this ScrollViewer's offset and viewport.
        _panel.Scroller = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
    }

    /*  MarkView's own viewer resolves same-document links itself and re-raises
        everything else as the routed event. Same contract here, against the
        model's anchors instead of a rendered anchor table. */
    private void OnRealizerLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        if (e.Url.StartsWith('#'))
        {
            if (!ScrollToModelAnchor(e.Url[1..]))
            {
                DebugLog.Write($"anchor not found: {e.Url}");
            }
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
