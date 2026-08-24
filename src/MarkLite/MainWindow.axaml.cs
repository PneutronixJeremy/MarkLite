using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Markdig;
using MarkLite.Rendering;
using MarkView.Avalonia;

namespace MarkLite;

public partial class MainWindow : Window
{
    private const string WelcomeMarkdown = """
        # MarkLite

        No document open.

        - **File → Open…** to pick a Markdown file
        - Drag a file onto this window
        - `MarkLite.exe <file.md>` from a terminal
        """;

    /*  Tabs. Each DocumentTab owns its viewer/watcher/search; the window
        swaps the active tab's viewer into ViewerHost and mirrors its state
        (title, stale banner, TOC, find box). Zero tabs = welcome state. */
    private readonly List<DocumentTab> _tabs = [];
    private DocumentTab? _activeTab;
    private MarkdownViewer? _welcomeViewer;
    private readonly MarkLiteHyperlinkCommand _hyperlinkCommand;

    /*  One pipeline and one renderer-extension instance shared by all tabs —
        both are stateless across render passes. Task lists, tables, autolinks
        and friends come from UseSupportedExtensions (MarkView's default set);
        UseMathematics feeds the Math package's block/inline renderers. */
    private static readonly MarkdownPipeline SharedPipeline =
        new MarkdownPipelineBuilder().UseSupportedExtensions().UseMathematics().Build();
    private static readonly MarkLiteRenderExtension RenderExtension = new();

    private readonly Panel _viewerHost;
    private readonly TextBox _findBox;

    private IStorageFolder? _lastOpenFolder;
    private bool _firstRenderLogged;

    /*  TOC state for the ACTIVE tab. Entry/control data lives on each tab;
        the button list below is UI for whichever tab is showing. _tocVisible
        is the user's preference (Ctrl+T / View menu) and persists across
        reloads and tab switches; the panel additionally hides itself when the
        active document has no headings. */
    private readonly List<Button> _tocButtons = [];
    private bool _tocVisible = true;
    private int _currentTocIndex = -1;

    /*  In-document search. The debounce timer batches keystrokes in the find
        box (a full re-highlight per keypress is wasteful on big documents);
        _findVisible mirrors the find bar and gates F3/Esc handling;
        _suppressFindEvents lets tab switches sync the box text without
        triggering a search. */
    private readonly DispatcherTimer _findDebounce;
    private bool _findVisible;
    private bool _suppressFindEvents;

    /*  Idle memory trim. Scrolling fills glyph/layout caches and an idle
        viewer has no allocation pressure, so garbage accumulates until the
        user acts again. After 30 s without activity, collect once
        aggressively; re-armed by the next activity. */
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _idleTrimDone;

    public MainWindow() : this([])
    {
    }

    public MainWindow(string[] args)
    {
        InitializeComponent();

        _viewerHost = this.FindControl<Panel>("ViewerHost")!;
        _findBox = this.FindControl<TextBox>("FindBox")!;

        _hyperlinkCommand = new MarkLiteHyperlinkCommand(
            currentDocumentDirectory: () => _activeTab?.FilePath is { } file ? Path.GetDirectoryName(file) : null,
            openDocument: OpenFile,
            scrollToAnchor: ScrollToAnchor);

        var idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        idleTrimTimer.Tick += (_, _) =>
        {
            if (!_idleTrimDone && DateTime.UtcNow - _lastActivityUtc > TimeSpan.FromSeconds(30))
            {
                _idleTrimDone = true;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                DebugLog.Write("idle memory trim");
            }
        };
        idleTrimTimer.Start();

        _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce.Stop();
            RunSearch(scrollToCurrent: true);
        };
        _findBox.TextChanged += (_, _) =>
        {
            if (_suppressFindEvents)
            {
                return;
            }
            MarkActivity();
            _findDebounce.Stop();
            _findDebounce.Start();
        };
        _findBox.KeyDown += OnFindBoxKeyDown;

        Closed += (_, _) =>
        {
            foreach (var tab in _tabs)
            {
                tab.Dispose();
            }
        };

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        /*  Style brushes follow the theme via DynamicResource, but syntax
            highlighting bakes colored Runs at render time — a variant switch
            needs one re-render per tab. Inactive tabs defer theirs. */
        ActualThemeVariantChanged += (_, _) =>
        {
            DebugLog.Write($"theme changed to {ActualThemeVariant}; re-rendering");
            foreach (var tab in _tabs)
            {
                if (tab.CurrentText is null)
                {
                    continue;
                }
                if (tab == _activeTab)
                {
                    RenderTab(tab, tab.CurrentText, tab.ScrollY);
                }
                else
                {
                    tab.PendingText = tab.CurrentText;
                }
            }
        };

        if (args.Length > 0)
        {
            OpenFile(args[0]);
        }
        else
        {
            ShowWelcome();
        }

        /*  Only the primary instance holds the pipe; StartServer no-ops in a
            standalone secondary. Activate() nudges the window forward when a
            handoff arrives (Windows may only flash the taskbar button). */
        SingleInstance.StartServer(path =>
        {
            DebugLog.Write($"handoff received: {path}");
            OpenFile(path);
            Activate();
        });

        Opened += (_, _) =>
        {
            DebugLog.Write($"startup: window opened {Program.StartupTimer.ElapsedMilliseconds} ms after process start");
        };
    }

    private void MarkActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
        _idleTrimDone = false;
    }

    private static MarkdownViewer CreateViewer()
    {
        var viewer = new MarkdownViewer
        {
            Pipeline = SharedPipeline,
            MaxWidth = 1100,
            Margin = new Thickness(28, 6, 28, 0),
            /*  The sidebar shows every heading level, and the entries are
                paired positionally with the rendered heading controls — a
                shallower depth would drop deep headings from the list while
                the controls remain, breaking that pairing. */
            TableOfContentsMaxDepth = 6,
        };
        /*  Without this, wide content (long code lines, wide tables) is
            measured unbounded and overflows instead of wrapping/scrolling
            inside its own block. */
        ScrollViewer.SetHorizontalScrollBarVisibility(viewer,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        return viewer;
    }

    /// <summary>View > Body font. Swaps the app-level font token; DynamicResource restyles live.</summary>
    private void OnBodyFontClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string fontSpec })
        {
            return;
        }
        Application.Current!.Resources["MdBodyFontFamily"] = new FontFamily(fontSpec);
        DebugLog.Write($"body font set: {fontSpec}");
    }

    #region tabs

    /// <summary>Opens a file in a new tab, or focuses the tab that already has it.</summary>
    private void OpenFile(string path)
    {
        string? fullPath = null;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // Unresolvable path: fall through and let LoadIntoTab render the error.
        }

        if (fullPath is not null)
        {
            var existing = _tabs.FirstOrDefault(
                t => string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                DebugLog.Write($"tab focused existing: {fullPath}");
                ActivateTab(existing);
                return;
            }
        }

        var tab = CreateTab();
        _tabs.Add(tab);
        this.FindControl<StackPanel>("TabStrip")!.Children.Add(tab.StripItem);
        ActivateTab(tab);
        LoadIntoTab(tab, fullPath ?? path);
        UpdateTabStripVisibility();
        DebugLog.Write($"tab opened '{tab.DisplayName}' ({_tabs.Count} tabs)");
    }

    private DocumentTab CreateTab()
    {
        var viewer = CreateViewer();

        /*  Added to the host straight away and kept there for the tab's whole
            life; ActivateTab only flips visibility (see the note there). */
        viewer.IsVisible = false;
        _viewerHost.Children.Add(viewer);

        /*  Registration order is load-bearing: UseMermaid front-inserts a
            renderer that broadly claims every fenced block, and MarkLite's
            extension must register after it so its own code renderer lands
            ahead (it forwards mermaid fences back). UseMath re-fronts its
            narrow math renderer regardless. */
        viewer.UseMermaid();
        viewer.Extensions.Add(RenderExtension);
        viewer.UseMath();
        viewer.LinkClicked += (_, e) => _hyperlinkCommand.Execute(e.Url);

        var label = new TextBlock { Text = "Untitled", VerticalAlignment = VerticalAlignment.Center };
        var nameButton = new Button { Content = label };
        nameButton.Classes.Add("TabName");
        var closeButton = new Button { Content = "✕" };
        closeButton.Classes.Add("TabClose");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        panel.Children.Add(nameButton);
        panel.Children.Add(closeButton);
        var item = new Border { Child = panel };
        item.Classes.Add("TabItem");

        var tab = new DocumentTab
        {
            Viewer = viewer,
            Watcher = new DocumentWatcher(),
            Search = new DocumentSearch(viewer),
            StripItem = item,
            StripLabel = label,
        };

        /*  Current-section tracking rides the template ScrollViewer's
            ScrollChanged. The template only exists once the viewer has been
            attached (tab activated), so the hook is installed lazily on first
            attachment. */
        viewer.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            /*  Posted at Loaded priority: the template (and with it
                PART_ScrollViewer) is applied during the layout pass that
                follows attachment, not at attachment itself. */
            if (tab.ScrollHooked || tab.Scroller is not { } scrollViewer)
            {
                return;
            }
            tab.ScrollHooked = true;
            scrollViewer.ScrollChanged += (_, _) =>
            {
                MarkActivity();
                if (_activeTab == tab)
                {
                    UpdateCurrentSection();
                }
            };
        }, DispatcherPriority.Loaded);
        nameButton.Click += (_, _) => ActivateTab(tab);
        closeButton.Click += (_, _) => CloseTab(tab);
        item.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(item).Properties.IsMiddleButtonPressed)
            {
                CloseTab(tab);
                e.Handled = true;
            }
        };
        tab.Watcher.ChangeSettled += () => OnTabFileChanged(tab);

        return tab;
    }

    private void LoadIntoTab(DocumentTab tab, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var text = File.ReadAllText(fullPath);

            tab.FilePath = fullPath;
            tab.CurrentText = text;
            tab.PendingText = null;
            tab.Watcher.Watch(fullPath);
            SetTabStale(tab, null);
            UpdateTabHeader(tab);
            RenderTab(tab, text);
            DebugLog.Write($"opened {fullPath} ({text.Length} chars)");
        }
        catch (Exception ex)
        {
            tab.FilePath = null;
            tab.CurrentText = null;
            tab.PendingText = null;
            tab.Watcher.StopWatching();
            SetTabStale(tab, null);
            tab.Viewer.Markdown = $"# Cannot open file\n\n`{path}`\n\n{ex.Message}";

            var attemptedName = Path.GetFileName(path);
            UpdateTabHeader(tab,
                nameOverride: attemptedName.Length > 0 ? attemptedName : "Untitled",
                tooltip: path);
            DebugLog.Write($"open failed for {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpdateTabHeader(DocumentTab tab, string? nameOverride = null, string? tooltip = null)
    {
        var name = nameOverride ?? tab.DisplayName;
        tab.StripLabel.Text = name;
        ToolTip.SetTip(tab.StripItem, tooltip ?? tab.FilePath ?? name);
        if (tab == _activeTab)
        {
            Title = $"MarkLite — {name}";
        }
    }

    /*  Tab switching hides the outgoing viewer instead of removing it from the
        host. Removing it would detach it from the logical tree, and the Mermaid
        package registers a DetachedFromLogicalTree handler that cancels an
        already-disposed CancellationTokenSource — so the second detach of any
        viewer holding a mermaid diagram throws ObjectDisposedException and
        aborts the switch halfway. Hidden-but-attached also keeps each viewer's
        scroll offset alive, so switching needs no offset restore at all. */
    private void ActivateTab(DocumentTab tab)
    {
        if (_activeTab == tab)
        {
            return;
        }

        if (_activeTab is not null)
        {
            _activeTab.SavedScrollY = _activeTab.ScrollY;
            _activeTab.StripItem.Classes.Remove("TabItemActive");
            _activeTab.Viewer.IsVisible = false;
            DebugLog.Write($"tab scroll saved {_activeTab.SavedScrollY:F1} for '{_activeTab.DisplayName}'");
        }
        if (_welcomeViewer is not null)
        {
            _welcomeViewer.IsVisible = false;
        }

        _activeTab = tab;
        MarkActivity();
        tab.StripItem.Classes.Add("TabItemActive");
        tab.Viewer.IsVisible = true;
        Title = $"MarkLite — {tab.DisplayName}";
        SetStaleBanner(tab.StaleMessage);

        _suppressFindEvents = true;
        _findBox.Text = tab.SearchTerm;
        _suppressFindEvents = false;

        DebugLog.Write($"tab switched to '{tab.DisplayName}'; scroll {tab.SavedScrollY:F1}");

        if (tab.PendingText is not null)
        {
            /*  Content changed (reload/theme) while this tab was inactive; a
                detached viewer cannot lay out, so the render was deferred to
                now. The render pipeline also refreshes TOC and search. */
            tab.CurrentText = tab.PendingText;
            RenderTab(tab, tab.PendingText, tab.SavedScrollY);
        }
        else
        {
            RefreshTocPanel();
            if (_findVisible && tab.SearchTerm.Length > 0)
            {
                RunSearch(scrollToCurrent: false);
            }
            else
            {
                UpdateFindCount();
            }
            RestoreActiveTabScroll();
        }
    }

    /*  Viewers stay attached across switches, so the offset normally survives
        on its own; this pushes the saved value back anyway, because a viewer
        that was hidden skipped layout and can report a stale extent on the
        first pass. Two passes for that reason: the Loaded-priority set may be
        clamped, the Background one runs after layout has settled. Both are
        no-ops when the offset never moved. */
    private void RestoreActiveTabScroll()
    {
        var tab = _activeTab;
        if (tab is null)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_activeTab != tab)
            {
                return;
            }
            tab.ScrollY = tab.SavedScrollY;
            Dispatcher.UIThread.Post(() =>
            {
                if (_activeTab != tab)
                {
                    return;
                }
                tab.ScrollY = tab.SavedScrollY;
                UpdateCurrentSection();
                DebugLog.Write($"tab scroll restored {tab.ScrollY:F1}");
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void CloseTab(DocumentTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _tabs.RemoveAt(index);
        this.FindControl<StackPanel>("TabStrip")!.Children.Remove(tab.StripItem);
        _viewerHost.Children.Remove(tab.Viewer);
        tab.Dispose();
        DebugLog.Write($"tab closed '{tab.DisplayName}' ({_tabs.Count} tabs)");

        if (_activeTab == tab)
        {
            _activeTab = null;
            if (_tabs.Count > 0)
            {
                ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
            }
            else
            {
                ShowWelcome();
            }
        }
        UpdateTabStripVisibility();
    }

    private void ShowWelcome()
    {
        _activeTab = null;
        if (_welcomeViewer is null)
        {
            _welcomeViewer = CreateViewer();
            _welcomeViewer.Markdown = WelcomeMarkdown;
            _viewerHost.Children.Add(_welcomeViewer);
        }
        _welcomeViewer.IsVisible = true;
        Title = "MarkLite";
        SetStaleBanner(null);
        RefreshTocPanel();
        UpdateFindCount();
        DebugLog.Write("welcome state (no tabs)");
    }

    private void UpdateTabStripVisibility()
    {
        /*  Visible from the first tab: the ✕ button is the only mouse-only
            way to close a lone tab (menus aside), and hiding the strip for
            single documents would take that away. */
        this.FindControl<Border>("TabStripBorder")!.IsVisible = _tabs.Count >= 1;
    }

    private void SetTabStale(DocumentTab tab, string? message)
    {
        tab.StaleMessage = message;
        if (message is not null)
        {
            if (!tab.StripItem.Classes.Contains("TabItemStale"))
            {
                tab.StripItem.Classes.Add("TabItemStale");
            }
        }
        else
        {
            tab.StripItem.Classes.Remove("TabItemStale");
        }
        if (tab == _activeTab)
        {
            SetStaleBanner(message);
        }
    }

    private void SetStaleBanner(string? message)
    {
        var banner = this.FindControl<Border>("StaleBanner")!;
        banner.IsVisible = message is not null;
        if (message is not null)
        {
            this.FindControl<TextBlock>("StaleText")!.Text = message;
        }
    }

    private void OnTabFileChanged(DocumentTab tab)
    {
        if (tab.FilePath is null)
        {
            return;
        }

        if (!File.Exists(tab.FilePath))
        {
            SetTabStale(tab, "File is missing — showing the last loaded content. It will reload when the file reappears.");
            DebugLog.Write($"file missing: {tab.FilePath}");
            return;
        }

        try
        {
            var text = File.ReadAllText(tab.FilePath);
            tab.CurrentText = text;

            if (tab == _activeTab)
            {
                var savedScroll = tab.ScrollY;
                DebugLog.Write($"reload triggered: {tab.FilePath}; scroll saved {savedScroll:F1}");
                RenderTab(tab, text, savedScroll,
                    () => DebugLog.Write($"scroll restored {tab.ScrollY:F1}"));
            }
            else
            {
                tab.PendingText = text;
                DebugLog.Write($"reload deferred (inactive tab): {tab.FilePath}");
            }
            SetTabStale(tab, null);
        }
        catch (IOException ex)
        {
            /*  Locked mid-write (or still being copied): keep what is on
                screen; the next file event retries. */
            SetTabStale(tab, "File is locked — showing the last loaded content.");
            DebugLog.Write($"reload failed (locked): {ex.Message}");
        }
    }

    #endregion

    /*  Single render path for a tab: assign markdown, then after the layout
        pass that realizes the new controls, restore the scroll offset (the
        viewer resets it to 0 on every content set), rebuild TOC data and
        re-apply search (only possible once controls are in the visual tree).
        Inactive tabs cannot lay out, so their render is parked in PendingText
        until activation. */
    private void RenderTab(DocumentTab tab, string text, double restoreScrollY = 0, Action? afterLayout = null)
    {
        if (tab != _activeTab)
        {
            tab.PendingText = text;
            return;
        }

        MarkActivity();
        tab.Search.Detach();
        tab.PendingText = null;
        tab.Viewer.Markdown = text;
        Dispatcher.UIThread.Post(() =>
        {
            /*  Two-pass restore, same reason as RestoreActiveTabScroll: at
                Loaded priority the fresh tree may report a smaller extent and
                the first set gets clamped. */
            if (restoreScrollY > 0)
            {
                tab.ScrollY = restoreScrollY;
                Dispatcher.UIThread.Post(() =>
                {
                    if (tab == _activeTab)
                    {
                        tab.ScrollY = restoreScrollY;
                    }
                }, DispatcherPriority.Background);
            }
            RebuildTocData(tab);
            if (tab == _activeTab)
            {
                RefreshTocPanel();
                UpdateCurrentSection();
                if (_findVisible && tab.SearchTerm.Length > 0)
                {
                    RunSearch(scrollToCurrent: false);
                }
            }
            if (!_firstRenderLogged)
            {
                _firstRenderLogged = true;
                DebugLog.Write($"startup: first content render {Program.StartupTimer.ElapsedMilliseconds} ms after process start");
            }
            afterLayout?.Invoke();

            /*  Parsing + control-tree construction produce a large one-shot
                garbage spike; the app idles right after a render, so collect
                now and hand the pages back to the OS instead of holding a
                bloated working set. */
            Dispatcher.UIThread.Post(static () =>
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open Markdown file",
            AllowMultiple = false,
            SuggestedStartLocation = _lastOpenFolder,
            FileTypeFilter =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown", "*.txt"] },
                FilePickerFileTypes.All,
            ],
        };

        var files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].Path.LocalPath;
        _lastOpenFolder = await files[0].GetParentAsync() as IStorageFolder;
        OpenFile(path);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().ToList();
        if (files is null)
        {
            return;
        }
        foreach (var file in files)
        {
            DebugLog.Write($"file dropped: {file.Path.LocalPath}");
            OpenFile(file.Path.LocalPath);
        }
    }

    private void OnCloseTabClicked(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is not null)
        {
            CloseTab(_activeTab);
        }
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control)
        {
            ToggleToc();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            ShowFindBar();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control)
        {
            if (_activeTab is not null)
            {
                CloseTab(_activeTab);
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _tabs.Count > 1 && _activeTab is not null)
        {
            var index = _tabs.IndexOf(_activeTab);
            var next = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                ? (index - 1 + _tabs.Count) % _tabs.Count
                : (index + 1) % _tabs.Count;
            ActivateTab(_tabs[next]);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F3)
        {
            FindMove(backward: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _findVisible)
        {
            CloseFindBar();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    #region in-document search

    private void OnFindClicked(object? sender, RoutedEventArgs e)
    {
        ShowFindBar();
    }

    private void OnFindNextClicked(object? sender, RoutedEventArgs e)
    {
        FindMove(backward: false);
    }

    private void OnFindPrevClicked(object? sender, RoutedEventArgs e)
    {
        FindMove(backward: true);
    }

    private void OnFindCloseClicked(object? sender, RoutedEventArgs e)
    {
        CloseFindBar();
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindMove(backward: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void ShowFindBar()
    {
        _findVisible = true;
        this.FindControl<Border>("FindBar")!.IsVisible = true;
        _findBox.Focus();
        _findBox.SelectAll();
        if (!string.IsNullOrEmpty(_findBox.Text))
        {
            RunSearch(scrollToCurrent: false);
        }
    }

    private void CloseFindBar()
    {
        _findVisible = false;
        _findDebounce.Stop();
        this.FindControl<Border>("FindBar")!.IsVisible = false;
        foreach (var tab in _tabs)
        {
            tab.Search.Clear();
            tab.SearchTerm = string.Empty;
        }
        UpdateFindCount();
        _activeTab?.Viewer.Focus();
        DebugLog.Write("search closed");
    }

    private void RunSearch(bool scrollToCurrent)
    {
        if (_activeTab is null)
        {
            UpdateFindCount();
            return;
        }

        MarkActivity();
        var term = _findBox.Text ?? string.Empty;
        _activeTab.SearchTerm = term;
        _activeTab.Search.Apply(term,
            FindBrush("MdSearchMatchBackground"),
            FindBrush("MdSearchCurrentBackground"),
            FindBrush("MdSearchCurrentForeground"),
            scrollToCurrent);
        UpdateFindCount();
        if (term.Length > 0)
        {
            DebugLog.Write($"search '{term}': {_activeTab.Search.Count} matches");
        }
    }

    private void FindMove(bool backward)
    {
        if (!_findVisible || _activeTab is null)
        {
            return;
        }
        MarkActivity();

        /*  A pending debounce means the shown highlights don't reflect the
            typed term yet — flush it instead of stepping stale matches; the
            fresh Apply already lands on its first match. */
        if (_findDebounce.IsEnabled)
        {
            _findDebounce.Stop();
            RunSearch(scrollToCurrent: true);
            return;
        }

        if (_activeTab.Search.Count == 0)
        {
            return;
        }
        if (backward)
        {
            _activeTab.Search.MovePrevious();
        }
        else
        {
            _activeTab.Search.MoveNext();
        }
        UpdateFindCount();
        DebugLog.Write($"search current {_activeTab.Search.CurrentOrdinal + 1} of {_activeTab.Search.Count}");
    }

    private void UpdateFindCount()
    {
        var label = this.FindControl<TextBlock>("FindCountText")!;
        var term = _findBox.Text ?? string.Empty;
        var search = _activeTab?.Search;
        label.Text = !_findVisible || term.Length == 0 || search is null
            ? string.Empty
            : search.Count == 0 ? "0 results" : $"{search.CurrentOrdinal + 1} of {search.Count}";
    }

    private IBrush FindBrush(string key)
    {
        return this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Yellow;
    }

    #endregion

    #region TOC sidebar

    private void OnToggleTocClicked(object? sender, RoutedEventArgs e)
    {
        ToggleToc();
    }

    private void ToggleToc()
    {
        _tocVisible = !_tocVisible;
        UpdateTocPanelVisibility();
        DebugLog.Write($"toc visibility toggled: {_tocVisible}");
    }

    private void UpdateTocPanelVisibility()
    {
        this.FindControl<Border>("TocPanel")!.IsVisible =
            _tocVisible && (_activeTab?.TocEntries.Count ?? 0) > 0;
    }

    /// <summary>Takes the viewer's heading tree and collects the rendered heading controls for one tab.</summary>
    private static void RebuildTocData(DocumentTab tab)
    {
        /*  The viewer builds its table of contents from the Markdig AST while
            rendering, so it covers ATX and setext headings alike and its slugs
            are the very ones its anchor table uses. It comes as a tree
            (Children nested by level); the sidebar is a flat indented list, so
            flatten it back to document order. */
        tab.TocEntries.Clear();
        FlattenToc(tab.Viewer.TableOfContents, tab.TocEntries);

        tab.HeadingControls.Clear();
        tab.HeadingControls.AddRange(tab.Viewer.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(static c => c.Classes.Any(static cl =>
                cl.Length == 11 && cl.StartsWith("markdown-h") && char.IsDigit(cl[10]))));

        if (tab.HeadingControls.Count != tab.TocEntries.Count)
        {
            DebugLog.Write($"toc mismatch: parsed {tab.TocEntries.Count} headings, rendered {tab.HeadingControls.Count}");
        }
        DebugLog.Write($"toc built: {tab.TocEntries.Count} headings");
    }

    private static void FlattenToc(IReadOnlyList<TocEntry>? entries, List<TocEntry> flat)
    {
        if (entries is null)
        {
            return;
        }
        foreach (var entry in entries)
        {
            flat.Add(entry);
            FlattenToc(entry.Children, flat);
        }
    }

    /// <summary>Rebuilds the sidebar button list from the active tab's TOC data.</summary>
    private void RefreshTocPanel()
    {
        var list = this.FindControl<StackPanel>("TocList")!;
        list.Children.Clear();
        _tocButtons.Clear();
        _currentTocIndex = -1;

        if (_activeTab is not null)
        {
            for (var i = 0; i < _activeTab.TocEntries.Count; ++i)
            {
                var entry = _activeTab.TocEntries[i];
                var index = i;
                var button = new Button
                {
                    Content = entry.Text,
                    Margin = new Thickness((entry.Level - 1) * 12, 0, 0, 0),
                };
                button.Classes.Add("TocEntry");
                button.Click += (_, _) => ScrollToHeading(index);
                _tocButtons.Add(button);
                list.Children.Add(button);
            }
        }

        UpdateTocPanelVisibility();
        UpdateCurrentSection();
    }

    private void ScrollToHeading(int index)
    {
        var tab = _activeTab;
        if (tab is null || index < 0 || index >= tab.HeadingControls.Count)
        {
            DebugLog.Write($"scroll-to-heading #{index} out of range");
            return;
        }

        var point = tab.HeadingControls[index].TranslatePoint(new Point(0, 0), tab.Viewer);
        if (point is null)
        {
            return;
        }

        var target = Math.Max(0, tab.ScrollY + point.Value.Y - 8);
        tab.ScrollY = target;
        DebugLog.Write($"scroll-to-heading #{index} '{tab.TocEntries[index].Text}' offset {target:F1}");
        UpdateCurrentSection();
    }

    private void ScrollToAnchor(string slug)
    {
        var tab = _activeTab;
        if (tab is null)
        {
            return;
        }
        var index = tab.TocEntries.FindIndex(e => string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            DebugLog.Write($"anchor link: #{slug}");
            ScrollToHeading(index);
            return;
        }

        /*  Not a heading slug — the viewer also registers anchors for other
            elements (explicit ids, footnotes), so hand it over. Its scroll is
            a BringIntoView jump, hence the fallback rather than first choice:
            the heading path above lands at a known offset. */
        DebugLog.Write($"anchor not a heading, deferring to viewer: #{slug}");
        tab.Viewer.ScrollToAnchor(slug);
    }

    /// <summary>Highlights the TOC entry of the heading nearest above the viewport top.</summary>
    private void UpdateCurrentSection()
    {
        var tab = _activeTab;
        if (tab is null || _tocButtons.Count == 0)
        {
            return;
        }

        var best = 0;
        for (var i = 0; i < tab.HeadingControls.Count && i < _tocButtons.Count; ++i)
        {
            var point = tab.HeadingControls[i].TranslatePoint(new Point(0, 0), tab.Viewer);
            if (point is not null && point.Value.Y <= 12)
            {
                best = i;
            }
        }

        if (best == _currentTocIndex)
        {
            return;
        }

        if (_currentTocIndex >= 0 && _currentTocIndex < _tocButtons.Count)
        {
            _tocButtons[_currentTocIndex].Classes.Remove("TocEntryCurrent");
        }
        _tocButtons[best].Classes.Add("TocEntryCurrent");
        _currentTocIndex = best;
        _tocButtons[best].BringIntoView();
    }

    #endregion
}
