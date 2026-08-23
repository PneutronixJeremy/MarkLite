using Avalonia.Controls;
using Markdown.Avalonia;

namespace AotSpike;

public partial class MainWindow : Window
{
    /*  Hardcoded sample covering the risk areas for the AOT spike: heading,
        paragraph, fenced C# block, GFM table, nested list. If all five render
        correctly from a NativeAOT + trimmed publish, the stack is viable. */
    private const string SampleMarkdown = """
# AotSpike — NativeAOT Markdown render test

This paragraph proves basic prose flow. It contains **bold**, *italic*, and
`inline code` runs to exercise inline formatting.

## Fenced C# block

```csharp
public sealed class Greeter
{
    public string Greet(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Hello, stranger!";
        }
        return $"Hello, {name}!";
    }
}
```

## GFM table

| Viewer           | Working set (MB) | Web engine |
|------------------|-----------------:|------------|
| glow             |            20–40 | no         |
| Notepad          |              160 | yes        |
| Markdown Monster |              402 | yes        |

## Nested list

- Level one item
  - Level two item
    - Level three item
- Second level-one item
  1. Ordered child
  2. Another ordered child
""";

    public MainWindow()
    {
        InitializeComponent();
        Content = new MarkdownScrollViewer
        {
            Markdown = SampleMarkdown,
        };
    }
}
