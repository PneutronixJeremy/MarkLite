using System;
using System.Diagnostics;
using System.Windows.Input;

namespace MarkLite.Rendering;

/*  Absolute web/mail links open in the default browser. Relative paths and
    #anchors need in-app navigation (document switching, scroll-to-heading)
    that does not exist yet, so they are logged no-ops rather than being
    shell-executed as arbitrary strings. */
internal sealed class MarkLiteHyperlinkCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter)
    {
        return parameter is string;
    }

    public void Execute(object? parameter)
    {
        if (parameter is not string url || url.Length == 0)
        {
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto")
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            DebugLog.Write($"link opened externally: {url}");
        }
        else
        {
            DebugLog.Write($"link ignored (no in-app navigation yet): {url}");
        }
    }
}
