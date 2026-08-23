using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace MarkLite.Rendering;

/*  Full replacement for Markdown.Avalonia's builtin style set — assigned to
    MarkdownScrollViewer.MarkdownStyle, so it must cover every element class
    the engine emits (nothing else styles them). */
public partial class MarkdownTheme : Styles
{
    public MarkdownTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
