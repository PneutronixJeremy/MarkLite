using System;
using System.Diagnostics;
using Avalonia;
using Velopack;

/*  MarkLite is Windows-only by design (win-x64 RID, Win32 rendering options,
    registry-based association, Velopack Windows hooks). Declaring it here
    keeps the platform-compatibility analyzer honest instead of per-call
    suppressions. */
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

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

        /*  Velopack owns install/update/uninstall lifecycle invocations: on
            those special launches (Setup, updater, uninstaller) it runs its
            hooks and exits before any UI code; on a normal launch it returns
            immediately. Must come before everything else. The uninstall hook
            removes the optional "Open with" registry keys so an uninstall
            leaves nothing behind. */
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => FileAssociation.UninstallCleanup())
            .Run();

        DebugLog.Write($"version {AppVersion.Display}");

        /*  Single-instance: with a file argument, hand it to a running primary
            instance and exit instead of opening a second window. A primary
            that cannot be reached (or a no-argument launch when one exists)
            falls through and runs standalone. MARKLITE_STANDALONE=1 skips the
            mechanism entirely — a second process neither claims the pipe nor
            hands off (used by verification scripts; also handy for comparing
            builds side by side). */
        /*  "--cmd <text>" is the scripted control surface: send one debug
            command to the running primary instance and exit without opening a
            window. The primary only acts on it when MARKLITE_DEBUG=1 is set
            there, so a stray command against a normal launch does nothing.
            Exit code 1 means the primary could not be reached. */
        if (args.Length > 0 && args[0] == "--cmd")
        {
            var command = string.Join(' ', args[1..]);
            if (SingleInstance.SendToPrimary(new DebugCommand(command)))
            {
                DebugLog.Write($"cmd sent: {command}");
            }
            else
            {
                DebugLog.Write($"cmd not sent (no primary instance): {command}");
                Environment.ExitCode = 1;
            }
            return;
        }

        var standalone = Environment.GetEnvironmentVariable("MARKLITE_STANDALONE") == "1";
        if (!standalone && !SingleInstance.TryBecomePrimary() && args.Length > 0)
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
            /*  Bundled fonts must be REGISTERED as a collection — an ad-hoc
                "avares://…#Family" FontFamily quietly falls back to the system
                default under Avalonia 12 (verified by glyph comparison).
                Font specs reference the collection as "fonts:MarkLite#Name". */
            .ConfigureFonts(static manager => manager.AddFontCollection(
                new Avalonia.Media.Fonts.EmbeddedFontCollection(
                    new Uri("fonts:MarkLite", UriKind.Absolute),
                    new Uri("avares://MarkLite/Assets/Fonts", UriKind.Absolute))))
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
