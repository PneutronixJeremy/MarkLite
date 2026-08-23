using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Markdown.Avalonia;

namespace MarkLite;

public partial class MainWindow : Window
{
    /*  Temporary default document so the shell has something to render before
        real file opening lands: with no CLI argument the app loads the sample
        fixture relative to the working directory. */
    private const string DefaultDocument = @"testdata\sample.md";

    private readonly MarkdownScrollViewer _viewer;

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
        _viewer = new MarkdownScrollViewer();
        this.FindControl<ContentControl>("ViewerHost")!.Content = _viewer;

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
            _viewer.Markdown = text;
            Title = $"MarkLite — {Path.GetFileName(fullPath)}";
            DebugLog.Write($"loaded {fullPath} ({text.Length} chars)");
        }
        catch (Exception ex)
        {
            _viewer.Markdown = $"# Cannot open file\n\n`{path}`\n\n{ex.Message}";
            Title = "MarkLite";
            DebugLog.Write($"load failed for {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        DebugLog.Write("File > Open invoked (not implemented yet)");
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
