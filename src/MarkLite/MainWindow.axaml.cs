using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
            openDocument: LoadFile);
        _viewer.Plugins = plugins;

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
            afterLayout?.Invoke();
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
}
