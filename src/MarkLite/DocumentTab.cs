using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MarkView.Avalonia;

namespace MarkLite;

/*  State bag for one open document. Each tab owns its viewer — the rendered
    control tree stays alive across tab switches, which keeps switching instant
    and preserves layout — plus its own file watcher and search state.
    MainWindow orchestrates; nothing here reaches into other tabs.

    A detached viewer never lays out, so post-layout passes (TOC control
    collection) would see an empty tree: background re-renders are therefore
    DEFERRED — PendingText holds the newest content and is rendered when the
    tab becomes active. */
internal sealed class DocumentTab : IDisposable
{
    public required MarkdownViewer Viewer { get; init; }
    public required DocumentWatcher Watcher { get; init; }
    public required DocumentSearch Search { get; init; }
    public required Border StripItem { get; init; }
    public required TextBlock StripLabel { get; init; }

    /// <summary>Full path of the loaded file; null when the last load failed.</summary>
    public string? FilePath { get; set; }

    public string? CurrentText { get; set; }

    /// <summary>Content that arrived while the tab was inactive; rendered on activation.</summary>
    public string? PendingText { get; set; }

    /// <summary>Stale-banner message for this tab; null when the file is healthy.</summary>
    public string? StaleMessage { get; set; }

    public string SearchTerm { get; set; } = string.Empty;

    /// <summary>Scroll offset captured when the tab is deactivated, restored on activation.</summary>
    public double SavedScrollY { get; set; }

    /// <summary>Set once the viewer's ScrollChanged has been hooked (needs an applied template).</summary>
    public bool ScrollHooked { get; set; }

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
