using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MarkLite.Rendering.Virtual;

using MarkView.Avalonia;

namespace MarkLite;

/*  Where the reader was, in a form that survives the document being re-parsed
    underneath them.

    A pixel offset alone stops meaning anything once a reload can insert fifty
    paragraphs above the viewport: every offset in the file moves, and the
    reader is left somewhere they never scrolled to. The block hash addresses
    content rather than position, so the paragraph they were reading comes back
    to the same place. Y is the fallback for the classic viewer, which has no
    blocks to address. */
internal readonly record struct ScrollRestore(double Y, ulong BlockHash, int BlockIndex, double OffsetWithin)
{
    /// <summary>A fresh document: nothing to put back.</summary>
    public static readonly ScrollRestore Top = new(0, 0, -1, 0);

    /// <summary>Whether a block anchor is available, as opposed to a bare pixel offset.</summary>
    public bool HasAnchor => BlockIndex >= 0;

    /// <summary>Whether putting this back would move the view at all.</summary>
    public bool MovesTheView => HasAnchor ? BlockIndex > 0 || OffsetWithin > 0 : Y > 0;

    public string Describe => HasAnchor ? $"{Y:F1} block {BlockIndex}" : $"{Y:F1}";
}

/*  State bag for one open document. Each tab owns its viewer, file watcher and
    search state; MainWindow orchestrates, and nothing here reaches into other
    tabs.

    Only the ACTIVE tab's viewer holds a rendered control tree. An inactive tab
    keeps its text (CurrentText) and where the reader was (SavedScroll), and
    nothing else — see ActivateTab. So CurrentText is the single source of
    truth for content: a background reload writes it and stops there. */
internal sealed class DocumentTab : IDisposable
{
    public required MarkdownViewer Viewer { get; init; }
    public required DocumentWatcher Watcher { get; init; }
    public required IDocumentSearch Search { get; init; }
    public required Border StripItem { get; init; }
    public required TextBlock StripLabel { get; init; }

    /// <summary>Full path of the loaded file; null when the last load failed.</summary>
    public string? FilePath { get; set; }

    /// <summary>The document's text — what gets rendered whenever this tab is activated.</summary>
    public string? CurrentText { get; set; }

    /// <summary>Stale-banner message for this tab; null when the file is healthy.</summary>
    public string? StaleMessage { get; set; }

    public string SearchTerm { get; set; } = string.Empty;

    /// <summary>Where the reader was when the tab was deactivated, restored on activation.</summary>
    public ScrollRestore SavedScroll { get; set; } = ScrollRestore.Top;

    /// <summary>Set once the viewer's ScrollChanged has been hooked (needs an applied template).</summary>
    public bool ScrollHooked { get; set; }

    /*  Headings for the sidebar: the viewer's own TableOfContents tree,
        flattened back to document order, paired positionally with the rendered
        heading controls (used for scroll position math). */
    public List<TocEntry> TocEntries { get; } = [];
    public List<Control> HeadingControls { get; } = [];

    public string DisplayName => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);

    /*  MarkdownViewer keeps its ScrollViewer private (template part
        PART_ScrollViewer); it exists only after the template has been applied,
        which happens on first attachment to the visual tree. Callers must
        tolerate null before that. */
    public ScrollViewer? Scroller
    {
        get
        {
            foreach (var descendant in Viewer.GetVisualDescendants())
            {
                if (descendant is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }
            }
            return null;
        }
    }

    public double ScrollY
    {
        get => Scroller?.Offset.Y ?? 0;
        set
        {
            if (Scroller is { } scrollViewer)
            {
                scrollViewer.Offset = scrollViewer.Offset.WithY(value);
            }
        }
    }

    /// <summary>Reads the reader's current position: a block anchor under the virtualizing
    /// viewer, a pixel offset under the classic one.</summary>
    public ScrollRestore CaptureScroll()
    {
        if (Viewer is VirtualMarkdownView { Model: { } model } virtualView && model.Blocks.Count > 0)
        {
            var (block, within) = virtualView.Panel.ScrollAnchor;
            block = Math.Clamp(block, 0, model.Blocks.Count - 1);
            return new ScrollRestore(ScrollY, model.Blocks[block].Hash, block, within);
        }
        return new ScrollRestore(ScrollY, 0, -1, 0);
    }

    /// <summary>Puts a captured position back. The anchor block is found by hash first — an edit
    /// elsewhere in the file renumbers the blocks — and by its old index when the block itself is
    /// gone. Returns the block landed on, or -1 when a pixel offset was used.</summary>
    public int RestoreScroll(ScrollRestore restore)
    {
        if (restore.HasAnchor
            && Viewer is VirtualMarkdownView { Model: { } model } virtualView
            && model.Blocks.Count > 0)
        {
            /*  Three ways of finding the same paragraph again, weakest last.
                After a reload the panel knows exactly where every surviving
                block went. After a tab switch it does not — the viewer was
                cleared — but the text is normally unchanged, so the hash finds
                it. And when the block itself is gone, its old index is the
                nearest thing to where the reader was. */
            var index = virtualView.Panel.TranslateFromPreviousLoad(restore.BlockIndex);
            if (index < 0)
            {
                index = model.FindBlockByHash(restore.BlockHash, restore.BlockIndex);
            }
            if (index < 0)
            {
                index = Math.Clamp(restore.BlockIndex, 0, model.Blocks.Count - 1);
            }
            virtualView.Panel.ScrollToBlock(index, restore.OffsetWithin);
            return index;
        }
        ScrollY = restore.Y;
        return -1;
    }

    public void Dispose()
    {
        Watcher.Dispose();
    }
}
