using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Markdig.Extensions.TaskLists;
using Markdig.Syntax;

using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Blocks;

namespace MarkdigSpike;

/*  Gate 2 proof: replaces the stock ListRenderer with a copy whose task-list
    marker is a prominent Border-based checkbox (filled accent box + white check
    for checked, outlined empty box for unchecked) instead of a small ☑/☐ text
    glyph — the same deliberate look MarkLite's Phase 2 built by hand against
    the whistyun engine, delivered here through MarkView's public extension
    API (IMarkViewExtension + ReplaceOrAdd). */
public sealed class ProminentTaskListExtension : IMarkViewExtension
{
    public void Register(AvaloniaRenderer renderer)
    {
        renderer.ReplaceOrAdd<ListRenderer>(new ProminentListRenderer());
        /*  The stock inline TaskListRenderer emits a ☑/☐ glyph unless the list
            renderer sets the internal SkipNextTaskList flag — which an external
            replacement cannot reach. Our list renderer always draws the box
            itself, so the inline renderer is silenced entirely (TaskList
            inlines are only valid at list-item starts in GFM anyway). */
        renderer.ReplaceOrAdd<MarkView.Avalonia.Rendering.Inlines.TaskListRenderer>(
            new SilentTaskListRenderer());
    }
}

public sealed class SilentTaskListRenderer : AvaloniaObjectRenderer<TaskList>
{
    protected override void Write(AvaloniaRenderer renderer, TaskList obj)
    {
    }
}

public sealed class ProminentListRenderer : AvaloniaObjectRenderer<ListBlock>
{
    protected override void Write(AvaloniaRenderer renderer, ListBlock obj)
    {
        var listPanel = new StackPanel { Spacing = 4 };
        listPanel.Classes.Add(obj.IsOrdered ? "markdown-list-ordered" : "markdown-list-unordered");

        int index = 1;
        if (obj.IsOrdered && int.TryParse(obj.OrderedStart, out int start))
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
                    Margin = new Thickness(0, 0, 8, 0),
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

    /*  Deterministic look, deliberately NOT a themed CheckBox control: filled
        accent box with white check vs empty outlined box (MarkLite Phase 2
        prominence spec). */
    private static Border BuildCheckBox(bool isChecked)
    {
        var box = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 1, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        box.Classes.Add(isChecked ? "task-box-checked" : "task-box-unchecked");

        if (isChecked)
        {
            box.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            box.Child = new TextBlock
            {
                Text = "✓",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
            box.BorderThickness = new Thickness(2);
        }
        return box;
    }
}
