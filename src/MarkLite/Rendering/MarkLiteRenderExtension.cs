using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using Markdig.Extensions.TaskLists;
using Markdig.Syntax;

using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Blocks;

namespace MarkLite.Rendering;

/*  MarkLite's renderer swaps, registered per render pass through MarkView's
    public extension API (AvaloniaRenderer.ReplaceOrAdd):
    - ListRenderer → prominent task-list checkboxes (filled accent box + white
      check vs outlined empty box) instead of the stock small ☑/☐ glyph, whose
      checked/unchecked states share identical style classes and so cannot be
      differentiated by styling alone;
    - inline TaskListRenderer → silenced. The stock one re-emits the glyph
      unless the list renderer sets an INTERNAL SkipNextTaskList flag that a
      replacement cannot reach. Our list renderer always draws the box itself,
      and GFM only allows task markers at list-item starts, so nothing is lost;
    - CodeBlockRenderer → panel with fence-language label, horizontal scroll
      for long lines, ColorCode syntax highlighting, and per-block selection.
      Known tradeoff: the viewer's cross-block selection layer only registers
      code blocks shaped exactly Border>TextBlock, so these blocks fall out of
      whole-document drag selection — the SelectableTextBlock inside provides
      per-block selection and copy instead. */
internal sealed class MarkLiteRenderExtension : IMarkViewExtension
{
    public void Register(AvaloniaRenderer renderer)
    {
        renderer.ReplaceOrAdd<ListRenderer>(new ProminentListRenderer());
        renderer.ReplaceOrAdd<MarkView.Avalonia.Rendering.Inlines.TaskListRenderer>(
            new SilentTaskListRenderer());

        /*  The code renderer must sit AHEAD of MermaidBlockRenderer, which
            broadly accepts every FencedCodeBlock and renders non-mermaid
            fences itself (plain, no label/highlighting). Registration order in
            MainWindow puts Mermaid before this extension, so the front slot
            here lands ahead of it; mermaid fences are forwarded back to it
            from MarkLiteCodeBlockRenderer.Write. The stock CodeBlockRenderer
            is removed outright. MathExtension re-fronts its own narrow
            renderer regardless of this insert (its documented behavior). */
        var stock = renderer.ObjectRenderers.Find<CodeBlockRenderer>();
        if (stock is not null)
        {
            renderer.ObjectRenderers.Remove(stock);
        }
        renderer.ObjectRenderers.Insert(0, new MarkLiteCodeBlockRenderer());

        /*  Comments become visible (see HtmlCommentRenderer.cs); all other raw
            HTML keeps the stock behavior of rendering nothing. */
        renderer.ReplaceOrAdd<HtmlBlockRenderer>(new HtmlCommentBlockRenderer());
        renderer.ReplaceOrAdd<MarkView.Avalonia.Rendering.Inlines.HtmlInlineRenderer>(
            new HtmlCommentInlineRenderer());
    }
}

internal sealed class SilentTaskListRenderer : AvaloniaObjectRenderer<TaskList>
{
    protected override void Write(AvaloniaRenderer renderer, TaskList obj)
    {
    }
}

/*  Copy of the stock ListRenderer with one change: task items get a
    Border-based checkbox marker (deterministic look, deliberately NOT a themed
    CheckBox). Colors come from TaskBox* classes in MarkdownTheme.axaml, so
    theme switches restyle the boxes live. Keeps the stock class names
    (markdown-list + ordered/unordered/loose/tight) — the selection layer and
    the builtin list styles both key off them. */
internal sealed class ProminentListRenderer : AvaloniaObjectRenderer<ListBlock>
{
    protected override void Write(AvaloniaRenderer renderer, ListBlock obj)
    {
        var listPanel = new StackPanel { Spacing = obj.IsLoose ? 8 : 2 };
        listPanel.Classes.Add("markdown-list");
        listPanel.Classes.Add(obj.IsOrdered ? "markdown-list-ordered" : "markdown-list-unordered");
        listPanel.Classes.Add(obj.IsLoose ? "markdown-list-loose" : "markdown-list-tight");

        int index = 1;
        if (obj.IsOrdered && obj.OrderedStart is not null
            && int.TryParse(obj.OrderedStart, out int start))
        {
            index = start;
        }

        foreach (var item in obj)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            var itemGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };

            bool isTaskItem = listItem.Count > 0
                && listItem[0] is ParagraphBlock para
                && para.Inline?.FirstChild is TaskList;

            Control marker;
            if (isTaskItem)
            {
                var taskList = (TaskList)((ParagraphBlock)listItem[0]).Inline!.FirstChild!;
                marker = BuildCheckBox(taskList.Checked);
            }
            else
            {
                var markerTb = new TextBlock
                {
                    Text = obj.IsOrdered ? $"{index}." : "•",
                    Margin = new Avalonia.Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                markerTb.Classes.Add("markdown-list-marker");
                marker = markerTb;
            }
            Grid.SetColumn(marker, 0);
            itemGrid.Children.Add(marker);

            var contentPanel = new StackPanel { Spacing = 4 };
            Grid.SetColumn(contentPanel, 1);
            itemGrid.Children.Add(contentPanel);

            renderer.Push(contentPanel);
            renderer.WriteChildren(listItem);
            renderer.Pop();

            listPanel.Children.Add(itemGrid);
            if (obj.IsOrdered)
            {
                index++;
            }
        }

        renderer.WriteBlock(listPanel);
    }

    private static Border BuildCheckBox(bool isChecked)
    {
        var box = new Border
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Top,
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
            };
            glyph.Classes.Add("TaskCheckGlyph");
            box.Child = glyph;
        }
        return box;
    }
}

/*  Replaces the stock code-block renderer (bare Border>TextBlock, no language
    label, long lines clipped) with the MarkLite panel:
        Border.markdown-code-block > DockPanel
            [ TextBlock.CodeLangLabel (top, right-aligned)
              ScrollViewer (horizontal) > SelectableTextBlock (colored Runs) ]
    Highlighting uses ColorCode over the whole block at render time — full
    multi-line lexing state, unlike MarkView's per-line ICodeHighlighter slot.
    Mermaid fences never reach this renderer: the Mermaid extension package
    claims them first. */
internal sealed class MarkLiteCodeBlockRenderer : AvaloniaObjectRenderer<CodeBlock>
{
    protected override void Write(AvaloniaRenderer renderer, CodeBlock obj)
    {
        var language = obj is FencedCodeBlock fenced ? fenced.Info?.Trim() ?? string.Empty : string.Empty;

        /*  Mermaid fences belong to the Mermaid package's renderer, which sits
            behind this one in the renderer list (see Register above). */
        if (string.Equals(language, "mermaid", System.StringComparison.OrdinalIgnoreCase))
        {
            foreach (var other in renderer.ObjectRenderers)
            {
                if (other is MarkView.Avalonia.Mermaid.MermaidBlockRenderer mermaidRenderer)
                {
                    ((Markdig.Renderers.IMarkdownObjectRenderer)mermaidRenderer).Write(renderer, obj);
                    return;
                }
            }
            // Mermaid package absent: fall through and render as a plain code block.
        }

        var code = new System.Text.StringBuilder();
        var lines = obj.Lines.Lines;
        for (int i = 0; i < obj.Lines.Count; i++)
        {
            if (i > 0)
            {
                code.Append('\n');
            }
            code.Append(lines[i].Slice.ToString());
        }
        var codeText = code.ToString();

        var content = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
        };
        var colorCodeLanguage = language.Length > 0 ? CodeHighlighter.MapLanguage(language) : null;
        if (colorCodeLanguage is null)
        {
            content.Inlines!.Add(new Avalonia.Controls.Documents.Run(codeText));
        }
        else
        {
            content.Inlines!.AddRange(CodeHighlighter.Colorize(codeText, colorCodeLanguage));
        }

        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

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
        border.Classes.Add("markdown-code-block");
        if (language.Length > 0)
        {
            border.Classes.Add($"language-{language}");
        }

        renderer.WriteBlock(border);
    }
}
