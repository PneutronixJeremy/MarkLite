using Microsoft.Win32;

namespace MarkLite;

/*  Per-user settings, stored under HKCU\Software\MarkLite — the same state key
    FileAssociation uses, so the Velopack uninstall cleanup removes everything
    in one sweep. Registry over a settings file: no path/roaming questions, and
    the app already touches HKCU for the association feature. */
internal static class UserSettings
{
    private const string KeyPath = @"Software\MarkLite";

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
