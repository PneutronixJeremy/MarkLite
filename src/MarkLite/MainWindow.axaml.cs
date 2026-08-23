using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;
using MarkLite.Rendering;

namespace MarkLite;

public partial class MainWindow : Window
{
    /*  Temporary default document so the shell has something to render before
        an explicit file is chosen: with no CLI argument the app loads the
        sample fixture relative to the working directory. */
    private const string DefaultDocument = @"testdata\sample.md";

    private readonly MarkdownScrollViewer _viewer;
    private readonly DocumentWatcher _watcher;
    private string? _currentFile;
    private string? _currentText;
    private IStorageFolder? _lastOpenFolder;
    private bool _firstRenderLogged;

    /*  TOC state. _tocVisible is the user's preference (Ctrl+T / View menu)
        and persists across reloads; the panel additionally hides itself when
        the document has no headings. Heading controls are matched to parsed
        entries by document order. */
    private readonly List<TocEntry> _tocEntries = [];
    private readonly List<Control> _headingControls = [];
    private readonly List<Button> _tocButtons = [];
    private bool _tocVisible = true;
    private int _currentTocIndex = -1;

    /*  In-document search. The debounce timer batches keystrokes in the find
        box (a full re-highlight per keypress is wasteful on big documents);
        _findVisible mirrors the find bar and gates F3/Esc handling. */
    private readonly DocumentSearch _search;
    private readonly DispatcherTimer _findDebounce;
    private bool _findVisible;

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

        /*  The markdown control is created in code rather than named in XAML:
            the Avalonia name generator fails to emit a field for x:Name'd
            controls coming from Markdown.Avalonia.Tight (build-time CS0103),
            so the XAML only carries an empty named host. */
        _viewer = new MarkdownScrollViewer
        {
            SelectionEnabled = true,
            SaveScrollValueWhenContentUpdated = true,
            MarkdownStyle = new MarkdownTheme(),
            MaxWidth = 1100,
            Margin = new Thickness(28, 6, 28, 0),
        };

        var plugins = new MdAvPlugins();
        plugins.Plugins.Add(new MarkLitePlugin());
        plugins.HyperlinkCommand = new MarkLiteHyperlinkCommand(
            currentDocumentDirectory: () => _currentFile is null ? null : Path.GetDirectoryName(_currentFile),
            openDocument: LoadFile,
            scrollToAnchor: ScrollToAnchor);
        _viewer.Plugins = plugins;
        _viewer.HeaderScrolled += (_, _) =>
        {
            MarkActivity();
            UpdateCurrentSection();
        };

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

        this.FindControl<ContentControl>("ViewerHost")!.Content = _viewer;

        _search = new DocumentSearch(_viewer);
        _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce.Stop();
            RunSearch(scrollToCurrent: true);
        };
        var findBox = this.FindControl<TextBox>("FindBox")!;
        findBox.TextChanged += (_, _) =>
        {
            MarkActivity();
            _findDebounce.Stop();
            _findDebounce.Start();
        };
        findBox.KeyDown += OnFindBoxKeyDown;

        _watcher = new DocumentWatcher();
        _watcher.ChangeSettled += OnFileChangeSettled;
        Closed += (_, _) => _watcher.Dispose();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        /*  Style brushes follow the theme via DynamicResource, but syntax
            highlighting bakes colored Runs at render time — a variant switch
            needs one re-render of the current document. */
        ActualThemeVariantChanged += (_, _) =>
        {
            if (_currentText is not null)
            {
                DebugLog.Write($"theme changed to {ActualThemeVariant}; re-rendering");
                RenderMarkdown(_currentText);
            }
        };

        LoadFile(args.Length > 0 ? args[0] : DefaultDocument);

        Opened += (_, _) =>
        {
            DebugLog.Write($"startup: window opened {Program.StartupTimer.ElapsedMilliseconds} ms after process start");
        };
    }

    private void LoadFile(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var text = File.ReadAllText(fullPath);
            RenderMarkdown(text);

            _currentFile = fullPath;
            _currentText = text;
            _watcher.Watch(fullPath);
            SetStale(null);
            Title = $"MarkLite — {Path.GetFileName(fullPath)}";
            DebugLog.Write($"opened {fullPath} ({text.Length} chars)");
        }
        catch (Exception ex)
        {
            _viewer.Markdown = $"# Cannot open file\n\n`{path}`\n\n{ex.Message}";
            _currentFile = null;
            _currentText = null;
            _watcher.StopWatching();
            SetStale(null);
            Title = "MarkLite";
            DebugLog.Write($"open failed for {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnFileChangeSettled()
    {
        if (_currentFile is null)
        {
            return;
        }

        if (!File.Exists(_currentFile))
        {
            SetStale("File is missing — showing the last loaded content. It will reload when the file reappears.");
            DebugLog.Write($"file missing: {_currentFile}");
            return;
        }

        try
        {
            var text = File.ReadAllText(_currentFile);
            var savedScroll = _viewer.ScrollValue;
            DebugLog.Write($"reload triggered: {_currentFile}; scroll saved {savedScroll.Y:F1}");

            _currentText = text;
            RenderMarkdown(text, () => DebugLog.Write($"scroll restored {_viewer.ScrollValue.Y:F1}"));
            SetStale(null);
        }
        catch (IOException ex)
        {
            /*  Locked mid-write (or still being copied): keep what is on
                screen; the next file event retries. */
            SetStale("File is locked — showing the last loaded content.");
            DebugLog.Write($"reload failed (locked): {ex.Message}");
        }
    }

    /*  Single render path: preprocess task lists, assign, then after the
        layout pass that realizes the new controls, hide task-item bullets
        (only possible once they are in the visual tree) and run any
        follow-up (e.g. scroll logging). */
    private void MarkActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
        _idleTrimDone = false;
    }

    private void RenderMarkdown(string text, Action? afterLayout = null)
    {
        MarkActivity();

        /*  The assignment below replaces the whole control tree, so search
            undo records would point at discarded controls — forget them now
            and re-apply the active search once the new tree has laid out. */
        _search.Detach();
        _viewer.Markdown = TaskListPreprocessor.Apply(text);
        Dispatcher.UIThread.Post(() =>
        {
            TaskListMarkerHider.Apply(_viewer);
            RebuildToc(text);
            if (_findVisible)
            {
                RunSearch(scrollToCurrent: false);
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

    private void SetStale(string? message)
    {
        var banner = this.FindControl<Border>("StaleBanner")!;
        banner.IsVisible = message is not null;
        if (message is not null)
        {
            this.FindControl<TextBlock>("StaleText")!.Text = message;
        }
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
        LoadFile(path);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault();
        if (file is not null)
        {
            DebugLog.Write($"file dropped: {file.Path.LocalPath}");
            LoadFile(file.Path.LocalPath);
        }
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        Close();
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
        var findBox = this.FindControl<TextBox>("FindBox")!;
        findBox.Focus();
        findBox.SelectAll();
        if (!string.IsNullOrEmpty(findBox.Text))
        {
            RunSearch(scrollToCurrent: false);
        }
    }

    private void CloseFindBar()
    {
        _findVisible = false;
        _findDebounce.Stop();
        this.FindControl<Border>("FindBar")!.IsVisible = false;
        _search.Clear();
        UpdateFindCount();
        _viewer.Focus();
        DebugLog.Write("search closed");
    }

    private void RunSearch(bool scrollToCurrent)
    {
        MarkActivity();
        var term = this.FindControl<TextBox>("FindBox")!.Text ?? string.Empty;
        _search.Apply(term,
            FindBrush("MdSearchMatchBackground"),
            FindBrush("MdSearchCurrentBackground"),
            FindBrush("MdSearchCurrentForeground"),
            scrollToCurrent);
        UpdateFindCount();
        if (term.Length > 0)
        {
            DebugLog.Write($"search '{term}': {_search.Count} matches");
        }
    }

    private void FindMove(bool backward)
    {
        if (!_findVisible)
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

        if (_search.Count == 0)
        {
            return;
        }
        if (backward)
        {
            _search.MovePrevious();
        }
        else
        {
            _search.MoveNext();
        }
        UpdateFindCount();
        DebugLog.Write($"search current {_search.CurrentOrdinal + 1} of {_search.Count}");
    }

    private void UpdateFindCount()
    {
        var label = this.FindControl<TextBlock>("FindCountText")!;
        var term = this.FindControl<TextBox>("FindBox")!.Text ?? string.Empty;
        label.Text = !_findVisible || term.Length == 0
            ? string.Empty
            : _search.Count == 0 ? "0 results" : $"{_search.CurrentOrdinal + 1} of {_search.Count}";
    }

    private IBrush FindBrush(string key)
    {
        return this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Yellow;
    }

    #endregion

    #region TOC sidebar

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
        this.FindControl<Border>("TocPanel")!.IsVisible = _tocVisible && _tocEntries.Count > 0;
    }

    private void RebuildToc(string markdownText)
    {
        _tocEntries.Clear();
        _tocEntries.AddRange(HeadingParser.Parse(markdownText));

        _headingControls.Clear();
        _headingControls.AddRange(_viewer.GetVisualDescendants()
            .OfType<CTextBlock>()
            .Where(static c => c.Classes.Any(static cl => cl.StartsWith("Heading"))));

        if (_headingControls.Count != _tocEntries.Count)
        {
            DebugLog.Write($"toc mismatch: parsed {_tocEntries.Count} headings, rendered {_headingControls.Count}");
        }

        var list = this.FindControl<StackPanel>("TocList")!;
        list.Children.Clear();
        _tocButtons.Clear();
        _currentTocIndex = -1;

        for (var i = 0; i < _tocEntries.Count; ++i)
        {
            var entry = _tocEntries[i];
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

        DebugLog.Write($"toc built: {_tocEntries.Count} headings");
        UpdateTocPanelVisibility();
        UpdateCurrentSection();
    }

    private void ScrollToHeading(int index)
    {
        if (index < 0 || index >= _headingControls.Count)
        {
            DebugLog.Write($"scroll-to-heading #{index} out of range");
            return;
        }

        var point = _headingControls[index].TranslatePoint(new Point(0, 0), _viewer);
        if (point is null)
        {
            return;
        }

        var target = Math.Max(0, _viewer.ScrollValue.Y + point.Value.Y - 8);
        _viewer.ScrollValue = new Vector(_viewer.ScrollValue.X, target);
        DebugLog.Write($"scroll-to-heading #{index} '{_tocEntries[index].Text}' offset {target:F1}");
        UpdateCurrentSection();
    }

    private void ScrollToAnchor(string slug)
    {
        var index = _tocEntries.FindIndex(e => string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            DebugLog.Write($"anchor not found: #{slug}");
            return;
        }
        DebugLog.Write($"anchor link: #{slug}");
        ScrollToHeading(index);
    }

    /// <summary>Highlights the TOC entry of the heading nearest above the viewport top.</summary>
    private void UpdateCurrentSection()
    {
        if (_tocButtons.Count == 0)
        {
            return;
        }

        var best = 0;
        for (var i = 0; i < _headingControls.Count && i < _tocButtons.Count; ++i)
        {
            var point = _headingControls[i].TranslatePoint(new Point(0, 0), _viewer);
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
