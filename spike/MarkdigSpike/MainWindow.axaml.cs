using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using Markdig;
using MarkView.Avalonia;

namespace MarkdigSpike;

public partial class MainWindow : Window
{
    private readonly MarkdownViewer _viewer;
    private bool _firstRenderLogged;

    public MainWindow()
    {
        InitializeComponent();

        _viewer = new MarkdownViewer();
        _viewer.Pipeline = new Markdig.MarkdownPipelineBuilder()
            .UseSupportedExtensions()
            .UseMathematics()
            .Build();
        ScrollViewer.SetHorizontalScrollBarVisibility(_viewer,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _viewer.Extensions.Add(new ProminentTaskListExtension());
        _viewer.UseMermaid();
        _viewer.UseTextMateHighlighting();
        _viewer.UseMath();
        Content = _viewer;

        string path = ResolveDocumentPath();
        try
        {
            _viewer.Markdown = File.ReadAllText(path);
            Console.Error.WriteLine($"[spike] loaded {path}");
        }
        catch (Exception ex)
        {
            _viewer.Markdown = $"# Load error\n\n`{path}`\n\n{ex.Message}";
        }

        Opened += (_, _) =>
        {
            if (!_firstRenderLogged)
            {
                _firstRenderLogged = true;
                /*  Log after layout settles so the number is comparable to
                    MarkLite's first-content-render metric (post-layout pass). */
                Dispatcher.UIThread.Post(
                    () => Console.Error.WriteLine(
                        $"[spike] first render {Program.StartupClock.ElapsedMilliseconds} ms"),
                    DispatcherPriority.Background);
                DispatcherTimer.RunOnce(LogBounds, TimeSpan.FromSeconds(3));
            }
        };
    }

    private void LogBounds()
    {
        Console.Error.WriteLine($"[spike] window ClientSize={ClientSize} scale={RenderScaling}");
        Console.Error.WriteLine($"[spike] viewer Bounds={_viewer.Bounds}");
        if (_viewer.Content is Control grid)
        {
            Console.Error.WriteLine($"[spike] contentGrid Bounds={grid.Bounds}");
        }
        Avalonia.Visual? sv = FindDescendant(_viewer, v => v is ScrollViewer);
        if (sv is ScrollViewer scroller)
        {
            Console.Error.WriteLine(
                $"[spike] scrollviewer Viewport={scroller.Viewport} Extent={scroller.Extent} HSB={ScrollViewer.GetHorizontalScrollBarVisibility(scroller)}");
        }
    }

    private static Avalonia.Visual? FindDescendant(Avalonia.Visual root, Func<Avalonia.Visual, bool> match)
    {
        foreach (Avalonia.Visual child in global::Avalonia.VisualTree.VisualExtensions.GetVisualChildren(root))
        {
            if (match(child))
            {
                return child;
            }
            Avalonia.Visual? found = FindDescendant(child, match);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static string ResolveDocumentPath()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
        {
            return Path.GetFullPath(args[1]);
        }

        // No arg: probe upward from the exe for the repo's test fixture.
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "testdata", "sample.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return "sample.md";
    }
}
