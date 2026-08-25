using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MarkView.Avalonia;

namespace MarkLite;

/*  State bag for one open document. Each tab owns its viewer, file watcher and
    search state; MainWindow orchestrates, and nothing here reaches into other
    tabs.

    Only the ACTIVE tab's viewer holds a rendered control tree. An inactive tab
    keeps its text (CurrentText) and where the reader was (SavedScrollY), and
    nothing else — see ActivateTab. So CurrentText is the single source of
    truth for content: a background reload writes it and stops there. */
internal sealed class DocumentTab : IDisposable
{
    public required MarkdownViewer Viewer { get; init; }
    public required DocumentWatcher Watcher { get; init; }
    public required DocumentSearch Search { get; init; }
    public required Border StripItem { get; init; }
    public required TextBlock StripLabel { get; init; }

    /// <summary>Full path of the loaded file; null when the last load failed.</summary>
    public string? FilePath { get; set; }

    /// <summary>The document's text — what gets rendered whenever this tab is activated.</summary>
    public string? CurrentText { get; set; }

    /// <summary>Stale-banner message for this tab; null when the file is healthy.</summary>
    public string? StaleMessage { get; set; }

    public string SearchTerm { get; set; } = string.Empty;

    /// <summary>Scroll offset captured when the tab is deactivated, restored on activation.</summary>
    public double SavedScrollY { get; set; }

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

    public void Dispose()
    {
        Watcher.Dispose();
    }
}
