using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using ColorCode;
using ColorCode.Common;
using ColorCode.Parsing;
using ColorCode.Styling;

namespace MarkLite.Rendering;

/*  Turns fenced-code text into colored Avalonia Runs using ColorCode's
    regex-based language parsers. One instance per colorize call — the
    expensive part (compiled language grammars) is cached statically inside
    ColorCode itself. */
internal sealed class CodeHighlighter : CodeColorizerBase
{
    private readonly List<Run> _runs = [];
    private readonly Dictionary<string, IBrush?> _brushCache = [];

    private CodeHighlighter(StyleDictionary styles)
        : base(styles, languageParser: null)
    {
    }

    /// <summary>Maps a fence language tag to a ColorCode language; null means render plain.</summary>
    internal static ILanguage? MapLanguage(string fenceLanguage)
    {
        return fenceLanguage.Trim().ToLowerInvariant() switch
        {
            "cs" or "csharp" or "c#" => Languages.CSharp,
            /*  ColorCode has no JSON grammar; the JavaScript one colors
                strings, numbers and literals well enough for JSON. */
            "js" or "javascript" or "json" => Languages.JavaScript,
            "ts" or "typescript" => Languages.Typescript,
            "powershell" or "posh" or "ps1" or "pwsh" => Languages.PowerShell,
            "xml" or "xaml" or "axaml" or "csproj" or "props" or "targets" => Languages.Xml,
            "html" or "htm" => Languages.Html,
            "css" => Languages.Css,
            "sql" => Languages.Sql,
            "cpp" or "c" => Languages.Cpp,
            "java" => Languages.Java,
            "php" => Languages.Php,
            "python" or "py" => Languages.Python,
            "fsharp" or "fs" => Languages.FSharp,
            "vb" or "vbnet" => Languages.VbDotNet,
            "md" or "markdown" => Languages.Markdown,
            _ => null,
        };
    }

    internal static IReadOnlyList<Run> Colorize(string code, ILanguage language)
    {
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var styles = dark ? StyleDictionary.DefaultDark : StyleDictionary.DefaultLight;
        if (!dark && styles.Contains(ScopeName.PowerShellCommand))
        {
            // ColorCode's light dictionary keeps the dark theme's yellow here — unreadable on a light panel.
            styles[ScopeName.PowerShellCommand].Foreground = "#795E26";
        }
        var highlighter = new CodeHighlighter(styles);
        highlighter.languageParser.Parse(code, language,
            (parsed, scopes) => highlighter.Write(parsed, scopes));
        return highlighter._runs;
    }

    protected override void Write(string parsedSourceCode, IList<Scope> scopes)
    {
        /*  Scopes form a tree over this chunk of source; children refine their
            parents. Painting parents first and children after leaves the
            innermost scope name per character, then consecutive characters
            with the same scope collapse into one Run. */
        var scopePerChar = new string?[parsedSourceCode.Length];
        foreach (var scope in scopes)
        {
            PaintScope(scope, scopePerChar);
        }

        var start = 0;
        for (var i = 1; i <= parsedSourceCode.Length; ++i)
        {
            if (i == parsedSourceCode.Length || scopePerChar[i] != scopePerChar[start])
            {
                var run = new Run(parsedSourceCode.Substring(start, i - start));
                var brush = BrushFor(scopePerChar[start]);
                if (brush is not null)
                {
                    run.Foreground = brush;
                }
                _runs.Add(run);
                start = i;
            }
        }
    }

    private static void PaintScope(Scope scope, string?[] scopePerChar)
    {
        var end = System.Math.Min(scope.Index + scope.Length, scopePerChar.Length);
        for (var i = scope.Index; i < end; ++i)
        {
            scopePerChar[i] = scope.Name;
        }

        foreach (var child in scope.Children)
        {
            PaintScope(child, scopePerChar);
        }
    }

    private IBrush? BrushFor(string? scopeName)
    {
        if (scopeName is null)
        {
            return null;
        }

        if (_brushCache.TryGetValue(scopeName, out var cached))
        {
            return cached;
        }

        IBrush? brush = null;
        if (Styles.Contains(scopeName) && Styles[scopeName].Foreground is { Length: > 0 } hex)
        {
            brush = BrushFromHex(hex, scopeName);
        }

        _brushCache[scopeName] = brush;
        return brush;
    }

    /*  ColorCode's own style dictionaries carry malformed literals: the dark
        theme's "XML Name" reads "#FF#E6E6E6" and "SQL System Function" has no
        leading '#' at all. Color.Parse throws FormatException on both, and an
        unhandled throw inside a render pass takes the whole process down — an
        xml fence in a dark-theme document was enough to kill the app.
        Normalize to a single leading '#', and leave the scope unstyled if the
        value still is not a color. */
    internal static IBrush? BrushFromHex(string hex, string scopeName)
    {
        var digits = hex.Replace("#", string.Empty).Trim();
        if (digits.Length is not (6 or 8) || !Color.TryParse($"#{digits}", out var color))
        {
            DebugLog.Write($"code highlight: unusable color '{hex}' for scope '{scopeName}'");
            return null;
        }

        return new SolidColorBrush(color);
    }
}
