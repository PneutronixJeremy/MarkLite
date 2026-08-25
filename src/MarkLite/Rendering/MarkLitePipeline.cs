using Markdig;

using MarkView.Avalonia;

namespace MarkLite.Rendering;

/*  The one Markdig pipeline the app parses with. It lives here rather than on
    the window because the viewer, the document model and the tests all have to
    agree on it — a second pipeline built "the same way" somewhere else would
    parse a different number of blocks and nothing would say so out loud.

    Pipelines are immutable once built and safe to share. */
internal static class MarkLitePipeline
{
    /*  Footnotes are Markdig's FootnoteExtension, spelled out rather than
        through an extension method: both Markdig and MarkView publish a
        UseFootnotes, they are not the same call, and the ambiguity is
        resolved here once instead of at every call site. MarkView's renderers
        for FootnoteGroup and FootnoteLink are registered unconditionally, so
        enabling the parser side is all that is needed.

        Consequence worth knowing: definitions leave their place in the flow
        and collect into a separator plus a group at the END of the document,
        and "[^n]" becomes a superscript link instead of literal text. */
    public static MarkdownPipeline Shared { get; } =
        new MarkdownPipelineBuilder()
            .UseSupportedExtensions()
            .UseMathematics()
            .Use<Markdig.Extensions.Footnotes.FootnoteExtension>()
            .Build();
}
