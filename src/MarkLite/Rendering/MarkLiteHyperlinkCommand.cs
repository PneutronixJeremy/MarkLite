using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace MarkLite.Rendering;

/*  Link routing:
    - absolute http/https/mailto → default browser;
    - relative (or absolute) paths to markdown/text files, resolved against the
      open document's directory → opened inside MarkLite;
    - other paths that exist on disk → shell-opened with their default handler;
    - #anchors → scroll to the matching heading in the open document;
    - anything else → logged no-op rather than shell-executing arbitrary strings. */
internal sealed class MarkLiteHyperlinkCommand : ICommand
{
    private static readonly string[] MarkdownExtensions = [".md", ".markdown", ".txt"];

    private readonly Func<string?> _currentDocumentDirectory;
    private readonly Action<string> _openDocument;
    private readonly Action<string> _scrollToAnchor;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    internal MarkLiteHyperlinkCommand(
        Func<string?> currentDocumentDirectory,
        Action<string> openDocument,
        Action<string> scrollToAnchor)
    {
        _currentDocumentDirectory = currentDocumentDirectory;
        _openDocument = openDocument;
        _scrollToAnchor = scrollToAnchor;
    }

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
            return;
        }

        if (url.StartsWith('#'))
        {
            _scrollToAnchor(Uri.UnescapeDataString(url[1..]));
            return;
        }

        var resolved = Resolve(url);
        if (resolved is null)
        {
            DebugLog.Write($"link ignored (unresolvable): {url}");
        }
        else if (IsMarkdownFile(resolved) && File.Exists(resolved))
        {
            DebugLog.Write($"link opened in MarkLite: {resolved}");
            _openDocument(resolved);
        }
        else if (File.Exists(resolved))
        {
            Process.Start(new ProcessStartInfo(resolved) { UseShellExecute = true });
            DebugLog.Write($"link shell-opened: {resolved}");
        }
        else
        {
            DebugLog.Write($"link ignored (target not found): {resolved}");
        }
    }

    private string? Resolve(string url)
    {
        try
        {
            /*  Markdown links use URL syntax — forward slashes and percent
                escapes — while the target is a Windows path. */
            var path = Uri.UnescapeDataString(url).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            var baseDirectory = _currentDocumentDirectory();
            if (baseDirectory is null)
            {
                return null;
            }
            return Path.GetFullPath(Path.Combine(baseDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsMarkdownFile(string path)
    {
        var extension = Path.GetExtension(path);
        foreach (var known in MarkdownExtensions)
        {
            if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
