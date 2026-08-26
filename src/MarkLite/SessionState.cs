using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MarkLite;

/*  Options > Reopen last session: the documents that were open when MarkLite
    last went away come back the next time it launches, each on the paragraph
    its reader was on.

    Written on every change rather than only on close. An update applies by
    handing the process to Velopack, which terminates it — Window.Closed never
    runs — and the update restart is the case this feature exists for. The
    close handler still saves, because it is the only moment the ACTIVE tab's
    live scroll position can be read.

    Storage is UserSettings (HKCU), scoped per single-instance group, so a
    scripted run and the user's own copy never trade documents. */
public partial class MainWindow
{
    /*  Set while the constructor is reopening the stored tabs: every OpenFile
        in that loop would otherwise save a half-restored session over the one
        being read. */
    private bool _restoringSession;

    /*  Where a restored session wants the reader parked, held until the window
        is up: see ApplySessionScroll. Kept as a value rather than read back
        from the tab, because the scroll events the first layout raises would
        otherwise capture the top of the document over it. */
    private DocumentTab? _sessionScrollPending;
    private ScrollRestore _sessionScrollAnchor;

    /*  Debounce behind the scroll hook; created on first use because most
        launches never scroll. */
    private DispatcherTimer? _sessionSaveTimer;

    /*  One stored tab: the file, plus the scroll anchor in the same terms
        ScrollRestore uses. The hash is what survives the file being edited
        between sessions — RestoreScroll prefers it over the index. Y is not
        stored: a pixel offset from a previous window size means nothing, and
        a tab with no anchor simply opens at the top.

        Fields are separated by '|', which is not legal in a Windows path. */
    private readonly record struct SessionEntry(string Path, int BlockIndex, ulong BlockHash, double OffsetWithin)
    {
        public string Format()
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{Path}|{BlockIndex}|{BlockHash}|{OffsetWithin:F1}");
        }

        public static SessionEntry? Parse(string stored)
        {
            var parts = stored.Split('|');
            if (parts.Length != 4 || parts[0].Length == 0)
            {
                return null;
            }
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockIndex)
                || !ulong.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var within))
            {
                return null;
            }
            return new SessionEntry(parts[0], blockIndex, hash, within);
        }

        public ScrollRestore ToScrollRestore()
        {
            return new ScrollRestore(0, BlockHash, BlockIndex, OffsetWithin);
        }
    }

    /// <summary>Whether the setting is on; unset means on, which is the default.</summary>
    private static bool SessionRestoreEnabled => UserSettings.RestoreSession ?? true;

    /// <summary>How many tabs are stored right now — what a check asserts on instead of reading
    /// the registry itself.</summary>
    private static int StoredSessionCount => UserSettings.Session?.Length ?? 0;

    /*  Called from every place the tab list or the reading position changes.
        Tabs whose file could not be opened carry no path and are left out:
        restoring a "Cannot open file" page helps nobody. */
    private void SaveSession()
    {
        if (_restoringSession || !SessionRestoreEnabled)
        {
            return;
        }

        var entries = new List<string>(_tabs.Count);
        var activeIndex = 0;
        foreach (var tab in _tabs)
        {
            if (tab.FilePath is not { Length: > 0 } path)
            {
                continue;
            }
            if (tab == _activeTab)
            {
                activeIndex = entries.Count;
            }
            /*  SavedScroll, never a fresh capture: the active tab's is kept
                current by the scroll hook (see MarkSessionDirty), and a
                capture taken from the Closed handler reads a viewer that is
                already coming apart — it answers block 0 often enough to
                overwrite a perfectly good anchor with the top of the file. */
            var scroll = tab.SavedScroll;
            entries.Add(new SessionEntry(path, scroll.BlockIndex, scroll.BlockHash, scroll.OffsetWithin).Format());
        }

        UserSettings.Session = entries.Count > 0 ? [.. entries] : null;
        UserSettings.SessionActiveIndex = entries.Count > 0 ? activeIndex : null;
        DebugLog.Write($"session saved: {entries.Count} tabs, active {activeIndex}"
            + (entries.Count > 0 ? $", entry '{entries[Math.Min(activeIndex, entries.Count - 1)]}'" : string.Empty));
    }

    /*  The reader scrolling is a session change like any other, but a scroll
        raises events by the hundred — so the position is captured on every one
        of them (cheap) and the registry write is debounced behind them. */
    private void MarkSessionDirty()
    {
        /*  While a restore is still waiting to be applied, the position on
            screen is the top of a freshly laid-out document, not where the
            reader was. Capturing it would throw the anchor away. */
        if (_restoringSession || _sessionScrollPending is not null || _activeTab is not { } tab)
        {
            return;
        }

        tab.SavedScroll = tab.CaptureScroll();
        if (!SessionRestoreEnabled)
        {
            return;
        }

        if (_sessionSaveTimer is null)
        {
            _sessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _sessionSaveTimer.Tick += (_, _) =>
            {
                _sessionSaveTimer!.Stop();
                SaveSession();
            };
        }
        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Start();
    }

    /// <summary>Reopens the stored tabs. False means nothing was restored and the caller should
    /// fall through to the welcome page.</summary>
    private bool RestoreSession()
    {
        if (!SessionRestoreEnabled)
        {
            return false;
        }
        if (UserSettings.Session is not { Length: > 0 } stored)
        {
            return false;
        }

        var wanted = UserSettings.SessionActiveIndex ?? 0;
        DocumentTab? active = null;
        var anchors = new List<(DocumentTab Tab, ScrollRestore Anchor)>(stored.Length);

        _restoringSession = true;
        try
        {
            for (var i = 0; i < stored.Length; i++)
            {
                if (SessionEntry.Parse(stored[i]) is not { } entry)
                {
                    DebugLog.Write($"session: unreadable entry '{stored[i]}'");
                    continue;
                }
                /*  A file deleted, renamed or unplugged since last time is
                    dropped without a word on screen: a window full of "Cannot
                    open file" tabs would be worse than no restore at all. */
                if (!File.Exists(entry.Path))
                {
                    DebugLog.Write($"session: skipped missing {entry.Path}");
                    continue;
                }

                OpenFile(entry.Path);
                var tab = _tabs.LastOrDefault(
                    t => string.Equals(t.FilePath, entry.Path, StringComparison.OrdinalIgnoreCase));
                if (tab is null)
                {
                    continue;
                }
                anchors.Add((tab, entry.ToScrollRestore()));
                if (i == wanted)
                {
                    active = tab;
                }
            }
        }
        finally
        {
            _restoringSession = false;
        }

        /*  Anchors go on AFTER every tab is open: each OpenFile activates the
            tab it created, and activation captures the OUTGOING tab's position
            over whatever was there — which, inside this loop, would be the
            anchor just restored onto it. */
        foreach (var (tab, anchor) in anchors)
        {
            tab.SavedScroll = anchor;
        }

        if (_tabs.Count == 0)
        {
            DebugLog.Write($"session: {stored.Length} entries, none could be opened");
            return false;
        }

        /*  Every tab was activated as it opened, so the last one is showing;
            switch if the session wanted a different one. The reading position
            is NOT applied here — see ApplySessionScroll. */
        active ??= _tabs[0];
        if (active != _activeTab)
        {
            ActivateTab(active);
        }
        _sessionScrollPending = active;
        _sessionScrollAnchor = active.SavedScroll;

        /*  Skipped entries must not linger in the store: the user closed
            nothing, but those documents are gone. */
        SaveSession();
        DebugLog.Write($"session restored: {_tabs.Count} of {stored.Length} tabs, active {_tabs.IndexOf(active)}");
        return true;
    }

    /*  Puts the reader back where they were, once the window exists.

        Not done while the session is being restored: that runs from the
        constructor, before the window is shown, and an offset set against a
        viewport that has not been laid out is clamped to zero and stays there.

        Nor with a posted callback. The virtualizing panel measures its way
        down to the anchor block over several layout passes, so the first
        attempt lands short — and anything queued at Background priority can be
        overtaken indefinitely by the work the panel and the debug command pipe
        keep putting in front of it. A plain timer at Normal priority retries
        until the block the anchor names is actually the one at the top of the
        viewport, which is the only condition worth waiting for. */
    private void ApplySessionScroll()
    {
        if (_sessionScrollPending is not { } tab)
        {
            return;
        }

        var anchor = _sessionScrollAnchor;
        if (tab != _activeTab || !anchor.MovesTheView)
        {
            _sessionScrollPending = null;
            return;
        }

        var attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            attempts++;
            var abandoned = tab != _activeTab;
            var block = abandoned ? -1 : tab.RestoreScroll(anchor);
            var landed = block >= 0 && tab.Viewer.Panel.FirstVisibleBlock == block;

            /*  Two seconds is far longer than settling takes (a handful of
                passes); it is there so a document that cannot reach the anchor
                at all stops trying instead of ticking for the session. */
            if (!abandoned && !landed && attempts < 40)
            {
                return;
            }

            timer.Stop();
            _sessionScrollPending = null;
            if (!abandoned)
            {
                tab.SavedScroll = anchor;
                DebugLog.Write(
                    $"session scroll restored {tab.ScrollY:F1} block {block} after {attempts} passes"
                    + (landed ? string.Empty : " (gave up)"));
            }
        };
        timer.Start();
    }

    /*  The Options toggle. Turning it off clears the stored session rather
        than leaving one behind for a later switch-on to resurrect — the
        setting means "do not remember", and quietly remembering anyway would
        be the wrong reading of it. */
    private void SetSessionRestore(bool enabled)
    {
        UserSettings.RestoreSession = enabled;
        this.FindControl<MenuItem>("ReopenSessionItem")!.IsChecked = enabled;
        if (enabled)
        {
            SaveSession();
        }
        else
        {
            UserSettings.Session = null;
            UserSettings.SessionActiveIndex = null;
        }
        DebugLog.Write($"reopen last session: {(enabled ? "on" : "off")} ({StoredSessionCount} tabs stored)");
    }
}
