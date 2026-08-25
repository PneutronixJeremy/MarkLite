using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    /*  Velopack update flow (check → silent download → offer restart) plus the
        action wired to the notice banner's single configurable button. */
    private readonly UpdateService _updateService = new();
    private Action? _noticeAction;
    private Action? _noticeSecondaryAction;

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
            /*  A downloaded-but-not-applied update installs itself once this
                process exits, so a plain close still lands on the new version
                next launch. No-op when nothing is pending. */
            _updateService.ApplyOnExit();
        };

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        /*  Style brushes follow the theme via DynamicResource, but syntax
            highlighting bakes colored Runs at render time — a variant switch
            needs one re-render per tab. Inactive tabs defer theirs. */
        ActualThemeVariantChanged += (_, _) =>
        {
            DebugLog.Write($"theme changed to {ActualThemeVariant}; re-rendering");
            RerenderAllTabs();
        };

        /*  Comment visibility is read before the first render so the very first
            document already reflects the saved choice; unset means on. */
        if (UserSettings.ShowHtmlComments is { } showComments)
        {
            HtmlComments.Visible = showComments;
            this.FindControl<MenuItem>("ShowHtmlCommentsItem")!.IsChecked = showComments;
            DebugLog.Write($"html comments restored: {(showComments ? "shown" : "hidden")}");
        }

        /*  Body font: the saved choice first, then the MARKLITE_BODYFONT
            debug/testing env hook on top (scripted font checks can't click
            menus). Both run before the first render. */
        if (UserSettings.BodyFontFamily is { Length: > 0 } savedFont)
        {
            Application.Current!.Resources["MdBodyFontFamily"] = new FontFamily(savedFont);
            SyncBodyFontChecks(savedFont);
            DebugLog.Write($"body font restored: {savedFont}");
        }
        if (Environment.GetEnvironmentVariable("MARKLITE_BODYFONT") is { Length: > 0 } bodyFont)
        {
            Application.Current!.Resources["MdBodyFontFamily"] = new FontFamily(bodyFont);
            SyncBodyFontChecks(bodyFont);
            DebugLog.Write($"body font from env: {bodyFont}");
        }

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
        SingleInstance.StartServer(
            path =>
            {
                DebugLog.Write($"handoff received: {path}");
                OpenFile(path);
                Activate();
            },
            /*  Debug commands arrive on the same pipe (see DebugCommands.cs).
                No Activate() here on purpose: scripted checks must not pull
                focus away from whatever the user is doing. */
            ExecuteDebugCommand);

        this.FindControl<MenuItem>("RegisterOpenWithItem")!.IsChecked = FileAssociation.IsRegistered;

        Opened += (_, _) =>
        {
            DebugLog.Write($"startup: window opened {Program.StartupTimer.ElapsedMilliseconds} ms after process start");
            OfferOpenWith();
            _ = CheckForUpdatesInBackground();
        };
    }

    #region updates and notices

    /*  Installed-copy-only: offer the "Open with" registration in a dismissable
        banner instead of a modal. Semantics are yes / not-now / never: Register
        answers it, "Don't show again" answers it negatively, and the plain ✕
        dismiss leaves it unanswered so the offer returns on the next launch.
        Dev/portable builds never see it (IsInstalled is false there). */
    private void OfferOpenWith()
    {
        if (!_updateService.IsInstalled || FileAssociation.OpenWithOffered || FileAssociation.IsRegistered)
        {
            return;
        }
        ShowNotice(
            "Add MarkLite to the \"Open with\" menu for .md/.markdown files? (Changeable any time under Options.)",
            "Register",
            () =>
            {
                FileAssociation.OpenWithOffered = true;
                FileAssociation.Register();
                this.FindControl<MenuItem>("RegisterOpenWithItem")!.IsChecked = true;
                ShowNotice(
                    "Registered. To make MarkLite the default, use Options → Make MarkLite the default…",
                    actionLabel: null, action: null);
            },
            "Don't show again",
            () =>
            {
                FileAssociation.OpenWithOffered = true;
                HideNotice();
            });
    }

    private async Task CheckForUpdatesInBackground()
    {
        /*  Small delay keeps the first render and the network check apart;
            rendering never waits on updates. Offline or no releases: the
            service logs and returns null. */
        await Task.Delay(TimeSpan.FromSeconds(3));
        var update = await _updateService.CheckAndDownloadAsync();
        if (update is null)
        {
            return;
        }
        /*  Do not steal the banner from an earlier notice (e.g. the Open-with
            offer): the update still applies on exit, and Help > Check for
            updates re-surfaces it on demand. */
        if (!this.FindControl<Border>("NoticeBanner")!.IsVisible)
        {
            ShowNotice($"MarkLite {update.TargetFullRelease.Version} is ready.",
                "Restart to update", _updateService.RestartToApply);
        }
        else
        {
            DebugLog.Write("update banner deferred: notice banner already in use");
        }
    }

    private async void OnCheckForUpdatesClicked(object? sender, RoutedEventArgs e)
    {
        if (!_updateService.IsInstalled)
        {
            ShowNotice("Updates work only in an installed copy — this is a portable/dev build.",
                actionLabel: null, action: null);
            return;
        }
        if (_updateService.Pending is { } pending)
        {
            ShowNotice($"MarkLite {pending.TargetFullRelease.Version} is ready.",
                "Restart to update", _updateService.RestartToApply);
            return;
        }

        ShowNotice("Checking for updates…", actionLabel: null, action: null);
        var update = await _updateService.CheckAndDownloadAsync();
        if (update is not null)
        {
            ShowNotice($"MarkLite {update.TargetFullRelease.Version} is ready.",
                "Restart to update", _updateService.RestartToApply);
        }
        else
        {
            ShowNotice($"MarkLite {_updateService.CurrentVersion} is up to date.",
                actionLabel: null, action: null);
        }
    }

    private void OnRegisterOpenWithClicked(object? sender, RoutedEventArgs e)
    {
        if (FileAssociation.IsRegistered)
        {
            FileAssociation.Unregister();
        }
        else
        {
            FileAssociation.Register();
        }
        this.FindControl<MenuItem>("RegisterOpenWithItem")!.IsChecked = FileAssociation.IsRegistered;
    }

    private void OnMakeDefaultClicked(object? sender, RoutedEventArgs e)
    {
        /*  Windows protects the per-extension default (UserChoice) with a
            hash, so flipping it programmatically is off the table by design —
            open the Settings page and tell the user what to look for. */
        DebugLog.Write("opening Windows default-apps settings");
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        ShowNotice("In Windows Settings, search for \".md\" and pick MarkLite as the default app.",
            actionLabel: null, action: null);
    }

    private void ShowNotice(string text, string? actionLabel, Action? action,
        string? secondaryLabel = null, Action? secondaryAction = null)
    {
        this.FindControl<TextBlock>("NoticeText")!.Text = text;
        var button = this.FindControl<Button>("NoticeActionButton")!;
        button.Content = actionLabel;
        button.IsVisible = actionLabel is not null;
        var secondary = this.FindControl<Button>("NoticeSecondaryButton")!;
        secondary.Content = secondaryLabel;
        secondary.IsVisible = secondaryLabel is not null;
        _noticeAction = action;
        _noticeSecondaryAction = secondaryAction;
        this.FindControl<Border>("NoticeBanner")!.IsVisible = true;
    }

    private void HideNotice()
    {
        this.FindControl<Border>("NoticeBanner")!.IsVisible = false;
        _noticeAction = null;
        _noticeSecondaryAction = null;
    }

    private void OnNoticeActionClicked(object? sender, RoutedEventArgs e)
    {
        _noticeAction?.Invoke();
    }

    private void OnNoticeSecondaryClicked(object? sender, RoutedEventArgs e)
    {
        _noticeSecondaryAction?.Invoke();
    }

    private void OnNoticeDismissClicked(object? sender, RoutedEventArgs e)
    {
        HideNotice();
    }

    #endregion

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

    /// <summary>View > Show HTML comments. Comments are rendered dimmed; other raw HTML stays dropped.</summary>
    private void OnShowHtmlCommentsClicked(object? sender, RoutedEventArgs e)
    {
        SetHtmlCommentsVisible(!HtmlComments.Visible);
    }

    private void SetHtmlCommentsVisible(bool visible)
    {
        HtmlComments.Visible = visible;
        UserSettings.ShowHtmlComments = visible;
        this.FindControl<MenuItem>("ShowHtmlCommentsItem")!.IsChecked = visible;
        DebugLog.Write($"html comments: {(visible ? "shown" : "hidden")}");

        /*  Visibility is decided while the control tree is built, so the change
            only shows after a re-render — the same reason a font or theme
            change re-renders. */
        RerenderAllTabs();
    }

    /// <summary>View > Body font. Swaps the app-level font token and re-renders.</summary>
    private void OnBodyFontClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string fontSpec } clicked)
        {
            return;
        }
        Application.Current!.Resources["MdBodyFontFamily"] = new FontFamily(fontSpec);
        UserSettings.BodyFontFamily = fontSpec;
        SyncBodyFontChecks(fontSpec);
        DebugLog.Write($"body font set: {fontSpec}");

        /*  Replacing an existing app-resource value does not reliably reach
            controls that already resolved it — freshly created controls do,
            so re-render (same trick the theme switch uses). */
        RerenderAllTabs();
    }

    /*  Radio check marks: exactly the entry whose Tag matches shows as chosen.
        Set explicitly rather than trusting MenuItem's own toggle behavior —
        re-clicking the current entry must leave it checked. */
    private void SyncBodyFontChecks(string fontSpec)
    {
        foreach (var item in this.FindControl<MenuItem>("BodyFontMenu")!.Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag as string == fontSpec;
        }
    }

    /*  Re-renders whatever is on screen. Inactive tabs need nothing: they hold
        no control tree, and the render they get on activation already picks up
        the new theme, font or comment setting. */
    private void RerenderAllTabs()
    {
        if (_activeTab is { CurrentText: not null } active)
        {
            RenderTab(active, active.CurrentText, active.ScrollY);
        }
        if (_welcomeViewer is { IsVisible: true })
        {
            _welcomeViewer.Markdown = null;
            _welcomeViewer.Markdown = WelcomeMarkdown;
        }
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

        /*  MarkLite's extension owns every code block, mermaid fences
            included. UseMath re-fronts its narrow math renderer regardless. */
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
            tab.Watcher.Watch(fullPath);
            SetTabStale(tab, null);
            UpdateTabHeader(tab);
            RenderTab(tab, text);
            DebugLog.Write($"opened {fullPath} ({text.Length} chars)");
        }
        catch (Exception ex)
        {
            tab.FilePath = null;
            /*  The error page is the tab's content, not a one-off assignment:
                a tab holds no tree while inactive, so it has to be rendered
                again — from CurrentText — every time the tab comes back. */
            tab.CurrentText = $"# Cannot open file\n\n`{path}`\n\n{ex.Message}";
            tab.Watcher.StopWatching();
            SetTabStale(tab, null);
            RenderTab(tab, tab.CurrentText);

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

    /*  ONE LIVE DOCUMENT PER WINDOW. Only the active tab holds a rendered
        control tree; deactivating a tab drops its tree (Markdown = null) and
        keeps nothing but the text and a scroll offset, so the window's working
        set tracks the document being read rather than the sum of everything
        open. The price is that switching costs a render — measured in the
        "tab switched" log line.

        The viewer itself stays in the host across switches (hidden, not
        removed): its template, and with it the ScrollViewer the scroll hook
        rides on, is built once on first attachment. */
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
            _activeTab.Search.Detach();
            _activeTab.Viewer.Markdown = null;
            DebugLog.Write($"tab scroll saved {_activeTab.SavedScrollY:F1} for '{_activeTab.DisplayName}'; tree dropped");
        }
        if (_welcomeViewer is not null)
        {
            _welcomeViewer.IsVisible = false;
            _welcomeViewer.Markdown = null;
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

        if (tab.CurrentText is not null)
        {
            /*  The incoming tab has no control tree — the switch away dropped
                it — so activation always renders. The render pipeline restores
                the offset and refreshes TOC and search with it. */
            var timer = Stopwatch.StartNew();
            RenderTab(tab, tab.CurrentText, tab.SavedScrollY, afterLayout: () =>
                /*  Posted, not written here: the scroll restore's own second
                    pass is already queued at this priority, so this lands
                    after it and "tab switched" is reliably the LAST line of a
                    switch — which is what scripts wait on. */
                Dispatcher.UIThread.Post(
                    () => DebugLog.Write($"tab switched to '{tab.DisplayName}'; render {timer.ElapsedMilliseconds} ms"),
                    DispatcherPriority.Background));
        }
        else
        {
            DebugLog.Write($"tab switched to '{tab.DisplayName}'; nothing loaded");
            RefreshTocPanel();
            UpdateFindCount();
        }
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
            _viewerHost.Children.Add(_welcomeViewer);
        }
        /*  The welcome page gives its tree back whenever a document takes the
            window (see ActivateTab), so it is rendered on the way in, not
            once at creation. */
        _welcomeViewer.Markdown = null;
        _welcomeViewer.Markdown = WelcomeMarkdown;
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
                /*  Nothing to re-render: the tab holds no tree. The new text
                    is already in CurrentText and is rendered on activation. */
                DebugLog.Write($"reload stored (inactive tab): {tab.FilePath}");
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
        Only the active tab has a viewer with a tree, so a render for any other
        tab is dropped — its text is already in CurrentText and gets rendered
        when the tab is activated. */
    private void RenderTab(DocumentTab tab, string text, double restoreScrollY = 0, Action? afterLayout = null)
    {
        if (tab != _activeTab)
        {
            DebugLog.Write($"render skipped (inactive tab): '{tab.DisplayName}'");
            return;
        }

        MarkActivity();
        tab.Search.Detach();
        /*  Markdown is a styled property: assigning the value it already holds
            raises no change and rebuilds nothing. A re-render with UNCHANGED
            text is exactly what a theme, font or comment-visibility switch
            asks for, so clear it first to force the rebuild. */
        tab.Viewer.Markdown = null;
        tab.Viewer.Markdown = text;
        Dispatcher.UIThread.Post(() =>
        {
            /*  Two-pass restore: at Loaded priority the fresh tree may still
                report a smaller extent, and the first set gets clamped to it.
                The Background pass runs once layout has settled. */
            if (restoreScrollY > 0)
            {
                tab.ScrollY = restoreScrollY;
                Dispatcher.UIThread.Post(() =>
                {
                    if (tab != _activeTab)
                    {
                        return;
                    }
                    tab.ScrollY = restoreScrollY;
                    UpdateCurrentSection();
                    DebugLog.Write($"tab scroll restored {tab.ScrollY:F1} for '{tab.DisplayName}'");
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

        /*  A pending debounce means the shown highlights may not reflect the
            typed term yet — flush it instead of stepping stale matches; the
            fresh Apply already lands on its first match. When the term in the
            box is the one already applied, the pending tick has nothing to
            change, so drop it and step normally: re-running the search there
            would silently reset the current match back to the first. */
        if (_findDebounce.IsEnabled)
        {
            _findDebounce.Stop();
            if (!string.Equals(_activeTab.SearchTerm, _findBox.Text ?? string.Empty, StringComparison.Ordinal))
            {
                RunSearch(scrollToCurrent: true);
                return;
            }
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
