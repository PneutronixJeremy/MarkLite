using System;
using System.IO;
using System.Linq;

using MarkLite.Rendering;
using MarkLite.Rendering.Virtual;

using Xunit;

namespace MarkLite.Tests;

/*  Search under the virtualizing viewer counts what the DOCUMENT says, not what
    happens to be rendered — so the plain-text projection the count comes from
    has to describe exactly what the renderers draw. Every case here is a place
    where a projection that guessed instead would either invent matches nobody
    can see (list bullets, code language labels, image alt text, a mermaid
    fence's source) or miss ones that are on screen (decoded entities, code
    lines, footnote definitions, visible comments). */
public class ModelSearchTests
{
    private const bool WithComments = true;
    private const bool WithoutComments = false;

    private static MarkdownDocumentModel Parse(string markdown) =>
        MarkdownDocumentModel.Parse(markdown, MarkLitePipeline.Shared);

    /// <summary>The whole document's projection, blocks separated the way the debug dump
    /// separates them.</summary>
    private static string Projection(MarkdownDocumentModel model, bool comments = WithComments) =>
        string.Join("\n\n",
            Enumerable.Range(0, model.Blocks.Count).Select(i => model.BlockText(i, comments)));

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
    public void ProjectionDropsListMarkersAndTaskGlyphs()
    {
        var model = Parse("- alpha\n- beta\n\n1. one\n2. two\n\n- [x] done\n- [ ] pending\n");

        var text = Projection(model);

        Assert.Contains("alpha", text);
        Assert.Contains("done", text);
        //  Bullets and ordered-list numbers are drawn by the list renderer as
        //  its own chrome TextBlocks, and the checkbox is a Border.
        Assert.DoesNotContain("•", text);
        Assert.DoesNotContain("1.", text);
        Assert.DoesNotContain("[x]", text);
        Assert.DoesNotContain("☑", text);
    }

    [Fact]
    public void ProjectionKeepsCodeLinesButNotTheLanguageLabel()
    {
        var model = Parse("```csharp\nvar reactor = 1;\nvar station = 2;\n```\n");

        var text = model.BlockText(0, WithComments);

        //  Exactly the join MarkLiteCodeBlockRenderer does: the fence lines are
        //  not part of the block's text, and there is no trailing newline.
        Assert.Equal("var reactor = 1;\nvar station = 2;", text);
        Assert.DoesNotContain("csharp", text);
    }

    [Fact]
    public void ProjectionDecodesHtmlEntities()
    {
        var model = Parse("Vent &amp; purge, then &lt;hold&gt;.\n");

        Assert.Equal("Vent & purge, then <hold>.", model.BlockText(0, WithComments));
    }

    [Fact]
    public void ProjectionSkipsWhatTheRenderersDrawWithoutText()
    {
        const string markdown = """
            ---
            title: front matter
            ---

            $$
            Q_{net} = \alpha
            $$

            ```mermaid
            flowchart TD
                A[Caution raised] --> B[Isolate]
            ```

            Inline maths $\Delta v = g_0$ follows.
            """;
        var model = Parse(markdown);

        var text = Projection(model);

        //  Front matter is dropped, a maths block draws a formula, and a mermaid
        //  fence draws a diagram — none of the three is text on screen.
        Assert.DoesNotContain("front matter", text);
        Assert.DoesNotContain("Q_{net}", text);
        Assert.DoesNotContain("flowchart", text);
        Assert.DoesNotContain("Caution raised", text);
        //  The prose around inline maths is still text; the formula is not.
        Assert.Contains("Inline maths", text);
        Assert.Contains("follows.", text);
        Assert.DoesNotContain("g_0", text);
    }

    [Fact]
    public void ProjectionKeepsLinkLabelsAndDropsImageAltTextAndUrls()
    {
        var model = Parse("See [the station log](https://example.invalid/station) and ![a station](s.png).\n");

        var text = model.BlockText(0, WithComments);

        Assert.Contains("the station log", text);
        //  A link's destination is never drawn, and an image is a picture: its
        //  alt text is markup the reader cannot see.
        Assert.DoesNotContain("example.invalid", text);
        Assert.DoesNotContain("a station", text);
    }

    [Fact]
    public void ProjectionFollowsTheCommentToggle()
    {
        var model = Parse("<!-- reviewer: check the seal -->\n\nProse.\n");

        Assert.Contains("check the seal", Projection(model, WithComments));
        Assert.DoesNotContain("check the seal", Projection(model, WithoutComments));
        //  Flipping the flag has to invalidate the cache, not serve the old
        //  answer back.
        Assert.Contains("check the seal", Projection(model, WithComments));
    }

    [Fact]
    public void ProjectionSeparatesCellsAndListItems()
    {
        var model = Parse("| head | tail |\n|---|---|\n| alpha | beta |\n\n- one\n- two\n");

        //  Two cells and two list items are separate TextBlocks on screen with
        //  nothing between them, so a term must not be able to straddle them.
        var text = Projection(model);
        Assert.DoesNotContain("alphabeta", text);
        Assert.DoesNotContain("onetwo", text);
        Assert.Contains("alpha", text);
        Assert.Contains("two", text);
    }

    [Fact]
    public void MatchOffsetsIndexIntoTheBlockTheyName()
    {
        var model = Parse(Fixture("sample-plan.md"));
        var search = new ModelSearch(model, "phase", WithComments);

        Assert.True(search.Count > 1);
        foreach (var match in search.Matches)
        {
            var text = model.BlockText(match.Block, WithComments);
            Assert.Equal("phase",
                text.Substring(match.Start, match.Length), ignoreCase: true);
        }
    }

    [Fact]
    public void MatchesAreNumberedInDocumentOrderAndGroupedByBlock()
    {
        var model = Parse(Fixture("sample-plan.md"));
        var search = new ModelSearch(model, "phase", WithComments);

        var blocks = search.Matches.Select(static m => m.Block).ToArray();
        Assert.Equal(blocks.OrderBy(static b => b), blocks);

        foreach (var block in search.BlocksWithMatches)
        {
            Assert.True(search.TryGetBlockRange(block, out var first, out var count));
            Assert.Equal(count, blocks.Count(b => b == block));
            //  The block's ordinals are exactly [first, first + count).
            Assert.All(Enumerable.Range(first, count),
                ordinal => Assert.Equal(block, search.Matches[ordinal].Block));
        }
        Assert.False(search.TryGetBlockRange(-1, out _, out _));
    }

    [Fact]
    public void LineIndexPlacesAMatchInsideALongCodeFence()
    {
        var model = Parse("```text\nalpha\nbeta\ngamma\ndelta\n```\n");
        var search = new ModelSearch(model, "delta", WithComments);

        var match = Assert.Single(search.Matches);
        Assert.Equal(3, match.LineIndex);
        //  The fence lines themselves are not part of the block's text.
        Assert.Equal(4, match.LineCount);
    }

    [Fact]
    public void FootnoteDefinitionsAreSearchableWhereTheyAreDrawn()
    {
        var model = Parse("Body[^a].\n\n[^a]: The seal was reseated.\n");
        var search = new ModelSearch(model, "reseated", WithComments);

        var match = Assert.Single(search.Matches);
        //  Markdig gathers definitions into a group at the end of the document,
        //  and that is where the reader sees them.
        Assert.Equal(model.Blocks.Count - 1, match.Block);
    }

    /*  The pin: the classic renderer walks the rendered tree of the whole
        document and reports 373 matches for "station" on this fixture. The
        projection is meant to describe that tree, so it has to agree exactly —
        this is the one assertion that catches a projection rule drifting away
        from what the renderers draw. */
    [Fact]
    public void StressFixtureMatchesTheRenderedDocumentCount()
    {
        var model = Parse(Fixture("stress-large.md"));

        Assert.Equal(373, new ModelSearch(model, "station", WithComments).Count);
    }

    [Fact]
    public void EmptyTermFindsNothing()
    {
        var model = Parse("alpha beta\n");

        Assert.Equal(0, new ModelSearch(model, string.Empty, WithComments).Count);
    }
}
