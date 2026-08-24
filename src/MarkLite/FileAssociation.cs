using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MarkLite;

/*  Optional, user-invoked "Open with" registration for .md/.markdown —
    HKCU-only, in-process, fully reversible.

    What it writes: a ProgID (HKCU\Software\Classes\MarkLite.md with friendly
    name, icon and open command pointing at the RUNNING exe) plus a
    "MarkLite.md" value under each extension's OpenWithProgids key. That makes
    MarkLite appear in Explorer's "Open with" list — nothing more.

    What it must never touch: the default handler. Explorer stores the user's
    default under FileExts\...\UserChoice, protected by a hash; this class
    never reads or writes UserChoice or anything else under FileExts, so the
    existing double-click handler stays exactly as the user configured it. */
internal static class FileAssociation
{
    private const string ProgId = "MarkLite.md";
    private const string StateKeyPath = @"Software\MarkLite";
    private static readonly string[] Extensions = [".md", ".markdown"];

    /*  One-time flag for the first-run "Open with" offer banner: asked once,
        never nag again, regardless of the answer. Lives outside the ProgID so
        toggling the registration off does not reset it. */
    internal static bool OpenWithOffered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(StateKeyPath);
            return key?.GetValue("OpenWithOffered") is int and not 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(StateKeyPath);
            key.SetValue("OpenWithOffered", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    internal static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return key?.GetValue(null) is string;
        }
    }

    /// <summary>Registers the ProgID and OpenWithProgids entries for the running exe.</summary>
    internal static void Register()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            DebugLog.Write("association register skipped: process path unknown");
            return;
        }

        BackupExistingKeys();

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, "Markdown Document (MarkLite)");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, $"\"{exePath}\",0");
            }
            using (var command = progId.CreateSubKey(@"shell\open\command"))
            {
                command.SetValue(null, $"\"{exePath}\" \"%1\"");
            }
        }

        foreach (var extension in Extensions)
        {
            using var openWith = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{extension}\OpenWithProgids");
            /*  OpenWithProgids values are REG_NONE markers keyed by ProgID;
                an empty byte payload is the conventional form. */
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        DebugLog.Write($"association registered for {string.Join("/", Extensions)} -> {exePath}");
    }

    /// <summary>Removes the ProgID and OpenWithProgids entries. Safe to call when not registered.</summary>
    internal static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);

        foreach (var extension in Extensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{extension}\OpenWithProgids", writable: true);
            if (openWith?.GetValueNames() is { } names && Array.IndexOf(names, ProgId) >= 0)
            {
                openWith.DeleteValue(ProgId, throwOnMissingValue: false);
            }
        }

        DebugLog.Write("association unregistered");
    }

    /// <summary>Uninstall-time cleanup: registration plus MarkLite's own state key — no orphans.</summary>
    internal static void UninstallCleanup()
    {
        Unregister();
        Registry.CurrentUser.DeleteSubKeyTree(StateKeyPath, throwOnMissingSubKey: false);
    }

    /*  Guardrail: before the first write, export the current per-user .md and
        .markdown keys with reg.exe so the prior state is trivially restorable
        (double-click the .reg file). Exports land under %APPDATA%\MarkLite —
        deliberately NOT %LOCALAPPDATA%\MarkLite, which is the Velopack install
        directory and gets wiped on uninstall. A missing source key simply
        produces no file (nothing existed to save). */
    private static void BackupExistingKeys()
    {
        try
        {
            var backupDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarkLite");
            Directory.CreateDirectory(backupDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            foreach (var extension in Extensions)
            {
                var file = Path.Combine(backupDirectory, $"assoc-backup-{stamp}-{extension.TrimStart('.')}.reg");
                var info = new ProcessStartInfo("reg.exe",
                    $"export \"HKCU\\Software\\Classes\\{extension}\" \"{file}\" /y")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(info);
                process?.WaitForExit(5000);
                if (File.Exists(file))
                {
                    DebugLog.Write($"association backup written: {file}");
                }
            }
        }
        catch (Exception ex)
        {
            // Backup is best-effort; registration itself is additive and reversible.
            DebugLog.Write($"association backup failed: {ex.Message}");
        }
    }
}
