using System;
using System.IO;
using System.Linq;

using MarkLite.Rendering;
using MarkLite.Rendering.Virtual;

using Xunit;

/*  Copy hands back the MARKDOWN, so every offset a reader selects on screen has
    to be turned into an offset in the file. That trip is what these tests pin:
    exact inside anything the renderers drew verbatim, and snapping outward —
    never guessing — everywhere else. An off-by-one here is a copied slice that
    starts mid-word or swallows a "**", which is the kind of thing nobody
    notices until they paste it somewhere that matters. */
namespace MarkLite.Tests;

public class SourceMappingTests
{
    private const bool WithComments = true;

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

    /// <summary>The source slice a selection from (block, from) to (block, to) would copy.</summary>
    private static string Slice(MarkdownDocumentModel model, int block, int from, int to)
    {
        var start = model.SourceOffset(block, from, atEnd: false, WithComments);
        var end = model.SourceOffset(block, to, atEnd: true, WithComments);
        return model.Text[start..Math.Max(start, end)];
    }

    [Fact]
    public void OffsetInAParagraphIsExact()
    {
        const string markdown = "# Title\n\nThe scrubber holds 12 K.\n";
        var model = Parse(markdown);

        //  Block 1 is the paragraph; "scrubber" starts at offset 4 of its text.
        Assert.Equal("The scrubber holds 12 K.", model.BlockText(1, WithComments));
        Assert.Equal(markdown.IndexOf("scrubber", StringComparison.Ordinal),
            model.SourceOffset(1, 4, atEnd: false, WithComments));
        Assert.Equal("scrubber", Slice(model, 1, 4, 12));
    }

    [Fact]
    public void AHeadingCopiesItsWordsAndNotItsHashes()
    {
        var model = Parse("## Coolant Limits\n\nBody.\n");

        Assert.Equal("Coolant Limits", model.BlockText(0, WithComments));
        //  The "## " is markup the reader never saw, so it is not theirs to copy.
        Assert.Equal("Coolant Limits", Slice(model, 0, 0, 14));
        Assert.Equal("Limits", Slice(model, 0, 8, 14));
    }

    [Fact]
    public void OffsetsAcrossEmphasisSkipTheMarkersInside()
    {
        const string markdown = "The **inboard loop** owns this.\n";
        var model = Parse(markdown);

        //  Projection: "The inboard loop owns this." — four characters shorter
        //  than the source, and the gap is in the middle.
        Assert.Equal("The inboard loop owns this.", model.BlockText(0, WithComments));
        Assert.Equal("inboard loop", Slice(model, 0, 4, 16));
        //  A selection spanning the emphasis keeps the markers, because
        //  everything BETWEEN the endpoints is the file verbatim.
        Assert.Equal("The **inboard loop** owns", Slice(model, 0, 0, 21));
    }

    [Fact]
    public void OffsetsInACodeFenceAreExactPerLine()
    {
        const string markdown = "```csharp\nvar a = 1;\nvar station = 2;\n```\n";
        var model = Parse(markdown);

        Assert.Equal("var a = 1;\nvar station = 2;", model.BlockText(0, WithComments));
        //  "station" is at offset 15 of the projection and its own place in the
        //  file; the fence lines are projected nowhere and mapped nowhere.
        Assert.Equal(markdown.IndexOf("station", StringComparison.Ordinal),
            model.SourceOffset(0, 15, atEnd: false, WithComments));
        Assert.Equal("station", Slice(model, 0, 15, 22));
        //  Across the line break: the source newline comes back with it.
        Assert.Equal("a = 1;\nvar", Slice(model, 0, 4, 14));
    }

    [Fact]
    public void ACodeSpanMapsThroughItsBackticks()
    {
        const string markdown = "Run `purge --loop` now.\n";
        var model = Parse(markdown);

        Assert.Equal("Run purge --loop now.", model.BlockText(0, WithComments));
        Assert.Equal(markdown.IndexOf("purge", StringComparison.Ordinal),
            model.SourceOffset(0, 4, atEnd: false, WithComments));
        Assert.Equal("purge --loop", Slice(model, 0, 4, 16));
    }

    [Fact]
    public void TableCellBoundariesSnapOutwardRatherThanGuessing()
    {
        const string markdown = "| head | tail |\n|---|---|\n| alpha | beta |\n";
        var model = Parse(markdown);

        //  Cells are one line each: "head\ntail\nalpha\nbeta".
        var text = model.BlockText(0, WithComments);
        Assert.Equal("head\ntail\nalpha\nbeta", text);

        /*  Offset 4 is the separator the projection put between two cells and
            the source never had. A start snaps forward to the next real
            character, an end back to the last one — so neither endpoint drags in
            the pipes and dashes between the cells. */
        Assert.Equal(markdown.IndexOf("tail", StringComparison.Ordinal),
            model.SourceOffset(0, 4, atEnd: false, WithComments));
        Assert.Equal(markdown.IndexOf("head", StringComparison.Ordinal) + 4,
            model.SourceOffset(0, 4, atEnd: true, WithComments));
        Assert.Equal("head", Slice(model, 0, 0, 4));
        //  A range spanning two cells keeps the markup between them, which is
        //  what makes the paste still a table.
        Assert.Equal("head | tail", Slice(model, 0, 0, 9));
    }

    [Fact]
    public void RepeatedTableCellsMapToTheirOwnOccurrence()
    {
        const string markdown = "| alpha | beta |\n|---|---|\n| beta | alpha |\n";
        var model = Parse(markdown);

        /*  Markdig gives a table cell's inlines offsets relative to the row
            slice, not to the document, so every cell has to be located rather
            than believed. A document with the same word in two cells is the case
            that catches a locator that just takes the first match: cell 4 is the
            SECOND "alpha", 27 characters further on than the first. */
        Assert.Equal("alpha\nbeta\nbeta\nalpha", model.BlockText(0, WithComments));
        Assert.Equal(markdown.IndexOf("alpha", StringComparison.Ordinal),
            model.SourceOffset(0, 0, atEnd: false, WithComments));
        Assert.Equal(markdown.LastIndexOf("alpha", StringComparison.Ordinal),
            model.SourceOffset(0, 16, atEnd: false, WithComments));
        Assert.Equal("alpha", Slice(model, 0, 16, 21));
    }

    [Fact]
    public void ADecodedEntityIsNotIndexedIntoButItsNeighboursAre()
    {
        const string markdown = "Vent &amp; purge.\n";
        var model = Parse(markdown);

        Assert.Equal("Vent & purge.", model.BlockText(0, WithComments));
        //  "purge" follows the entity and is still exact.
        Assert.Equal(markdown.IndexOf("purge", StringComparison.Ordinal),
            model.SourceOffset(0, 7, atEnd: false, WithComments));
        //  The whole line comes back as written, entity included.
        Assert.Equal("Vent &amp; purge.", Slice(model, 0, 0, 13));
    }

    [Fact]
    public void ABlockThatProjectsNothingFallsBackToItsOwnExtent()
    {
        const string markdown = "Before.\n\n```mermaid\nflowchart TD\n    A --> B\n```\n\nAfter.\n";
        var model = Parse(markdown);

        var fence = model.Blocks
            .Select(static (block, index) => (block, index))
            .First(static pair => pair.block.Block is Markdig.Syntax.FencedCodeBlock);

        //  A diagram has no text, so there is nothing to be precise about: the
        //  block's own extent is the honest answer.
        Assert.Equal(string.Empty, model.BlockText(fence.index, WithComments));
        Assert.Equal(model.Blocks[fence.index].Start,
            model.SourceOffset(fence.index, 0, atEnd: false, WithComments));
        Assert.Equal(model.Blocks[fence.index].End + 1,
            model.SourceOffset(fence.index, 0, atEnd: true, WithComments));
    }

    [Fact]
    public void EveryOffsetOfEveryBlockOfTheStressFixtureMapsBackToItsOwnCharacter()
    {
        var model = Parse(Fixture("stress-large.md"));

        /*  The property that matters, checked across a 500 KB document rather
            than on a handful of hand-written cases: wherever the projection
            claims a character came verbatim from the source, the source really
            does have that character there. A run recorded with the wrong offset
            would show up here as a mismatch, and nowhere else until someone
            pasted a mangled slice. */
        var checkedRuns = 0;
        for (var index = 0; index < model.Blocks.Count; index++)
        {
            var (text, runs) = model.BlockSourceMap(index, WithComments);
            foreach (var run in runs)
            {
                Assert.Equal(
                    text.Substring(run.TextStart, run.Length),
                    model.Text.Substring(run.SourceStart, run.Length));
                checkedRuns++;
            }
        }
        //  A fixture this size is thousands of runs; a handful would mean the
        //  walk stopped early and the assertion above proved nothing.
        Assert.True(checkedRuns > 5000, $"only {checkedRuns} runs checked");
    }
}
