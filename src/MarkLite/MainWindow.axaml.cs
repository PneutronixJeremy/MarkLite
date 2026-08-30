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
using MarkLite.Rendering.Virtual;
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
    private VirtualMarkdownView? _welcomeViewer;
    private readonly MarkLiteHyperlinkCommand _hyperlinkCommand;

    /*  One renderer-extension instance shared by all tabs; it is stateless
        across render passes. The pipeline it renders is MarkLitePipeline.Shared. */
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

    /*  Sidebar width. The column is what the GridSplitter drags and what the
        layout reads; _tocWidth is the remembered value, kept apart so hiding
        the panel (column to zero) and showing it again round-trips the
        reader's width. Clamped so a stray registry value cannot produce a
        sidebar that is invisible or swallows the document. */
    private const double TocDefaultWidth = 250;
    private const double TocMinWidth = 140;
    private const double TocMaxWidth = 600;
    private readonly ColumnDefinition _tocColumn;
    private readonly GridSplitter _tocSplitter;
    private double _tocWidth = TocDefaultWidth;

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

        /*  Sidebar width: the stored value is applied when the panel first
            shows (UpdateTocPanelVisibility), so it is only remembered here. A
            drag persists on release, not per pixel; a double-click on the
            strip is the way back to the default. */
        _tocColumn = this.FindControl<Grid>("ContentGrid")!.ColumnDefinitions[0];
        _tocSplitter = this.FindControl<GridSplitter>("TocSplitter")!;
        if (UserSettings.TocWidth is { } storedTocWidth)
        {
            _tocWidth = ClampTocWidth(storedTocWidth);
            DebugLog.Write($"toc width restored: {_tocWidth:F0}");
        }
        /*  DoubleTapped fires on the second press; that press's release still
            raises DragCompleted, and if no layout pass ran in between the
            column's ActualWidth is the OLD width — reading it would undo the
            reset. The flag makes that release re-apply the default instead. */
        var tocResetPending = false;
        _tocSplitter.DoubleTapped += (_, _) =>
        {
            tocResetPending = true;
            SetTocWidth(TocDefaultWidth, persist: true);
        };
        _tocSplitter.DragCompleted += (_, _) =>
        {
            if (tocResetPending)
            {
                tocResetPending = false;
                SetTocWidth(TocDefaultWidth, persist: true);
            }
            else
            {
                SetTocWidth(_tocColumn.ActualWidth, persist: true);
            }
        };

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
            /*  Before the tabs go: the active tab's scroll position is live
                only while its viewer is. Every other change saves as it
                happens, because an update restart never reaches this handler. */
            SaveSession();

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

        /*  Line numbers: read before the first render, like the comment
            toggle. Unset means off. */
        if (UserSettings.ShowLineNumbers is { } showLineNumbers)
        {
            GutterPanel.Enabled = showLineNumbers;
            this.FindControl<MenuItem>("ShowLineNumbersItem")!.IsChecked = showLineNumbers;
            DebugLog.Write($"line numbers restored: {(showLineNumbers ? "shown" : "hidden")}");
        }

        /*  Wide scroll bars: the XAML default is on (the window carries the
            class); a stored choice overrides it before the first render so the
            bars never flip in front of the reader. */
        if (UserSettings.WideScrollBars is { } wideScrollBars)
        {
            ApplyWideScrollBars(wideScrollBars);
            DebugLog.Write($"wide scroll bars restored: {(wideScrollBars ? "on" : "off")}");
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

        /*  Reopen last session: unset means on. The stored tabs come back
            first, so a file argument opens ON TOP of them and ends up active —
            the session is the app's own state and the argument is an addition
            to it, which is how Notepad behaves. */
        var restoreSession = UserSettings.RestoreSession ?? true;
        this.FindControl<MenuItem>("ReopenSessionItem")!.IsChecked = restoreSession;
        var sessionRestored = RestoreSession();

        if (args.Length > 0)
        {
            OpenFile(args[0]);
        }
        else if (!sessionRestored)
        {
            ShowWelcome();
        }

        /*  Only the primary instance holds the pipe; StartServer no-ops in a
            standalone secondary. */
        SingleInstance.StartServer(
            path =>
            {
                DebugLog.Write($"handoff received: {path}");
                OpenFile(path);
                RaiseToForeground();
            },
            /*  Debug commands arrive on the same pipe (see DebugCommands.cs).
                No Activate() here on purpose: scripted checks must not pull
                focus away from whatever the user is doing. */
            ExecuteDebugCommand);

        this.FindControl<MenuItem>("RegisterOpenWithItem")!.IsChecked = FileAssociation.IsRegistered;
        this.FindControl<MenuItem>("VersionItem")!.Header = $"MarkLite {AppVersion.Display}";

        Opened += (_, _) =>
        {
            DebugLog.Write($"startup: window opened {Program.StartupTimer.ElapsedMilliseconds} ms after process start");
            ApplySessionScroll();
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

    private void OnReopenSessionClicked(object? sender, RoutedEventArgs e)
    {
        SetSessionRestore(this.FindControl<MenuItem>("ReopenSessionItem")!.IsChecked);
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

    /*  Brings the window to the user after a handoff. Three separate things
        have to happen and only the first is Avalonia's:

        - a minimized window has to be RESTORED, or it comes forward as a
          taskbar button and the document is still out of sight;
        - Activate() asks the platform to raise us;
        - SetForegroundWindow is what actually moves the foreground, and it
          only succeeds because the secondary launch handed its right over
          before writing to the pipe (see SingleInstance.SendToPrimary).

        Everything here is logged: a refused raise is invisible on screen
        except as a flashing taskbar button, which is exactly the failure this
        replaced. */
    private void RaiseToForeground()
    {
        var wasMinimized = WindowState == WindowState.Minimized;
        if (wasMinimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var raised = handle != IntPtr.Zero && NativeMethods.SetForegroundWindow(handle);
        DebugLog.Write(
            $"handoff raise: {(wasMinimized ? "restored from minimized" : "already visible")}, "
            + $"SetForegroundWindow {(raised ? "succeeded" : "refused")}");
    }

    private void MarkActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
        _idleTrimDone = false;
    }

    private static VirtualMarkdownView CreateViewer()
    {
        /*  The view must NOT be given Pipeline/BaseUri/Source: each of those
            drives the base class's own render path, which would replace its
            panel with a fully realized document. It parses with
            MarkLitePipeline.Shared itself. */
        var viewer = new VirtualMarkdownView { MaxWidth = 1100 };
        /*  A strip on each side of the document is reserved for the
            line-number gutter, so the outer margin is narrower than the text
            column it ends up producing. Symmetric, so the document stays
            centred whether or not the numbers are drawn. */
        viewer.Margin = new Thickness(8, 6, 8, 0);
        /*  The sidebar shows every heading level, and its entries are paired
            positionally with the model's headings — a shallower depth would
            drop deep headings from the list while their counterparts remain,
            breaking that pairing. */
        viewer.TableOfContentsMaxDepth = 6;
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

    /// <summary>View > Show line numbers. The gutter's width is reserved either way, so this
    /// only decides whether the numbers are drawn.</summary>
    private void OnShowLineNumbersClicked(object? sender, RoutedEventArgs e)
    {
        SetLineNumbersVisible(!GutterPanel.Enabled);
    }

    private void SetLineNumbersVisible(bool visible)
    {
        GutterPanel.Enabled = visible;
        UserSettings.ShowLineNumbers = visible;
        this.FindControl<MenuItem>("ShowLineNumbersItem")!.IsChecked = visible;
        DebugLog.Write($"line numbers: {(visible ? "shown" : "hidden")}");

        /*  A repaint, not a re-render: the strip is already reserved, so nothing
            moves and no block has to be rebuilt. */
        _activeTab?.Viewer.Panel.InvalidateGutter();
    }

    /// <summary>View > Wide scroll bars: every bar keeps Fluent's expanded look instead of
    /// collapsing to the thin idle strip. Layout is untouched either way.</summary>
    private void OnWideScrollBarsClicked(object? sender, RoutedEventArgs e)
    {
        SetWideScrollBars(!WideScrollBarsOn);
    }

    /// <summary>Whether the window carries the class the wide-bar style keys on.</summary>
    private bool WideScrollBarsOn => Classes.Contains("WideScrollBars");

    private void SetWideScrollBars(bool wide)
    {
        ApplyWideScrollBars(wide);
        UserSettings.WideScrollBars = wide;
        DebugLog.Write($"wide scroll bars: {(wide ? "on" : "off")}");
    }

    /*  The class on the window is the whole mechanism: a window-level style
        turns AllowAutoHide off on every descendant ScrollViewer, template
        children included, so no viewer has to be tracked individually. */
    private void ApplyWideScrollBars(bool wide)
    {
        if (wide)
        {
            if (!WideScrollBarsOn)
            {
                Classes.Add("WideScrollBars");
            }
        }
        else
        {
            Classes.Remove("WideScrollBars");
        }
        this.FindControl<MenuItem>("WideScrollBarsItem")!.IsChecked = wide;
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

    /*  Rebuilds whatever is on screen. Inactive tabs need nothing: they hold
        no control tree, and the render they get on activation already picks up
        the new theme, font or comment setting.

        Same text, different controls — theme, body font and comment visibility
        are all decided while a control is BUILT, never afterwards. The parsed
        model is unaffected, so it (and the reader's place in it) is kept and
        only what had been realized is dropped. */
    private void RerenderAllTabs()
    {
        if (_activeTab is { CurrentText: not null } active)
        {
            MarkActivity();
            active.Search.Detach();
            active.Viewer.ResetLayout();
            DebugLog.Write($"layout reset for '{active.DisplayName}'; model kept");
            Dispatcher.UIThread.Post(() =>
            {
                if (active != _activeTab)
                {
                    return;
                }
                UpdateCurrentSection();
                if (_findVisible && active.SearchTerm.Length > 0)
                {
                    RunSearch(scrollToCurrent: false);
                }
            }, DispatcherPriority.Background);
        }
        if (_welcomeViewer is { IsVisible: true } welcome)
        {
            welcome.ResetLayout();
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
        this.FindControl<WrapPanel>("TabStrip")!.Children.Add(tab.StripItem);
        ActivateTab(tab);
        LoadIntoTab(tab, fullPath ?? path);
        UpdateTabStripVisibility();
        SaveSession();
        DebugLog.Write($"tab opened '{tab.DisplayName}' ({_tabs.Count} tabs)");
    }

    private DocumentTab CreateTab()
    {
        var viewer = CreateViewer();

        /*  Added to the host straight away and kept there for the tab's whole
            life; ActivateTab only flips visibility (see the note there). */
        viewer.IsVisible = false;
        _viewerHost.Children.Add(viewer);

        /*  MarkLite's extension owns every code block, mermaid fences included;
            the math extension re-fronts its own narrow renderer regardless.

            Deliberately NOT viewer.UseMath(): that helper also REPLACES
            Pipeline with a freshly built one, and assigning Pipeline drives
            the base class's own render path — which rebuilds Content from
            scratch and throws the virtualizing panel away. Registering the
            extension alone has the same rendering effect, because
            MarkLitePipeline.Shared already parses maths. */
        viewer.Extensions.Add(RenderExtension);
        viewer.Extensions.AddMath();
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
            Search = new VirtualDocumentSearch(viewer),
            StripItem = item,
            StripLabel = label,
        };

        /*  Current-section tracking rides the viewer's scroll events, which
            it raises from its template ScrollViewer once that exists. */
        viewer.ViewScrollChanged += (_, _) =>
        {
            MarkActivity();
            if (_activeTab == tab)
            {
                UpdateCurrentSection();
                MarkSessionDirty();
            }
        };
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
            _activeTab.SavedScroll = _activeTab.CaptureScroll();
            _activeTab.StripItem.Classes.Remove("TabItemActive");
            _activeTab.Viewer.IsVisible = false;
            _activeTab.Search.Detach();
            /*  TocEntries are plain model data and stay — the sidebar keeps
                working for a tab that holds no controls. */
            _activeTab.Viewer.Clear();
            DebugLog.Write($"tab scroll saved {_activeTab.SavedScroll.Describe} for '{_activeTab.DisplayName}'; tree dropped");
        }
        if (_welcomeViewer is not null)
        {
            _welcomeViewer.IsVisible = false;
            _welcomeViewer.Clear();
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

        /*  The switch just captured the outgoing tab's position and changed
            which tab is active — both belong in the stored session, and an
            update restart may never give another chance to write them. */
        SaveSession();

        if (tab.CurrentText is not null)
        {
            /*  The incoming tab has no control tree — the switch away dropped
                it — so activation always renders. The render pipeline restores
                the offset and refreshes TOC and search with it. */
            var timer = Stopwatch.StartNew();
            RenderTab(tab, tab.CurrentText, tab.SavedScroll, afterLayout: () =>
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
        this.FindControl<WrapPanel>("TabStrip")!.Children.Remove(tab.StripItem);
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
        SaveSession();
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
        _welcomeViewer.Load(WelcomeMarkdown);
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
                var savedScroll = tab.CaptureScroll();
                DebugLog.Write($"reload triggered: {tab.FilePath}; scroll saved {savedScroll.Describe}");
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
    private void RenderTab(DocumentTab tab, string text, ScrollRestore restore = default, Action? afterLayout = null)
    {
        if (tab != _activeTab)
        {
            DebugLog.Write($"render skipped (inactive tab): '{tab.DisplayName}'");
            return;
        }

        MarkActivity();
        tab.Search.Detach();
        tab.Viewer.Load(text);
        Dispatcher.UIThread.Post(() =>
        {
            /*  Two-pass restore: at Loaded priority the fresh tree may still
                report a smaller extent, and the first set gets clamped to it.
                The Background pass runs once layout has settled. */
            if (restore.MovesTheView)
            {
                tab.RestoreScroll(restore);
                Dispatcher.UIThread.Post(() =>
                {
                    if (tab != _activeTab)
                    {
                        return;
                    }
                    var block = tab.RestoreScroll(restore);
                    UpdateCurrentSection();
                    var where = block >= 0 ? $"{tab.ScrollY:F1} block {block}" : $"{tab.ScrollY:F1}";
                    DebugLog.Write($"tab scroll restored {where} for '{tab.DisplayName}'");
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
        /*  Ctrl+A and Ctrl+C reach the document only when the focus is not in a
            text box: in the find box they mean "select what I typed" and "copy
            what I typed", which is what the box itself does with them. Same
            reason the code panel's own SelectableTextBlock keeps its copy — a
            reader who selected code inside one block expects Ctrl+C to give them
            that, not the document selection. */
        if (e.KeyModifiers == KeyModifiers.Control && !IsTextInputFocused())
        {
            if (e.Key == Key.A)
            {
                SelectAllInDocument();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C && !IsSelectableTextFocused())
            {
                CopyDocumentSelection();
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Escape)
        {
            if (_findVisible)
            {
                CloseFindBar();
                e.Handled = true;
                return;
            }
            if (ActiveSelection is { IsEmpty: false } selection)
            {
                selection.Clear();
                DebugLog.Write("selection cleared");
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    #region selection and copy

    /// <summary>The selection of whichever document is on screen, welcome page included.</summary>
    private DocumentSelection? ActiveSelection => (_activeTab?.Viewer ?? _welcomeViewer)?.Selection;

    private bool IsTextInputFocused() => FocusManager?.GetFocusedElement() is TextBox;

    private bool IsSelectableTextFocused() =>
        FocusManager?.GetFocusedElement() is SelectableTextBlock;

    private void OnSelectAllClicked(object? sender, RoutedEventArgs e) => SelectAllInDocument();

    private void OnCopyClicked(object? sender, RoutedEventArgs e) => CopyDocumentSelection();

    private void SelectAllInDocument()
    {
        if (ActiveSelection is { } selection)
        {
            selection.SelectAll();
            DebugLog.Write($"selection {selection.Describe()}");
            return;
        }
        //  Classic viewer: MarkView's own layer covers the whole rendered tree.
        (_activeTab?.Viewer ?? _welcomeViewer)?.SelectAll();
    }

    /*  Copy hands over the MARKDOWN SOURCE the selection covers, not the
        rendered text — the decision recorded for this viewer: what a reader
        pastes elsewhere should be the document they are reading, tables, links
        and code fences intact. */
    private void CopyDocumentSelection()
    {
        if (ActiveSelection is not { } selection)
        {
            _ = (_activeTab?.Viewer ?? _welcomeViewer)?.CopyToClipboardAsync();
            return;
        }

        var text = selection.CopyText();
        if (text.Length == 0)
        {
            DebugLog.Write("copy: nothing selected");
            return;
        }
        if (Clipboard is null)
        {
            DebugLog.Write("copy: no clipboard");
            return;
        }
        /*  Avalonia 12 has no SetTextAsync: the clipboard takes a data transfer,
            which is also what makes it possible to offer several formats later
            without changing the call site. */
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        _ = Clipboard.SetDataAsync(transfer);
        DebugLog.Write($"copied {text.Length} chars of markdown");
    }

    #endregion

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
        var visible = _tocVisible && (_activeTab?.TocEntries.Count ?? 0) > 0;
        this.FindControl<Border>("TocPanel")!.IsVisible = visible;

        /*  A hidden Border still leaves its fixed-width column standing, so
            the column itself is collapsed — MinWidth first, or the zero width
            would be clamped back up. Showing restores the remembered width. */
        if (visible)
        {
            _tocColumn.MinWidth = TocMinWidth;
            _tocColumn.Width = new GridLength(_tocWidth);
        }
        else
        {
            _tocColumn.MinWidth = 0;
            _tocColumn.Width = new GridLength(0);
        }
        _tocSplitter.IsVisible = visible;
    }

    private static double ClampTocWidth(double width)
    {
        return Math.Clamp(width, TocMinWidth, TocMaxWidth);
    }

    /// <summary>Sets the sidebar width — a splitter release, a double-click reset or the
    /// debug command all land here. Clamped; the column follows only while the panel shows.</summary>
    private void SetTocWidth(double width, bool persist)
    {
        _tocWidth = ClampTocWidth(width);
        if (this.FindControl<Border>("TocPanel")!.IsVisible)
        {
            _tocColumn.Width = new GridLength(_tocWidth);
        }
        if (persist)
        {
            UserSettings.TocWidth = (int)Math.Round(_tocWidth);
        }
        DebugLog.Write($"toc width: {_tocWidth:F0}");
    }

    /// <summary>Refills one tab's sidebar heading list from its parsed model.</summary>
    private static void RebuildTocData(DocumentTab tab)
    {
        /*  The model holds every heading whether or not it is on screen, so the
            sidebar is complete from the first frame: nothing is walked, and no
            rendered heading control is needed. Headings come as a tree
            (Children nested by level) and the sidebar is a flat indented list,
            so flatten them back to document order. */
        tab.TocEntries.Clear();
        if (tab.Viewer.Model is { } model)
        {
            FlattenToc(model.TocEntries, tab.TocEntries);
        }
        DebugLog.Write($"toc built from model: {tab.TocEntries.Count} headings");
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
        if (_activeTab is not { Viewer.Model: { } model } tab)
        {
            return;
        }
        if (index < 0 || index >= model.Headings.Count)
        {
            DebugLog.Write($"scroll-to-heading #{index} out of range");
            return;
        }

        /*  The heading may not be realized, so there is no control to measure
            against — the block index is the address instead. The panel corrects
            the landing itself once the blocks it just realized have real
            heights; the current section is refreshed after that correction as
            well as before it. */
        var heading = model.Headings[index];
        tab.Viewer.Panel.ScrollToBlock(heading.BlockIndex, -8);
        DebugLog.Write($"scroll-to-heading #{index} '{heading.Text}' block {heading.BlockIndex} "
            + $"offset {tab.ScrollY:F1}");
        UpdateCurrentSection();
        Dispatcher.UIThread.Post(UpdateCurrentSection, DispatcherPriority.Background);
    }

    private void ScrollToAnchor(string slug)
    {
        var tab = _activeTab;
        if (tab is null)
        {
            return;
        }

        /*  The model's anchor table covers headings, footnote definitions and
            explicit ids alike, and resolves each to the block a jump has to
            realize — so there is no reason to go through the sidebar's heading
            list first. */
        if (tab.Viewer.ScrollToModelAnchor(slug))
        {
            DebugLog.Write($"anchor link: #{slug}");
            UpdateCurrentSection();
            Dispatcher.UIThread.Post(UpdateCurrentSection, DispatcherPriority.Background);
        }
        else
        {
            DebugLog.Write($"anchor not found: #{slug}");
        }
    }

    /// <summary>Highlights the TOC entry of the heading nearest above the viewport top.</summary>
    private void UpdateCurrentSection()
    {
        if (_activeTab is not { Viewer.Model: { } model } tab || _tocButtons.Count == 0)
        {
            return;
        }

        /*  Block-level first: the last heading that starts at or above the
            topmost visible block. The 12 px of grace are there because a
            heading scrolled to sits just below the viewport top. */
        var best = 0;
        var panel = tab.Viewer.Panel;
        var firstVisible = panel.BlockNearViewportTop(12);
        for (var i = 0; i < model.Headings.Count && i < _tocButtons.Count; ++i)
        {
            if (model.Headings[i].BlockIndex <= firstVisible)
            {
                best = i;
            }
        }

        /*  Then the refinement, where the answer can actually be checked: a
            realized heading has a real Y. It matters when one top-level block
            holds several headings (a quote, a list item) and when a tall block
            starts above the viewport while its headings are still below it —
            both cases the block index alone gets wrong. Headings are in
            document order, so the first realized one below the line ends the
            search. */
        var refined = -1;
        for (var i = 0; i < model.Headings.Count && i < _tocButtons.Count; ++i)
        {
            if (RealizedHeadingTop(tab, panel, model.Headings[i]) is not { } y)
            {
                continue;
            }
            if (y > 12)
            {
                break;
            }
            refined = i;
        }
        if (refined >= 0)
        {
            best = refined;
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

    /*  Y of a heading's own control relative to the viewer, when the block
        holding it is realized. The slug is the identity rather than the
        position: one block can hold several headings, and the realizer tags
        each control with the model's slug for exactly this lookup. */
    private static double? RealizedHeadingTop(
        DocumentTab tab, VirtualBlockPanel panel, MarkdownDocumentModel.HeadingInfo heading)
    {
        /*  A container realized during THIS scroll event has not been arranged
            yet: it still reports the position it had before the jump, which
            would put the reader in whichever section happened to be on screen
            a moment ago. Only an arranged container knows where it is; until
            then the block index is the better answer. */
        if (panel.GetRealized(heading.BlockIndex) is not { IsArrangeValid: true } container)
        {
            return null;
        }
        if (container.TranslatePoint(new Point(0, 0), tab.Viewer)?.Y is not { } containerTop)
        {
            return null;
        }

        foreach (var descendant in container.GetVisualDescendants().OfType<TextBlock>())
        {
            if (descendant.Tag as string != heading.Slug)
            {
                continue;
            }
            /*  The heading's LAYOUT SLOT, not its rendered box: a heading
                carries a top margin that separates it from what came before,
                and a jump aims at the block, so the drawn glyphs sit that
                margin lower than the block does. Measuring the box would put
                a heading the reader was just sent to below the line that
                decides "am I in this section yet". Never above the block's own
                top, which is where a block's first heading belongs. */
            var y = descendant.TranslatePoint(new Point(0, 0), tab.Viewer)?.Y;
            return y is null
                ? containerTop
                : Math.Max(containerTop, y.Value - descendant.Margin.Top);
        }
        return null;
    }

    #endregion
}
