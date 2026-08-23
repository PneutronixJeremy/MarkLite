using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;

namespace MarkLite.Rendering;

/*  MarkLite's rendering extensions for the markdown engine:
    - replaces the fenced-code-block renderer with a syntax-highlighted panel
      that shows the fence language;
    - renders GFM task-list markers (pre-tokenized by TaskListPreprocessor)
      as prominent read-only checkboxes. */
internal sealed partial class MarkLitePlugin : IMdAvPlugin
{
    [GeneratedRegex($"{TaskListPreprocessor.SentinelOpen}([ x]){TaskListPreprocessor.SentinelClose}")]
    private static partial Regex TaskSentinel();

    public void Setup(SetupInfo info)
    {
        info.Register(new CodeBlockOverride());
        info.Register(InlineParser.New(TaskSentinel(), "TaskListCheckBox", CreateTaskBoxInline));
    }

    private static CInline CreateTaskBoxInline(Match match)
    {
        return new CInlineUIContainer(CreateTaskBox(match.Groups[1].Value == "x"));
    }

    private static Control CreateTaskBox(bool isChecked)
    {
        var box = new Border
        {
            Width = 17,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Classes.Add("TaskBox");
        box.Classes.Add(isChecked ? "TaskBoxChecked" : "TaskBoxUnchecked");

        if (isChecked)
        {
            var glyph = new TextBlock
            {
                Text = "✓",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            glyph.Classes.Add("TaskCheckGlyph");
            box.Child = glyph;
        }

        return box;
    }
}
