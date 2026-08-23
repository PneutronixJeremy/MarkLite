using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;

namespace MarkLite.Rendering;

/*  Replaces the engine's builtin fenced-code-block renderer (which drops the
    fence language and renders plain text) with a panel that shows the language
    label and syntax-highlighted content. Unknown languages — including mermaid,
    which is deliberately not rendered as a diagram — fall back to plain styled
    text. */
internal sealed class CodeBlockOverride : BlockOverride2
{
    public CodeBlockOverride() : base("CodeBlocksWithLangEvaluator")
    {
    }

    public override IEnumerable<DocumentElement>? Convert2(
        string text,
        Match firstMatch,
        ParseStatus status,
        IMarkdownEngine2 engine,
        out int parseTextBegin,
        out int parseTextEnd)
    {
        /*  firstMatch groups (from the builtin parser's pattern):
            [1] = the opening fence run (``` or longer), [2] = the language tag. */
        var closePattern = new Regex($@"\n[ ]*{firstMatch.Groups[1].Value}[ ]*(\n|$)");
        var closeMatch = closePattern.Match(text, firstMatch.Index + firstMatch.Length);

        if (!closeMatch.Success)
        {
            parseTextBegin = parseTextEnd = -1;
            return null;
        }

        parseTextBegin = firstMatch.Index;
        parseTextEnd = closeMatch.Index + closeMatch.Length;

        var contentStart = firstMatch.Index + firstMatch.Length;
        var code = text[contentStart..closeMatch.Index];
        var language = firstMatch.Groups[2].Value.Trim();

        return [new UnBlockElement(Create(language, code))];
    }

    private static Control Create(string language, string code)
    {
        var content = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
        };
        content.Classes.Add("CodeBlockText");

        /*  Plain (unknown-language) code goes through Inlines as a single Run
            rather than the Text property, so in-document search can treat
            every code block uniformly: it extracts and splits Run inlines. */
        var colorCodeLanguage = language.Length > 0 ? CodeHighlighter.MapLanguage(language) : null;
        if (colorCodeLanguage is null)
        {
            content.Inlines!.Add(new Run(code));
        }
        else
        {
            content.Inlines!.AddRange(CodeHighlighter.Colorize(code, colorCodeLanguage));
        }

        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        scroll.Classes.Add("CodeBlock");

        var panel = new DockPanel();
        if (language.Length > 0)
        {
            var label = new TextBlock { Text = language.ToLowerInvariant() };
            label.Classes.Add("CodeLangLabel");
            DockPanel.SetDock(label, Dock.Top);
            panel.Children.Add(label);
        }
        panel.Children.Add(scroll);

        var border = new Border { Child = panel };
        border.Classes.Add("CodeBlock");
        return border;
    }
}
