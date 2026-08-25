using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using MarkView.Avalonia;
using MarkView.Avalonia.Rendering;

namespace MarkLite.Rendering.Virtual;

/*  Everything about a document that can be known WITHOUT building controls:
    its top-level blocks, where each one sits in the source, its headings and
    anchors, and its table of contents.

    This is what makes virtualized rendering possible. The viewer realizes only
    the blocks near the viewport, so the table of contents, anchor targets and
    scroll estimates can no longer be read off a rendered tree — they have to
    come from the parsed document instead. Nothing here touches Avalonia. */
internal sealed partial class MarkdownDocumentModel
{
    /*  Copied from MarkView's MarkdownViewer, which applies it before parsing:
        the non-standard "![alt](url =WxH)" size hint is rewritten to a title
        string the image renderer understands, while backtick code spans (group
        1) pass through untouched so documented syntax examples are not
        mangled. Rendering a block through MarkView's renderers therefore
        requires MarkView's preprocessing — hence the copy. */
    [GeneratedRegex(@"(`+)[\s\S]*?\1|(\!\[[^\]]*\]\()([^\s\)]+)\s+=(\d+x\d+)(\))")]
    private static partial Regex ImageSizePreprocessorRegex();

    /// <summary>One top-level block, with where it came from in <see cref="Text"/>.</summary>
    /// <param name="Block">The Markdig block, ready to hand to a renderer.</param>
    /// <param name="Start">Index of the block's first character in <see cref="Text"/>.</param>
    /// <param name="End">Index of the block's last character (inclusive).</param>
    /// <param name="Hash">FNV-1a hash of the source slice; identifies the block across reloads.</param>
    internal readonly record struct BlockInfo(Block Block, int Start, int End, ulong Hash)
    {
        public int Length => End - Start + 1;
    }

    /// <summary>A heading, with the top-level block that has to be realized to show it.</summary>
    internal readonly record struct HeadingInfo(int Level, string Text, string Slug, int BlockIndex);

    private MarkdownDocumentModel(
        string text,
        MarkdownDocument document,
        List<BlockInfo> blocks,
        List<HeadingInfo> headings,
        Dictionary<string, int> anchors,
        IReadOnlyList<TocEntry> tocEntries)
    {
        Text = text;
        Document = document;
        Blocks = blocks;
        Headings = headings;
        Anchors = anchors;
        TocEntries = tocEntries;
    }

    /*  The PREPROCESSED source — the text every span here indexes into, and the
        text a copy operation must slice. It differs from the file only inside
        an image size hint, which never spans a line break, so line numbers are
        the file's own. */
    public string Text { get; }

    public MarkdownDocument Document { get; }

    /// <summary>Top-level blocks in document order. Index into this is a block's identity.</summary>
    public IReadOnlyList<BlockInfo> Blocks { get; }

    /// <summary>Every heading in the document (nested ones included), in document order.</summary>
    public IReadOnlyList<HeadingInfo> Headings { get; }

    /// <summary>Anchor slug to the index of the top-level block that contains its target.</summary>
    public IReadOnlyDictionary<string, int> Anchors { get; }

    /// <summary>The heading tree, built the same way MarkView builds its own.</summary>
    public IReadOnlyList<TocEntry> TocEntries { get; }

    public static MarkdownDocumentModel Parse(string markdown, MarkdownPipeline pipeline, int tocMaxDepth = 6)
    {
        var text = ImageSizePreprocessorRegex().Replace(markdown, static match =>
            match.Groups[1].Success
                ? match.Value
                : $"{match.Groups[2].Value}{match.Groups[3].Value} \"={match.Groups[4].Value}\"{match.Groups[5].Value}");

        var document = Markdig.Markdown.Parse(text, pipeline);

        var blocks = new List<BlockInfo>(document.Count);
        var headings = new List<HeadingInfo>();
        var anchors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var flatHeadings = new List<(int Level, string Text, string Slug)>();

        /*  One generator for the whole document, fed in document order, so the
            "-1"/"-2" suffixes on repeated headings come out identical to the
            ones MarkView's renderer produces. */
        var slugs = new SlugGenerator();
        var builder = new StringBuilder();

        for (var index = 0; index < document.Count; index++)
        {
            var block = document[index];
            var span = block.Span;
            var start = Math.Clamp(span.Start, 0, Math.Max(0, text.Length - 1));
            var end = Math.Clamp(span.End, start, Math.Max(0, text.Length - 1));
            blocks.Add(new BlockInfo(block, start, end, Hash(text, start, end)));

            CollectHeadings(block, index, slugs, builder, headings, flatHeadings, anchors);
            CollectAnchors(block, index, anchors);
        }

        return new MarkdownDocumentModel(
            text, document, blocks, headings, anchors,
            TocEntry.BuildTree(flatHeadings, tocMaxDepth));
    }

    /*  Headings nested in a quote or a list item are headings too: MarkView's
        renderer reaches them through WriteChildren and gives them slugs from
        the same generator, so the walk here has to visit them in exactly the
        same order — the whole subtree, parent before children. */
    private static void CollectHeadings(
        Block block,
        int blockIndex,
        SlugGenerator slugs,
        StringBuilder builder,
        List<HeadingInfo> headings,
        List<(int Level, string Text, string Slug)> flat,
        Dictionary<string, int> anchors)
    {
        if (block is HeadingBlock heading)
        {
            builder.Clear();
            ExtractText(heading.Inline, builder);
            var text = builder.ToString();
            var slug = slugs.GenerateSlug(text);
            headings.Add(new HeadingInfo(heading.Level, text, slug, blockIndex));
            flat.Add((heading.Level, text, slug));
            anchors[slug] = blockIndex;
            return;
        }

        if (block is ContainerBlock container)
        {
            foreach (var child in container)
            {
                CollectHeadings(child, blockIndex, slugs, builder, headings, flat, anchors);
            }
        }
    }

    /*  Anchors that are not headings: footnote definitions (MarkView registers
        them as "fn-<order>") and any explicit {#id} attribute. Both resolve to
        the top-level block a jump has to realize. */
    private static void CollectAnchors(Block block, int blockIndex, Dictionary<string, int> anchors)
    {
        if (block.TryGetAttributes() is { Id: { Length: > 0 } id })
        {
            anchors[id] = blockIndex;
        }

        switch (block)
        {
            case FootnoteGroup group:
                foreach (var child in group)
                {
                    if (child is Footnote footnote)
                    {
                        anchors[$"fn-{footnote.Order}"] = blockIndex;
                    }
                }
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    CollectAnchors(child, blockIndex, anchors);
                }
                break;
        }
    }

    /*  Same rule as MarkView's HeadingRenderer, and GitHub's: literal text and
        code-span content count, container inlines are descended into,
        everything else contributes nothing. The slug depends on it, so the two
        must not drift. */
    private static void ExtractText(Inline? inline, StringBuilder builder)
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
                case ContainerInline container:
                    ExtractText(container.FirstChild, builder);
                    break;
            }
            inline = inline.NextSibling;
        }
    }

    /*  FNV-1a over the block's source slice. Used to recognise a block across a
        re-parse: a live reload keeps the height and the realized control of
        every block whose text did not change, and the scroll anchor survives
        edits elsewhere in the file. Not a security hash — collisions cost a
        re-measure, nothing more. */
    private static ulong Hash(string text, int start, int end)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;
        for (var i = start; i <= end && i < text.Length; i++)
        {
            var c = text[i];
            hash = (hash ^ (byte)(c & 0xFF)) * prime;
            hash = (hash ^ (byte)(c >> 8)) * prime;
        }
        return hash;
    }
}
