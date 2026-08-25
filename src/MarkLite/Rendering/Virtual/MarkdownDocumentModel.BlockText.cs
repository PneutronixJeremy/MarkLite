using System;
using System.Text;

using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkLite.Rendering.Virtual;

/*  What a block SAYS, without building a single control.

    Search needs the whole document, and the virtualizing viewer only ever
    renders the part of it near the viewport — so the text a search runs over
    has to come from the parsed blocks instead of from the screen. This is that
    projection: the characters a reader would see if the block were on screen,
    in the order they would see them, and nothing else.

    "Nothing else" is the hard part, and it is defined by what the renderers
    actually draw:

    - list bullets and numbers, the code panel's language label and the task
      checkbox are chrome, not text (HighlightSession skips the same three);
    - raw HTML is dropped except comments, and those only while the View toggle
      is on — hence the flag, rather than a static read: the projection has to
      agree with the tree that is on screen right now;
    - front matter draws nothing, display maths draws a formula and a mermaid
      fence draws a diagram, so none of the three offers text to find;
    - an image is a picture: its alt text is markup a reader cannot see. A
      link's label is text they can.

    Cached per block, and thrown away when the comment flag flips. */
internal sealed partial class MarkdownDocumentModel
{
    private string?[] _blockText = [];
    private bool _blockTextComments;

    /// <summary>Plain-text projection of one top-level block — what a search over the whole
    /// document matches against.</summary>
    /// <param name="index">Index into <see cref="Blocks"/>.</param>
    /// <param name="includeHtmlComments">Whether HTML comments are being rendered, and so are
    /// part of the text on screen.</param>
    public string BlockText(int index, bool includeHtmlComments)
    {
        if (index < 0 || index >= Blocks.Count)
        {
            return string.Empty;
        }

        if (_blockText.Length != Blocks.Count || _blockTextComments != includeHtmlComments)
        {
            _blockText = new string?[Blocks.Count];
            _blockTextComments = includeHtmlComments;
        }

        if (_blockText[index] is { } cached)
        {
            return cached;
        }

        var builder = new StringBuilder();
        AppendBlock(Blocks[index].Block, includeHtmlComments, builder);
        var text = builder.ToString();
        _blockText[index] = text;
        return text;
    }

    /*  One leaf's worth of text per line. The separator matters: two paragraphs
        of a list item are two TextBlocks on screen with no character between
        them, so joining them without a break would invent matches that span a
        gap the reader can see. */
    private static void AppendBlock(Block block, bool comments, StringBuilder builder)
    {
        switch (block)
        {
            /*  Comments are the one kind of raw HTML MarkLite draws, and only
                while the View toggle is on — see HtmlCommentRenderer. */
            case HtmlBlock html:
                if (comments && HtmlComments.IsComment(html))
                {
                    StartLine(builder);
                    builder.Append(HtmlComments.BlockText(html));
                }
                return;

            /*  Both parse as code blocks and neither draws code: front matter
                draws nothing at all, a maths block draws a formula. Matched
                before CodeBlock because both derive from it. */
            case YamlFrontMatterBlock:
            case MathBlock:
                return;

            //  A mermaid fence is a diagram; its source is never on screen.
            case FencedCodeBlock fence when IsMermaidFence(fence):
                return;

            case CodeBlock code:
                StartLine(builder);
                AppendLines(code, builder);
                return;

            //  Definitions collected out of the flow; the renderer draws none.
            case LinkReferenceDefinitionGroup:
                return;

            case LeafBlock leaf:
                if (leaf.Inline is not null)
                {
                    StartLine(builder);
                    AppendInline(leaf.Inline, comments, builder);
                }
                return;

            /*  Lists, quotes, tables, footnote groups: every child is drawn in
                document order, so every child contributes in document order.
                Table cells arrive this way too — one line each, which also
                keeps a match from straddling a cell boundary. */
            case ContainerBlock container:
                foreach (var child in container)
                {
                    AppendBlock(child, comments, builder);
                }
                return;
        }
    }

    private static void AppendInline(Inline? inline, bool comments, StringBuilder builder)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.AsSpan());
                    break;

                case CodeInline code:
                    builder.Append(code.Content);
                    break;

                //  "&amp;" is drawn as "&", so that is what a search sees.
                case HtmlEntityInline entity:
                    builder.Append(entity.Transcoded.AsSpan());
                    break;

                //  "<https://…>" draws its own URL as the link label.
                case AutolinkInline autolink:
                    builder.Append(autolink.Url);
                    break;

                case LineBreakInline:
                    builder.Append('\n');
                    break;

                case HtmlInline html:
                    if (comments && HtmlComments.IsComment(html))
                    {
                        builder.Append(html.Tag);
                    }
                    break;

                /*  An image draws a picture, so its alt text is markup rather
                    than something on screen to find. Matched before
                    ContainerInline, which LinkInline derives from — an ordinary
                    link's LABEL is text and does contribute. */
                case LinkInline { IsImage: true }:
                    break;

                case ContainerInline container:
                    AppendInline(container.FirstChild, comments, builder);
                    break;

                /*  Everything left draws a glyph, a number or a formula rather
                    than text: the task-list marker (MarkLite draws the box
                    itself), a footnote reference's superscript, inline maths. */
                default:
                    break;
            }
            inline = inline.NextSibling;
        }
    }

    /*  The code block's own lines, joined exactly the way
        MarkLiteCodeBlockRenderer joins them — same loop, same separator, no
        trailing newline — so a match offset here is a match offset there. */
    private static void AppendLines(CodeBlock code, StringBuilder builder)
    {
        var lines = code.Lines.Lines;
        for (var i = 0; i < code.Lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }
            builder.Append(lines[i].Slice.AsSpan());
        }
    }

    private static bool IsMermaidFence(FencedCodeBlock fence) =>
        string.Equals(fence.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

    private static void StartLine(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }
    }
}
