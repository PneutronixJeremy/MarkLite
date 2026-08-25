using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MarkLite;

/*  Scripted control surface, live only when MARKLITE_DEBUG=1. Verification
    scripts send commands with "MarkLite.exe --cmd <text>", which the primary
    instance receives on the single-instance pipe and runs here on the UI
    thread. The point is to exercise the real code paths — the same methods the
    menus, keyboard and sidebar call — WITHOUT injecting keyboard or mouse
    input, which would fight the user for focus on their own desktop.

    Every command answers with a "[marklite] cmd …" line on stderr so a script
    can wait for the effect instead of sleeping, and "dump-state" writes one
    JSON line with everything a check might assert on. */
public partial class MainWindow
{
    internal void ExecuteDebugCommand(DebugCommand command)
    {
        if (!DebugLog.Enabled)
        {
            /*  Nothing to log to and nothing to script: a release launch
                without the debug flag ignores commands entirely. */
            return;
        }

        var text = command.Text.Trim();
        var split = text.IndexOf(' ');
        var verb = split < 0 ? text : text[..split];
        var argument = split < 0 ? string.Empty : text[(split + 1)..].Trim();

        try
        {
            var result = RunDebugCommand(verb, argument);
            DebugLog.Write($"cmd {text} -> {result}");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"cmd {text} -> error {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string RunDebugCommand(string verb, string argument)
    {
        switch (verb)
        {
            case "scroll":
                return SetDebugScroll(ParseDouble(argument));

            case "scroll-end":
            {
                var scroller = DebugScroller();
                if (scroller is null)
                {
                    return "no scroller";
                }
                return SetDebugScroll(Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
            }

            case "scroll-page":
            {
                var scroller = DebugScroller();
                if (scroller is null)
                {
                    return "no scroller";
                }
                var pages = argument.Length == 0 ? 1 : ParseDouble(argument);
                return SetDebugScroll(scroller.Offset.Y + (pages * scroller.Viewport.Height));
            }

            case "tab":
            {
                var index = (int)ParseDouble(argument);
                if (index < 0 || index >= _tabs.Count)
                {
                    return $"tab {index} out of range ({_tabs.Count} tabs)";
                }
                ActivateTab(_tabs[index]);
                return $"active '{_tabs[index].DisplayName}'";
            }

            case "close-tab":
            {
                if (_activeTab is null)
                {
                    return "no active tab";
                }
                var name = _activeTab.DisplayName;
                CloseTab(_activeTab);
                return $"closed '{name}' ({_tabs.Count} tabs)";
            }

            case "find":
            {
                ShowFindBar();
                /*  Set the term without waking the keystroke debounce, then run
                    the search directly: a script wants it finished by the time
                    the acknowledgement lands. */
                _suppressFindEvents = true;
                _findBox.Text = argument;
                _suppressFindEvents = false;
                _findDebounce.Stop();
                RunSearch(scrollToCurrent: true);
                return $"{_activeTab?.Search.Count ?? 0} matches";
            }

            case "find-next":
            case "find-prev":
            {
                FindMove(backward: verb == "find-prev");
                var search = _activeTab?.Search;
                return search is null ? "no active tab" : $"{search.CurrentOrdinal + 1} of {search.Count}";
            }

            case "find-close":
                CloseFindBar();
                return "closed";

            case "toc":
            {
                var index = (int)ParseDouble(argument);
                var entries = _activeTab?.TocEntries.Count ?? 0;
                if (index < 0 || index >= entries)
                {
                    return $"toc {index} out of range ({entries} headings)";
                }
                ScrollToHeading(index);
                return $"'{_activeTab!.TocEntries[index].Text}' offset {DebugScrollY():F1}";
            }

            case "anchor":
                ScrollToAnchor(argument);
                return $"offset {DebugScrollY():F1}";

            case "select-all":
            {
                var viewer = DebugViewer();
                if (viewer is null)
                {
                    return "no viewer";
                }
                viewer.SelectAll();
                return "selected";
            }

            case "copy":
            {
                var viewer = DebugViewer();
                if (viewer is null)
                {
                    return "no viewer";
                }
                _ = viewer.CopyToClipboardAsync();
                return "copied";
            }

            case "html-comments":
            {
                var visible = !string.Equals(argument, "off", StringComparison.OrdinalIgnoreCase);
                SetHtmlCommentsVisible(visible);
                return visible ? "shown" : "hidden";
            }

            case "gc":
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                var process = Process.GetCurrentProcess();
                return $"working set {process.WorkingSet64 / 1024 / 1024} MB";
            }

            case "dump-state":
                DebugLog.Write($"state {BuildStateJson()}");
                return "written";

            default:
                return $"unknown command '{verb}'";
        }
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    /*  The active tab's viewer, or the welcome viewer when no document is
        open — commands work in the welcome state too, so a script can assert
        on "close every tab" without special-casing. */
    private MarkView.Avalonia.MarkdownViewer? DebugViewer()
    {
        return _activeTab?.Viewer ?? _welcomeViewer;
    }

    private ScrollViewer? DebugScroller()
    {
        if (_activeTab is { } tab)
        {
            return tab.Scroller;
        }
        if (_welcomeViewer is { } welcome)
        {
            foreach (var descendant in welcome.GetVisualDescendants())
            {
                if (descendant is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }
            }
        }
        return null;
    }

    private double DebugScrollY()
    {
        return DebugScroller()?.Offset.Y ?? 0;
    }

    private string SetDebugScroll(double y)
    {
        var scroller = DebugScroller();
        if (scroller is null)
        {
            return "no scroller";
        }
        var clamped = Math.Clamp(y, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
        scroller.Offset = scroller.Offset.WithY(clamped);
        MarkActivity();
        return $"offset {scroller.Offset.Y:F1} of {scroller.Extent.Height:F1}";
    }

    /*  Hand-written rather than serialized: one line, no reflection, nothing
        for the trimmer to guess at, and the shape is asserted on by scripts
        rather than consumed as a public contract. */
    private string BuildStateJson()
    {
        var process = Process.GetCurrentProcess();
        var builder = new StringBuilder(512);
        builder.Append("{\"tabs\":[");
        for (var i = 0; i < _tabs.Count; ++i)
        {
            var tab = _tabs[i];
            var scroller = tab.Scroller;
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append("{\"index\":").Append(i)
                .Append(",\"path\":").Append(JsonString(tab.FilePath ?? string.Empty))
                .Append(",\"name\":").Append(JsonString(tab.DisplayName))
                .Append(",\"active\":").Append(tab == _activeTab ? "true" : "false")
                .Append(",\"chars\":").Append(tab.CurrentText?.Length ?? 0)
                .Append(",\"scrollY\":").Append(Number(scroller?.Offset.Y ?? 0))
                .Append(",\"extent\":").Append(Number(scroller?.Extent.Height ?? 0))
                .Append(",\"viewport\":").Append(Number(scroller?.Viewport.Height ?? 0))
                .Append(",\"stale\":").Append(tab.StaleMessage is null ? "false" : "true")
                .Append('}');
        }
        builder.Append(']');

        /*  Virtualization counters: what a check needs to tell "only the
            viewport is realized" from "the whole document happens to fit".
            Zeroed on the classic renderer, which realizes everything. */
        if (_activeTab?.Viewer is Rendering.Virtual.VirtualMarkdownView virtualView)
        {
            var panel = virtualView.Panel;
            var (firstRealized, lastRealized) = panel.RealizedRange;
            /*  The scroll anchor as the app itself saves it: which block is at
                the viewport top, and how far into that block the reader is.
                A check that a tab switch or a reload put the reader back has
                to compare THAT, not the pixel offset — offsets above the
                viewport are estimates and legitimately differ between two
                renders of the same document. */
            var anchorWithin = panel.FirstVisibleBlock >= 0
                ? (_activeTab.Scroller?.Offset.Y ?? 0) - panel.BlockOffset(panel.FirstVisibleBlock)
                : 0;
            builder.Append(",\"virtual\":true")
                .Append(",\"blocks\":").Append(panel.BlockCount)
                .Append(",\"realizedBlocks\":").Append(panel.RealizedBlockCount)
                .Append(",\"measuredBlocks\":").Append(panel.MeasuredBlockCount)
                .Append(",\"firstRealized\":").Append(firstRealized)
                .Append(",\"lastRealized\":").Append(lastRealized)
                .Append(",\"firstVisibleBlock\":").Append(panel.FirstVisibleBlock)
                .Append(",\"anchorWithin\":").Append(Number(anchorWithin))
                /*  Where the last jump aimed and where that block actually sits
                    now: their difference against scrollY is how far off a
                    heading jump landed once the correction pass has run. */
                .Append(",\"targetBlock\":").Append(panel.LastScrollTargetBlock)
                .Append(",\"targetBlockOffset\":").Append(Number(
                    panel.LastScrollTargetBlock >= 0 ? panel.BlockOffset(panel.LastScrollTargetBlock) : 0));
        }
        else
        {
            builder.Append(",\"virtual\":false,\"blocks\":0,\"realizedBlocks\":0")
                .Append(",\"measuredBlocks\":0,\"firstRealized\":-1,\"lastRealized\":-1")
                .Append(",\"firstVisibleBlock\":-1,\"anchorWithin\":0")
                .Append(",\"targetBlock\":-1,\"targetBlockOffset\":0");
        }

        builder.Append(",\"activeTab\":").Append(_activeTab is null ? -1 : _tabs.IndexOf(_activeTab))
            .Append(",\"tocCount\":").Append(_activeTab?.TocEntries.Count ?? 0)
            .Append(",\"tocIndex\":").Append(_currentTocIndex)
            .Append(",\"findVisible\":").Append(_findVisible ? "true" : "false")
            .Append(",\"matches\":").Append(_activeTab?.Search.Count ?? 0)
            .Append(",\"matchIndex\":").Append(_activeTab?.Search.CurrentOrdinal ?? -1)
            .Append(",\"workingSetMb\":").Append(Number(process.WorkingSet64 / 1024.0 / 1024.0))
            .Append(",\"privateMb\":").Append(Number(process.PrivateMemorySize64 / 1024.0 / 1024.0))
            .Append(",\"managedMb\":").Append(Number(GC.GetTotalMemory(false) / 1024.0 / 1024.0))
            .Append('}');
        return builder.ToString();
    }

    private static string Number(double value)
    {
        return value.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string JsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }
}
