using System;
using System.Reflection;

namespace MarkLite;

/*  The app's own version, for display and for the debug log.

    Deliberately NOT UpdateService.CurrentVersion: that one comes from
    Velopack's UpdateManager and reads "0.0.0-dev" for any copy that is not
    installed, so the portable zip and every dev run would show a fake number.
    The assembly attribute is written from <Version> in the csproj by the SDK
    and is correct in installed, portable and dev builds alike. */
internal static class AppVersion
{
    /// <summary>Display version, e.g. "1.1.0" — the informational version with any "+gitHash" suffix cut.</summary>
    internal static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AppVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        /*  Fallback for a build where the attribute did not survive trimming:
            three parts, matching how <Version> is written. The log line is how
            a check tells the two apart — both render "1.1.0". */
        DebugLog.Write("version: informational attribute missing; using the assembly version");
        var version = assembly.GetName().Version;
        return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
