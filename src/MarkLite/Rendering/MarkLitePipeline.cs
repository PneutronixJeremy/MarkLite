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
    public static MarkdownPipeline Shared { get; } =
        new MarkdownPipelineBuilder().UseSupportedExtensions().UseMathematics().Build();
}
