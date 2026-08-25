using System;
using System.Collections.Generic;
using System.Text;

using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkLite.Rendering.Virtual;

/*  What a block SAYS, without building a single control — and where in the
    source file each character of that came from.

    Search needs the whole document, and the virtualizing viewer only ever
    renders the part of it near the viewport, so the text a search runs over has
    to come from the parsed blocks instead of from the screen. Copy needs the
    reverse trip: the reader selects rendered text and gets the MARKDOWN back,
    which means every character of the projection has to know its source offset.
    One walk produces both.

    The projection is the characters a reader would see if the block were on
    screen, in the order they would see them, and nothing else. "Nothing else"
    is defined by what the renderers actually draw:

    - list bullets and numbers, the code panel's language label and the task
      checkbox are chrome, not text (HighlightSession skips the same three);
    - raw HTML is dropped except comments, and those only while the View toggle
      is on — hence the flag, rather than a static read: the projection has to
      agree with the tree that is on screen right now;
    - front matter draws nothing, display maths draws a formula and a mermaid
      fence draws a diagram, so none of the three offers text to find;
    - an image is a picture: its alt text is markup a reader cannot see. A
      link's label is text they can.

    The text is cached per block, and thrown away when the comment flag flips.
    The source map is not: it is wanted once per copy, never per keystroke. */
internal sealed partial class MarkdownDocumentModel
{
    /// <summary>A stretch of a block's projection that came verbatim from the source, so an
    /// offset inside it maps to a source offset by simple arithmetic.</summary>
    /// <param name="TextStart">Offset in the block's projection.</param>
    /// <param name="Length">Characters, the same count in both.</param>
    /// <param name="SourceStart">Offset in <see cref="Text"/>.</param>
    internal readonly record struct TextRun(int TextStart, int Length, int SourceStart)
    {
        public int TextEnd => TextStart + Length;

        public int SourceEnd => SourceStart + Length;
    }

    /*  Collects the projection, and — when asked — the runs of it that are
        verbatim source. A run is only recorded when the projected characters and
        the source characters are the same characters in the same number: a
        decoded entity, a normalized line break or a code span that CommonMark
        stripped a space from is projected as text but cannot be indexed into,
        so it contributes characters and no run. An endpoint landing in one of
        those snaps outward to the nearest run instead of guessing. */
    private sealed class Projection
    {
        public readonly StringBuilder Builder = new();
        public readonly List<TextRun>? Runs;

        private readonly string _source;
        private readonly int _blockStart;
        private readonly int _blockEnd;
        private int _cursor;

        public Projection(bool withRuns, string source, int blockStart, int blockEnd)
        {
            Runs = withRuns ? [] : null;
            _source = source;
            _blockStart = blockStart;
            _blockEnd = blockEnd;
            _cursor = blockStart;
        }

        /// <summary>Appends text that is verbatim source, at the offset the parser claims.</summary>
        public void Append(ReadOnlySpan<char> text, int sourceStart)
        {
            if (Runs is not null && text.Length > 0)
            {
                var at = Verify(text, sourceStart);
                if (at >= 0)
                {
                    Runs.Add(new TextRun(Builder.Length, text.Length, at));
                    _cursor = at + text.Length;
                }
            }
            Builder.Append(text);
        }

        /*  A claimed source offset is CHECKED against the source before it is
            believed, and repaired when it is wrong.

            Markdig's inline spans are document-absolute almost everywhere — but
            not inside a table, where the cells are parsed out of a slice of the
            row and their inlines carry offsets relative to that slice. Taken at
            face value those offsets point at whatever happens to live near the
            top of the file, which is exactly the sort of bug that produces a
            plausible-looking copied slice from the wrong part of the document.

            So: if the source really says these characters at the claimed offset,
            the offset stands. If not, the same characters are looked for further
            on inside this block, starting from where the previous run ended —
            the projection is built in document order, so the first occurrence
            from there is this run's. If they are not in the block at all, the
            run is not recorded and the endpoint snapping takes over. */
        private int Verify(ReadOnlySpan<char> text, int claimed)
        {
            if (claimed >= 0 && claimed + text.Length <= _source.Length
                && _source.AsSpan(claimed, text.Length).SequenceEqual(text))
            {
                return claimed;
            }

            var from = Math.Clamp(_cursor, 0, _source.Length);
            var limit = Math.Clamp(_blockEnd + 1, from, _source.Length);
            var found = _source.AsSpan(from, limit - from).IndexOf(text);
            return found < 0 ? -1 : from + found;
        }

        /// <summary>Appends text a reader sees but that has no character-for-character
        /// counterpart in the source.</summary>
        public void AppendUnmapped(ReadOnlySpan<char> text) => Builder.Append(text);

        public void AppendUnmapped(char c) => Builder.Append(c);

        /// <summary>Starts a new line unless one has just started. Separates the leaves of a
        /// block, which are separate TextBlocks on screen with nothing between them.</summary>
        public void StartLine()
        {
            if (Builder.Length > 0 && Builder[^1] != '\n')
            {
                Builder.Append('\n');
            }
        }
    }

    private string?[] _blockText = [];
    private bool _blockTextComments;

    /// <summary>Plain-text projection of one top-level block — what a search over the whole
    /// document matches against, and what a selection offset counts in.</summary>
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

        var projection = new Projection(withRuns: false, Text, Blocks[index].Start, Blocks[index].End);
        AppendBlock(Blocks[index].Block, includeHtmlComments, projection);
        var text = projection.Builder.ToString();
        _blockText[index] = text;
        return text;
    }

    /// <summary>The same projection, with the runs of it that map back to the source.</summary>
    public (string Text, IReadOnlyList<TextRun> Runs) BlockSourceMap(int index, bool includeHtmlComments)
    {
        if (index < 0 || index >= Blocks.Count)
        {
            return (string.Empty, []);
        }
        var projection = new Projection(withRuns: true, Text, Blocks[index].Start, Blocks[index].End);
        AppendBlock(Blocks[index].Block, includeHtmlComments, projection);
        return (projection.Builder.ToString(), projection.Runs!);
    }

    /// <summary>Source offset in <see cref="Text"/> for an offset in a block's projection —
    /// what turns a selection the reader made on screen into a slice of markdown.</summary>
    /// <param name="atEnd">Which way to snap when the offset falls in projected text that has no
    /// character-for-character source (a decoded entity, a line break, the gap between two
    /// leaves): false rounds down to where the next verbatim run starts, true rounds up to where
    /// the previous one ended. A selection's start wants the former and its end the latter, so
    /// neither eats a character the reader did not select.</summary>
    public int SourceOffset(int blockIndex, int textOffset, bool atEnd, bool includeHtmlComments)
    {
        if (blockIndex < 0 || blockIndex >= Blocks.Count)
        {
            return 0;
        }

        var block = Blocks[blockIndex];
        var (_, runs) = BlockSourceMap(blockIndex, includeHtmlComments);
        if (runs.Count == 0)
        {
            //  A block that projects nothing verbatim (a diagram, front matter):
            //  its own extent is the most precise answer available.
            return atEnd ? block.End + 1 : block.Start;
        }

        /*  Inside a run: exact, character for character. The half-open end
            matters and it differs by endpoint. A start offset names the
            character AT it, so it belongs to the run that contains that
            character; an end offset names the position AFTER the last selected
            character, so an offset sitting exactly on a boundary belongs to the
            run that ENDS there — taking it as the start of the next run would
            drag the markup between them into the slice, which is how "inboard
            loop" came back as "inboard loop**". */
        foreach (var run in runs)
        {
            var inside = atEnd
                ? textOffset > run.TextStart && textOffset <= run.TextEnd
                : textOffset >= run.TextStart && textOffset < run.TextEnd;
            if (inside)
            {
                return run.SourceStart + (textOffset - run.TextStart);
            }
        }

        /*  Between runs, or past the last one. Snapping outward keeps the slice
            from including markup the reader never saw: a start moves forward to
            the next real character, an end moves back to the last one. */
        if (atEnd)
        {
            var best = block.Start;
            foreach (var run in runs)
            {
                if (run.TextEnd <= textOffset)
                {
                    best = Math.Max(best, run.SourceEnd);
                }
            }
            return best;
        }

        var start = block.End + 1;
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            if (runs[i].TextStart >= textOffset)
            {
                start = Math.Min(start, runs[i].SourceStart);
            }
        }
        return start;
    }

    /*  One leaf's worth of text per line. The separator matters: two paragraphs
        of a list item are two TextBlocks on screen with no character between
        them, so joining them without a break would invent matches — and
        selections — that span a gap the reader can see. */
    private static void AppendBlock(Block block, bool comments, Projection projection)
    {
        switch (block)
        {
            /*  Comments are the one kind of raw HTML MarkLite draws, and only
                while the View toggle is on — see HtmlCommentRenderer. */
            case HtmlBlock html:
                if (comments && HtmlComments.IsComment(html))
                {
                    projection.StartLine();
                    AppendMappedIfSameLength(
                        projection, HtmlComments.BlockText(html), html.Span.Start, html.Span.Length);
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
                projection.StartLine();
                AppendLines(code, projection);
                return;

            //  Definitions collected out of the flow; the renderer draws none.
            case LinkReferenceDefinitionGroup:
                return;

            case LeafBlock leaf:
                if (leaf.Inline is not null)
                {
                    projection.StartLine();
                    AppendInline(leaf.Inline, comments, projection);
                }
                return;

            /*  Lists, quotes, tables, footnote groups: every child is drawn in
                document order, so every child contributes in document order.
                Table cells arrive this way too — one line each, which also
                keeps a match from straddling a cell boundary. */
            case ContainerBlock container:
                foreach (var child in container)
                {
                    AppendBlock(child, comments, projection);
                }
                return;
        }
    }

    private static void AppendInline(Inline? inline, bool comments, Projection projection)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                /*  The slice IS the source: StringSlice.Start indexes into the
                    very text this model parsed, so literal text maps for free.
                    (Covers EmojiInline, which derives from LiteralInline.) */
                case LiteralInline literal:
                    projection.Append(literal.Content.AsSpan(), literal.Content.Start);
                    break;

                /*  "`code`" drops its delimiters, and CommonMark also strips one
                    space from each end when both are present — so the content is
                    only indexable when its length accounts for the whole span. */
                case CodeInline code:
                    AppendMappedIfSameLength(projection, code.Content,
                        code.Span.Start + code.DelimiterCount,
                        code.Span.Length - (2 * code.DelimiterCount));
                    break;

                //  "&amp;" is drawn as "&": four fewer characters, no mapping.
                case HtmlEntityInline entity:
                    projection.AppendUnmapped(entity.Transcoded.AsSpan());
                    break;

                //  "<https://…>" draws its own URL as the link label.
                case AutolinkInline autolink:
                    AppendMappedIfSameLength(projection, autolink.Url,
                        autolink.Span.Start + 1, autolink.Span.Length - 2);
                    break;

                //  Two spaces and a newline, or a backslash: not one character.
                case LineBreakInline:
                    projection.AppendUnmapped('\n');
                    break;

                case HtmlInline html:
                    if (comments && HtmlComments.IsComment(html))
                    {
                        AppendMappedIfSameLength(
                            projection, html.Tag, html.Span.Start, html.Span.Length);
                    }
                    break;

                /*  An image draws a picture, so its alt text is markup rather
                    than something on screen to find. Matched before
                    ContainerInline, which LinkInline derives from — an ordinary
                    link's LABEL is text and does contribute. */
                case LinkInline { IsImage: true }:
                    break;

                case ContainerInline container:
                    AppendInline(container.FirstChild, comments, projection);
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
        trailing newline — so a match offset here is a match offset there. Each
        line's slice carries its own source position, so code maps character for
        character even though the fence lines around it are not projected. */
    private static void AppendLines(CodeBlock code, Projection projection)
    {
        var lines = code.Lines.Lines;
        for (var i = 0; i < code.Lines.Count; i++)
        {
            if (i > 0)
            {
                projection.AppendUnmapped('\n');
            }
            var slice = lines[i].Slice;
            projection.Append(slice.AsSpan(), slice.Start);
        }
    }

    /*  Maps a rendered string onto a source span only when the two are the same
        length, which is the test for "these are the same characters". When they
        are not — a stripped space, an escaped character, trivia — the text is
        still projected, just not indexable, and an endpoint inside it snaps to
        the nearest run. */
    private static void AppendMappedIfSameLength(
        Projection projection, string text, int sourceStart, int sourceLength)
    {
        if (text.Length == sourceLength && sourceStart >= 0)
        {
            projection.Append(text.AsSpan(), sourceStart);
        }
        else
        {
            projection.AppendUnmapped(text.AsSpan());
        }
    }

    private static bool IsMermaidFence(FencedCodeBlock fence) =>
        string.Equals(fence.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);
}
