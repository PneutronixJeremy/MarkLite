using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MarkLite.Rendering;

/*  Task-list items are ordinary list items to the markdown engine, so they get
    a bullet marker AND our checkbox. GFM renderers hide the bullet for task
    items; the engine offers no hook for that, so after a document renders this
    walks the visual tree, finds each checkbox, and hides the bullet in the
    same grid row of the containing list. */
internal static class TaskListMarkerHider
{
    internal static void Apply(Visual root)
    {
        foreach (var taskBox in root.GetVisualDescendants()
                                    .OfType<Border>()
                                    .Where(static b => b.Classes.Contains("TaskBox")))
        {
            HideMarkerFor(taskBox);
        }
    }

    private static void HideMarkerFor(Visual taskBox)
    {
        /*  Walk up to the nearest list grid, remembering which direct child of
            it contains the checkbox — its row is the list item's row. */
        Visual child = taskBox;
        for (var parent = child.GetVisualParent(); parent is not null; child = parent, parent = child.GetVisualParent())
        {
            if (parent is Grid grid && grid.Classes.Contains("List") && child is Control content)
            {
                var row = Grid.GetRow(content);
                foreach (var marker in grid.Children.Where(c =>
                             c.Classes.Contains("ListMarker") && Grid.GetRow(c) == row))
                {
                    marker.IsVisible = false;
                }
                return;
            }
        }
    }
}
