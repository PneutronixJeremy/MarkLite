using Avalonia;
using System;
using System.Diagnostics;

namespace MarkdigSpike;

class Program
{
    public static readonly Stopwatch StartupClock = new();

    [STAThread]
    public static void Main(string[] args)
    {
        StartupClock.Start();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /*  Software rendering matches MarkLite's configuration (a text viewer gains
        nothing from GL, and it cut ~30-40 MB working set in Phase 5) — Gate 3
        footprint numbers must be measured under identical conditions. */
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software }
            })
            .LogToTrace();
}
