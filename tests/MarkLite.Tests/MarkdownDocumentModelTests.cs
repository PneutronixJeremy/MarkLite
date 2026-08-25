using System;
using System.IO;
using System.Linq;

using Markdig;

using MarkLite.Rendering;
using MarkLite.Rendering.Virtual;

using MarkView.Avalonia;

using Xunit;

namespace MarkLite.Tests;

/*  The model is what the virtualized viewer navigates by: the block list is
    the scroll extent, the slugs are the anchor targets, the hashes are how a
    live reload recognises what did not change. Everything asserted here is
    something the viewer would otherwise get wrong silently. */
public class MarkdownDocumentModelTests
{
    /*  The app's own pipeline, not a rebuilt copy: a copy that drifted would
        parse a different number of blocks than the viewer renders. */
    private static MarkdownDocumentModel Parse(string markdown) =>
        MarkdownDocumentModel.Parse(markdown, MarkLitePipeline.Shared);

    private static string Fixture(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "testdata")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!, "testdata", name));
    }

    [Fact]
    public void TopLevelBlocksAreCountedInDocumentOrder()
    {
        var model = Parse("# One\n\npara\n\n- a\n- b\n\n## Two\n");

        Assert.Equal(4, model.Blocks.Count);
        Assert.Equal(["heading-block", "paragraph-block", "list-block", "heading-block"],
            model.Blocks.Select(b => Kind(b.Block)));
    }

    [Fact]
    public void BlockSpansSliceBackToTheirOwnSource()
    {
        const string markdown = "# Title\n\nFirst paragraph.\n\nSecond paragraph.\n";
        var model = Parse(markdown);

        var slices = model.Blocks
            .Select(b => model.Text.Substring(b.Start, b.Length))
            .ToArray();

        Assert.Equal("# Title", slices[0]);
        Assert.Equal("First paragraph.", slices[1]);
        Assert.Equal("Second paragraph.", slices[2]);
    }

    [Fact]
    public void SetextHeadingsAreIncluded()
    {
        var model = Parse("Title\n=====\n\nSub\n---\n\ntext\n");

        Assert.Equal(2, model.Headings.Count);
        Assert.Equal((1, "Title"), (model.Headings[0].Level, model.Headings[0].Text));
        Assert.Equal((2, "Sub"), (model.Headings[1].Level, model.Headings[1].Text));
    }

    [Fact]
    public void RepeatedHeadingsGetMarkViewsNumberedSlugs()
    {
        var model = Parse("# Setup\n\n# Setup\n\n# Setup\n");

        Assert.Equal(["setup", "setup-1", "setup-2"], model.Headings.Select(h => h.Slug));
    }

    [Fact]
    public void HeadingTextIgnoresEmphasisButKeepsCodeSpans()
    {
        var model = Parse("# A *bold* `Move`\n");

        Assert.Equal("A bold Move", model.Headings[0].Text);
        Assert.Equal("a-bold-move", model.Headings[0].Slug);
    }

    [Fact]
    public void NestedHeadingsResolveToTheirTopLevelBlock()
    {
        var model = Parse("para\n\n> # Quoted\n>\n> text\n\n# Plain\n");

        var quoted = model.Headings.Single(h => h.Text == "Quoted");
        var plain = model.Headings.Single(h => h.Text == "Plain");

        //  The quote is one top-level block; jumping to the nested heading has
        //  to realize that block, not the heading itself.
        Assert.Equal(1, quoted.BlockIndex);
        Assert.Equal(2, plain.BlockIndex);
    }

    [Fact]
    public void HeadingAnchorsResolveToTheirBlock()
    {
        var model = Parse("intro\n\n# Heading One\n\n## Heading Two\n");

        Assert.Equal(1, model.Anchors["heading-one"]);
        Assert.Equal(2, model.Anchors["heading-two"]);
    }

    /*  The app's pipeline is MarkView's UseSupportedExtensions plus maths, and
        that set includes NEITHER footnotes NOR generic {#id} attributes — so a
        footnote definition is just a paragraph and an {#id} is literal text.
        Asserted rather than assumed: if the pipeline ever gains them, this
        test fails and the anchor expectations get revisited on purpose. */
    [Fact]
    public void FootnoteAndIdAnchorsAreAbsentUnderTheAppsPipeline()
    {
        var model = Parse("Text with a note.[^n]\n\n## Marked {#custom-id}\n\n[^n]: The note.\n");

        Assert.False(model.Anchors.ContainsKey("fn-1"));
        Assert.False(model.Anchors.ContainsKey("custom-id"));
    }

    /*  The model's own footnote and {#id} handling, exercised against a
        pipeline that does enable them. Keeps that code honest even though the
        app does not switch the extensions on. */
    [Fact]
    public void FootnoteAndIdAnchorsResolveWhenTheExtensionsAreEnabled()
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseSupportedExtensions()
            .Use<Markdig.Extensions.Footnotes.FootnoteExtension>()
            .UseGenericAttributes()
            .Build();
        var model = MarkdownDocumentModel.Parse(
            "Text with a note.[^n]\n\n## Marked {#custom-id}\n\n[^n]: The note.\n", pipeline);

        Assert.True(model.Anchors.ContainsKey("custom-id"));
        //  Footnote definitions are gathered into one group block at the end.
        Assert.Equal(model.Blocks.Count - 1, model.Anchors["fn-1"]);
    }

    [Fact]
    public void TableOfContentsNestsByLevel()
    {
        var model = Parse("# A\n\n## A1\n\n### A11\n\n## A2\n\n# B\n");

        Assert.Equal(["A", "B"], model.TocEntries.Select(e => e.Text));
        Assert.Equal(["A1", "A2"], model.TocEntries[0].Children.Select(e => e.Text));
        Assert.Equal(["A11"], model.TocEntries[0].Children[0].Children.Select(e => e.Text));
    }

    [Fact]
    public void HashesAreStableAcrossParsesAndDistinguishContent()
    {
        const string markdown = "# Title\n\nAlpha.\n\nBeta.\n";
        var first = Parse(markdown);
        var second = Parse(markdown);

        Assert.Equal(first.Blocks.Select(b => b.Hash), second.Blocks.Select(b => b.Hash));
        Assert.NotEqual(first.Blocks[1].Hash, first.Blocks[2].Hash);
    }

    [Fact]
    public void EditingOneParagraphLeavesEveryOtherHashAlone()
    {
        var before = Parse("# Title\n\nAlpha.\n\nBeta.\n\nGamma.\n");
        var after = Parse("# Title\n\nAlpha CHANGED.\n\nBeta.\n\nGamma.\n");

        Assert.Equal(before.Blocks.Count, after.Blocks.Count);
        Assert.Equal(before.Blocks[0].Hash, after.Blocks[0].Hash);
        Assert.NotEqual(before.Blocks[1].Hash, after.Blocks[1].Hash);
        Assert.Equal(before.Blocks[2].Hash, after.Blocks[2].Hash);
        Assert.Equal(before.Blocks[3].Hash, after.Blocks[3].Hash);
    }

    [Fact]
    public void ImageSizeHintIsRewrittenButCodeSpansAreNot()
    {
        var model = Parse("![alt](pic.png =20x10)\n\n`![alt](pic.png =20x10)`\n");

        Assert.Contains("pic.png \"=20x10\"", model.Text);
        Assert.Contains("`![alt](pic.png =20x10)`", model.Text);
    }

    [Fact]
    public void StressFixtureParsesToAStableBlockCount()
    {
        var model = Parse(Fixture("stress-large.md"));

        /*  Markdig's own count, which is what the viewer virtualizes over. The
            generator script reports 2078 because it counts the blocks it
            EMITS; the parser merges some of them (the three footnote
            definitions are ordinary paragraphs under this pipeline, setext
            underlines belong to their heading). Pinned so a fixture or
            pipeline change cannot move it unnoticed. */
        Assert.Equal(2073, model.Blocks.Count);
        Assert.Equal(308, model.Headings.Count);

        //  Every block's slice must round-trip, or scrolling maths and the
        //  Phase 6 copy path would both be reading the wrong characters.
        foreach (var block in model.Blocks)
        {
            Assert.InRange(block.Start, 0, model.Text.Length - 1);
            Assert.InRange(block.End, block.Start, model.Text.Length - 1);
        }
    }

    [Fact]
    public void StressFixtureSlugsAreUniqueDespiteRepeatedHeadings()
    {
        var model = Parse(Fixture("stress-large.md"));

        var slugs = model.Headings.Select(h => h.Slug).ToArray();
        Assert.Equal(slugs.Length, slugs.Distinct().Count());

        //  The fixture deliberately repeats heading text; the numbered
        //  suffixes are what keeps the anchors apart.
        Assert.Contains(slugs, s => s.EndsWith("-1", StringComparison.Ordinal));
    }

    private static string Kind(Markdig.Syntax.Block block) => block switch
    {
        Markdig.Syntax.HeadingBlock => "heading-block",
        Markdig.Syntax.ParagraphBlock => "paragraph-block",
        Markdig.Syntax.ListBlock => "list-block",
        Markdig.Syntax.QuoteBlock => "quote-block",
        Markdig.Syntax.CodeBlock => "code-block",
        _ => block.GetType().Name,
    };
}
