using System;
using System.Diagnostics;
using Avalonia;

namespace MarkLite;

internal static class Program
{
    /*  Started explicitly at the top of Main — a field initializer would run
        lazily at first access under beforefieldinit semantics and report a
        meaningless near-zero elapsed time. Read by MainWindow when
        MARKLITE_DEBUG=1 logging is on. */
    internal static readonly Stopwatch StartupTimer = new();

    [STAThread]
    public static void Main(string[] args)
    {
        StartupTimer.Start();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
