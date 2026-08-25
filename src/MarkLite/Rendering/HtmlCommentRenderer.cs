using System;
using System.Text;

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using MarkView.Avalonia.Rendering;

namespace MarkLite.Rendering;

/*  Markdown comments are invisible in every renderer MarkLite is built on:
    Markdig parses them as HtmlBlock/HtmlInline and MarkView renders both to
    nothing. That is fine for markup a document uses for layout, but a comment
    is content the author wrote and a reader may need to see — a review marker,
    a note to a future editor, a directive a tool acts on.

    So comments render as dimmed monospace showing the markup exactly as
    written, under a View toggle. Everything else that arrives as raw HTML keeps
    the old behavior and is dropped: a document that opens with an <img> tag
    must not turn that tag into visible text.  */
internal static class HtmlComments
{
    /*  Read during a render pass, set before one. Static rather than per-viewer
        because a render pass is synchronous and single-threaded, and every open
        document shares the one View setting. */
    internal static bool Visible { get; set; } = true;

    internal const string StyleClass = "markdown-html-comment";

    /// <summary>True for the HTML blocks that are comments rather than markup.</summary>
    internal static bool IsComment(HtmlBlock block)
    {
        return block.Type == HtmlBlockType.Comment;
    }

    /// <summary>True for inline HTML that is a comment: "&lt;!-- … --&gt;".</summary>
    internal static bool IsComment(HtmlInline inline)
    {
        var tag = inline.Tag;
        return tag.StartsWith("<!--", StringComparison.Ordinal);
    }

    /*  Markdig hands inline comments over in pieces — the opening "<!--", the
        text, and the closing "-->" can arrive as separate HtmlInline nodes —
        so each piece renders on its own and the line reads as the original
        markup once they sit next to each other. */
    internal static string BlockText(HtmlBlock block)
    {
        var text = new StringBuilder();
        var lines = block.Lines.Lines;
        for (var i = 0; i < block.Lines.Count; i++)
        {
            if (i > 0)
            {
                text.Append('\n');
            }
            text.Append(lines[i].Slice.ToString());
        }
        return text.ToString().TrimEnd();
    }
}

/*  Replaces MarkView's HtmlBlockRenderer, which writes nothing at all. Comment
    blocks become a dimmed monospace TextBlock; every other HTML block is still
    dropped, so this only ever ADDS what was previously invisible. */
internal sealed class HtmlCommentBlockRenderer : AvaloniaObjectRenderer<HtmlBlock>
{
    protected override void Write(AvaloniaRenderer renderer, HtmlBlock obj)
    {
        if (!HtmlComments.Visible || !HtmlComments.IsComment(obj))
        {
            return;
        }

        var text = HtmlComments.BlockText(obj);
        if (text.Length == 0)
        {
            return;
        }

        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        };
        block.Classes.Add(HtmlComments.StyleClass);
        renderer.WriteBlock(block);
    }
}

/*  The inline counterpart, for comments that sit at the end of a line of prose
    or inside a list item — which is where a marker aimed at a tool usually
    lives. Rendered in place as a Run so the surrounding paragraph keeps its
    flow and wrapping. */
internal sealed class HtmlCommentInlineRenderer : AvaloniaObjectRenderer<HtmlInline>
{
    protected override void Write(AvaloniaRenderer renderer, HtmlInline obj)
    {
        if (!HtmlComments.Visible || !HtmlComments.IsComment(obj))
        {
            return;
        }

        var run = new Run(obj.Tag);
        run.Classes.Add(HtmlComments.StyleClass);
        renderer.WriteInline(run);
    }
}
