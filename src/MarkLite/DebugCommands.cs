using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Controls;

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
                if (DebugViewer() is null)
                {
                    return "no viewer";
                }
                SelectAllInDocument();
                return ActiveSelection is { } all ? all.Describe() : "selected";
            }

            case "select":
            {
                /*  "select <block> <offset> <block> <offset>" — an endpoint pair
                    in the document's own coordinates, which is what makes a
                    selection assertable from outside: no pixels, and it works on
                    blocks that have never been rendered. */
                if (ActiveSelection is not { } selection)
                {
                    return "no document";
                }
                var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4)
                {
                    return "usage: select <block> <offset> <block> <offset>";
                }
                selection.Set(
                    new Rendering.Virtual.SelectionPoint((int)ParseDouble(parts[0]), (int)ParseDouble(parts[1])),
                    new Rendering.Virtual.SelectionPoint((int)ParseDouble(parts[2]), (int)ParseDouble(parts[3])));
                DebugLog.Write($"selection {selection.Describe()}");
                return selection.Describe();
            }

            case "select-none":
            {
                ActiveSelection?.Clear();
                return "cleared";
            }

            case "copy":
            {
                if (DebugViewer() is null)
                {
                    return "no viewer";
                }
                CopyDocumentSelection();
                return ActiveSelection is { } current
                    ? $"{current.CopyText().Length} chars"
                    : "copied";
            }

            case "point-text":
            case "point-link":
            {
                /*  "point-text <block> <offset>" / "point-link <block> [n]" —
                    where that character or link is drawn, in SCREEN pixels.

                    For the one kind of check the command channel cannot stand in
                    for: press, drag, release and hover have to be exercised
                    through the pointer plumbing itself, and a script outside the
                    process cannot know where a character lands — that is the
                    outcome of wrapping, theme metrics and the panel's layout.
                    Reporting the point lets such a check aim exactly, instead of
                    guessing pixels and quietly testing the margin. */
                if (_activeTab is not { } pointTab)
                {
                    return "no document";
                }
                var pointView = pointTab.Viewer;
                var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    return $"usage: {verb} <block> <offset-or-ordinal>";
                }
                var target = (int)ParseDouble(parts[0]);
                var second = parts.Length > 1 ? (int)ParseDouble(parts[1]) : 0;
                var point = verb == "point-link"
                    ? pointView.ScreenPointOfLink(target, second)
                    : pointView.ScreenPointOfText(target, second);
                return point is { } at ? $"{at.X},{at.Y}" : $"not drawn (block {target})";
            }

            case "click-link":
            {
                /*  "click-link <block> [n]" — follows a link the way a click on
                    it would, with the point taken from the link's own layout.
                    No input is injected: the command runs the same hit test and
                    the same navigation the pointer handler runs. */
                if (_activeTab is not { } clickTab)
                {
                    return "no document";
                }
                var view = clickTab.Viewer;
                var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    return "usage: click-link <block> [ordinal]";
                }
                var block = (int)ParseDouble(parts[0]);
                var ordinal = parts.Length > 1 ? (int)ParseDouble(parts[1]) : 0;
                var url = view.ClickLinkInBlock(block, ordinal);
                return url is null ? $"no link {ordinal} in block {block}" : url;
            }

            case "html-comments":
            {
                var visible = !string.Equals(argument, "off", StringComparison.OrdinalIgnoreCase);
                SetHtmlCommentsVisible(visible);
                return visible ? "shown" : "hidden";
            }

            case "gutter":
            {
                var showLines = !string.Equals(argument, "off", StringComparison.OrdinalIgnoreCase);
                SetLineNumbersVisible(showLines);
                return showLines ? "shown" : "hidden";
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

            case "dump-text":
                return DumpModelText();

            default:
                return $"unknown command '{verb}'";
        }
    }

    /*  Writes the model's plain-text projection of every block to a fixed file
        in the temp directory. What search actually matches against, dumped so a
        script can count occurrences in it with a plain text tool and compare
        that against what the find bar reports — the two must agree exactly, and
        counting the SOURCE instead only ever approximates it.

        Blocks are separated by a blank line, so nothing a search could match
        straddles two of them and the file's own match count is the
        document's. */
    private string DumpModelText()
    {
        if (_activeTab?.Viewer.Model is not { } model)
        {
            return "no model (nothing open)";
        }

        var path = Path.Combine(Path.GetTempPath(), "marklite-blocktext.txt");
        var builder = new StringBuilder(model.Text.Length);
        for (var index = 0; index < model.Blocks.Count; index++)
        {
            builder.Append(model.BlockText(index, Rendering.HtmlComments.Visible)).Append("\n\n");
        }
        File.WriteAllText(path, builder.ToString());
        return $"{model.Blocks.Count} blocks, {builder.Length} chars";
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
    private Rendering.Virtual.VirtualMarkdownView? DebugViewer()
    {
        return _activeTab?.Viewer ?? _welcomeViewer;
    }

    private ScrollViewer? DebugScroller()
    {
        return DebugViewer()?.Scroller;
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
            Zeroed when no document is open, which has no panel to count. */
        if (_activeTab is { } virtualTab)
        {
            var virtualView = virtualTab.Viewer;
            var panel = virtualView.Panel;
            var (firstRealized, lastRealized) = panel.RealizedRange;
            /*  The scroll anchor as the app itself saves it: which block is at
                the viewport top, and how far into that block the reader is.
                A check that a tab switch or a reload put the reader back has
                to compare THAT, not the pixel offset — offsets above the
                viewport are estimates and legitimately differ between two
                renders of the same document. */
            var anchorWithin = panel.FirstVisibleBlock >= 0
                ? (virtualTab.Scroller?.Offset.Y ?? 0) - panel.BlockOffset(panel.FirstVisibleBlock)
                : 0;
            var (firstLine, lastLine) = panel.VisibleSourceLines;
            builder.Append(",\"blocks\":").Append(panel.BlockCount)
                .Append(",\"realizedBlocks\":").Append(panel.RealizedBlockCount)
                .Append(",\"measuredBlocks\":").Append(panel.MeasuredBlockCount)
                .Append(",\"firstRealized\":").Append(firstRealized)
                .Append(",\"lastRealized\":").Append(lastRealized)
                .Append(",\"firstVisibleBlock\":").Append(panel.FirstVisibleBlock)
                .Append(",\"anchorWithin\":").Append(Number(anchorWithin))
                /*  What the gutter is showing: a check can compare these
                    against a grep of the fixture without reading pixels. */
                .Append(",\"firstVisibleLine\":").Append(firstLine)
                .Append(",\"lastVisibleLine\":").Append(lastLine)
                /*  Where the last jump aimed and where that block actually sits
                    now: their difference against scrollY is how far off a
                    heading jump landed once the correction pass has run. */
                .Append(",\"targetBlock\":").Append(panel.LastScrollTargetBlock)
                .Append(",\"targetBlockOffset\":").Append(Number(
                    panel.LastScrollTargetBlock >= 0 ? panel.BlockOffset(panel.LastScrollTargetBlock) : 0))
                /*  The source line the gutter draws for that block, so a check
                    can confirm a heading jump landed on a heading LINE of the
                    file without reading pixels. */
                .Append(",\"targetBlockLine\":").Append(
                    panel.LastScrollTargetBlock >= 0 && virtualView.Model is { } targetModel
                    && panel.LastScrollTargetBlock < targetModel.Blocks.Count
                        ? targetModel.Blocks[panel.LastScrollTargetBlock].StartLine
                        : 0);
        }
        else
        {
            builder.Append(",\"blocks\":0,\"realizedBlocks\":0")
                .Append(",\"measuredBlocks\":0,\"firstRealized\":-1,\"lastRealized\":-1")
                .Append(",\"firstVisibleBlock\":-1,\"anchorWithin\":0")
                .Append(",\"firstVisibleLine\":0,\"lastVisibleLine\":0")
                .Append(",\"targetBlock\":-1,\"targetBlockOffset\":0,\"targetBlockLine\":0");
        }

        /*  The version as the HELP MENU shows it, not as AppVersion computes
            it: a check reading this is asserting on the text the user sees,
            and submenu items are not in the UI Automation tree until the menu
            is opened. */
        builder.Append(",\"version\":")
            .Append(JsonString(this.FindControl<MenuItem>("VersionItem")?.Header as string ?? string.Empty))
            .Append(",\"activeTab\":").Append(_activeTab is null ? -1 : _tabs.IndexOf(_activeTab))
            .Append(",\"tocCount\":").Append(_activeTab?.TocEntries.Count ?? 0)
            .Append(",\"tocIndex\":").Append(_currentTocIndex)
            .Append(",\"gutterVisible\":").Append(Rendering.Virtual.GutterPanel.Enabled ? "true" : "false")
            .Append(",\"findVisible\":").Append(_findVisible ? "true" : "false")
            .Append(",\"matches\":").Append(_activeTab?.Search.Count ?? 0)
            .Append(",\"matchIndex\":").Append(_activeTab?.Search.CurrentOrdinal ?? -1)
            /*  Matches that actually carry a highlight right now — the
                realized subset, so a check can tell "the match was found in the
                model" from "the match is on screen with a mark on it". */
            .Append(",\"highlighted\":").Append(_activeTab?.Search.HighlightedCount ?? 0)
            /*  "12:5-14:80 (203 chars)", or empty when nothing is selected. The
                endpoints are block:offset, so a check can assert on the exact
                range it asked for without touching a pixel, and the character
                count is the length of the markdown a copy would produce. */
            .Append(",\"selection\":").Append(JsonString(ActiveSelection?.Describe() ?? string.Empty))
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
