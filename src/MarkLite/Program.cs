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

        /*  Single-instance: with a file argument, hand it to a running primary
            instance and exit instead of opening a second window. A primary
            that cannot be reached (or a no-argument launch when one exists)
            falls through and runs standalone. */
        if (!SingleInstance.TryBecomePrimary() && args.Length > 0)
        {
            var fullPath = System.IO.Path.GetFullPath(args[0]);
            if (SingleInstance.SendToPrimary(fullPath))
            {
                DebugLog.Write($"handed off to primary instance: {fullPath}; exiting");
                return;
            }
            DebugLog.Write("primary instance unreachable; running standalone");
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                /*  Software rendering: a text-document viewer gains nothing
                    from ANGLE/GL, and skipping the GL contexts saves tens of
                    MB of working set. Skia's CPU raster handles scrolling
                    text easily. */
                RenderingMode = [Win32RenderingMode.Software],
            })
            .LogToTrace();
    }
}
