using System;
using System.Linq;
using Microsoft.Win32;

namespace MarkLite;

/*  Per-user settings, stored under HKCU\Software\MarkLite — the same state key
    FileAssociation uses, so the Velopack uninstall cleanup removes everything
    in one sweep. Registry over a settings file: no path/roaming questions, and
    the app already touches HKCU for the association feature. */
internal static class UserSettings
{
    private const string KeyPath = @"Software\MarkLite";

    /*  Where the reopen-last-session state lives. A launch that sets
        MARKLITE_INSTANCE forms its own single-instance group — that is how a
        verification run drives a MarkLite of its own while the user keeps
        working — and it gets its own session store to match. Without that,
        scripted runs would inherit the user's open documents, and worse, hand
        their fixtures back to the user's next launch. Scoping rather than
        disabling keeps the feature itself testable: a script exercises the
        real code against a key nobody else reads.

        The instance name is script-chosen, so only word characters survive
        into the key path — a stray backslash would otherwise pick the subkey. */
    private static string SessionKeyPath { get; } = BuildSessionKeyPath();

    private static string BuildSessionKeyPath()
    {
        if (Environment.GetEnvironmentVariable("MARKLITE_INSTANCE") is not { Length: > 0 } instance)
        {
            return KeyPath;
        }

        var safe = new string(instance.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return $@"{KeyPath}\Instances\{(safe.Length > 0 ? safe : "instance")}";
    }

    /*  View > Show HTML comments. Default ON: a comment is content the author
        wrote, and silently hiding it is what made this setting necessary. Null
        means never set, which the caller reads as the default. */
    internal static bool? ShowHtmlComments
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("ShowHtmlComments") is int stored ? stored != 0 : null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value is null)
            {
                key.DeleteValue("ShowHtmlComments", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("ShowHtmlComments", value.Value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    /*  View > Show line numbers. Default OFF: the gutter answers "where is this
        in the file", which is a question only some readers are asking. The
        space it draws in is reserved either way, so the setting changes nothing
        about the document's layout. */
    internal static bool? ShowLineNumbers
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("ShowLineNumbers") is int stored ? stored != 0 : null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value is null)
            {
                key.DeleteValue("ShowLineNumbers", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("ShowLineNumbers", value.Value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    /*  View > Wide scroll bars. Default ON: Fluent's idle bar is a hairline
        that reads as decoration, and a document reader wants the bar it can
        aim at without hovering first. Null means never set. */
    internal static bool? WideScrollBars
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("WideScrollBars") is int stored ? stored != 0 : null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value is null)
            {
                key.DeleteValue("WideScrollBars", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("WideScrollBars", value.Value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    /*  Options > Reopen last session. Default ON, Notepad's behaviour: the
        documents that were open come back, which matters most across an update
        restart nobody asked for. Null means never set. */
    internal static bool? RestoreSession
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(SessionKeyPath);
            return key?.GetValue("RestoreSession") is int stored ? stored != 0 : null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(SessionKeyPath);
            if (value is null)
            {
                key.DeleteValue("RestoreSession", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("RestoreSession", value.Value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    /// <summary>One entry per open tab in tab-strip order, or null when no session is stored.
    /// Entry format is owned by MainWindow; see SessionEntry.</summary>
    internal static string[]? Session
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(SessionKeyPath);
            return key?.GetValue("Session") as string[];
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(SessionKeyPath);
            if (value is null || value.Length == 0)
            {
                key.DeleteValue("Session", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("Session", value, RegistryValueKind.MultiString);
            }
        }
    }

    /// <summary>Which entry of <see cref="Session"/> was the active tab.</summary>
    internal static int? SessionActiveIndex
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(SessionKeyPath);
            return key?.GetValue("SessionActiveIndex") as int?;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(SessionKeyPath);
            if (value is null)
            {
                key.DeleteValue("SessionActiveIndex", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("SessionActiveIndex", value.Value, RegistryValueKind.DWord);
            }
        }
    }

    /// <summary>The chosen body font spec (e.g. "fonts:MarkLite#Roboto"), or null when never set.</summary>
    internal static string? BodyFontFamily
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("BodyFontFamily") as string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value is null)
            {
                key.DeleteValue("BodyFontFamily", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("BodyFontFamily", value);
            }
        }
    }
}
