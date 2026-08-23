using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        _viewer.HeaderScrolled += (_, _) => UpdateCurrentSection();

        this.FindControl<ContentControl>("ViewerHost")!.Content = _viewer;

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
    private void RenderMarkdown(string text, Action? afterLayout = null)
    {
        _viewer.Markdown = TaskListPreprocessor.Apply(text);
        Dispatcher.UIThread.Post(() =>
        {
            TaskListMarkerHider.Apply(_viewer);
            RebuildToc(text);
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

    #region TOC sidebar

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control)
        {
            ToggleToc();
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
