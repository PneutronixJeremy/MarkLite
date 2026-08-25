using System.Collections.Generic;

namespace MarkLite.Rendering.Virtual;

/*  Where a term occurs in the DOCUMENT, as opposed to in whatever happens to be
    on screen.

    Every match is addressed the way the virtualizing viewer addresses
    everything else: by block index plus an offset inside that block's text
    projection. That makes a match navigable before it is rendered — the count
    is the document's, the current match can be scrolled to whether or not its
    block has controls yet, and stepping through matches never depends on what
    the panel has realized.

    No Avalonia here: the whole thing is testable against a fixture. */
internal sealed class ModelSearch
{
    /// <summary>One match.</summary>
    /// <param name="Block">Index into <see cref="MarkdownDocumentModel.Blocks"/>.</param>
    /// <param name="Start">Character offset in the block's text projection.</param>
    /// <param name="Length">Length of the match, in characters.</param>
    /// <param name="LineIndex">Lines of the projection before the match.</param>
    /// <param name="LineCount">Lines the projection has in total. With
    /// <paramref name="LineIndex"/> this places a match vertically inside a block that is
    /// hundreds of lines tall — a code fence, a long table — so the scroll lands near the
    /// match rather than at the top of the block.</param>
    internal readonly record struct Match(
        int Block, int Start, int Length, int LineIndex, int LineCount);

    private readonly List<Match> _matches = [];
    private readonly Dictionary<int, (int First, int Count)> _byBlock = [];

    public ModelSearch(MarkdownDocumentModel model, string term, bool includeHtmlComments)
    {
        if (term.Length == 0)
        {
            return;
        }

        for (var index = 0; index < model.Blocks.Count; index++)
        {
            var text = model.BlockText(index, includeHtmlComments);
            if (text.Length == 0)
            {
                continue;
            }

            var ranges = HighlightSession.FindRanges(text, term);
            if (ranges.Count == 0)
            {
                continue;
            }

            var lineCount = HighlightSession.CountNewlines(text, text.Length) + 1;
            _byBlock[index] = (_matches.Count, ranges.Count);
            foreach (var (start, end) in ranges)
            {
                _matches.Add(new Match(index, start, end - start,
                    HighlightSession.CountNewlines(text, start), lineCount));
            }
        }
    }

    /// <summary>Matches in document order. A match's index here is its ordinal — the number the
    /// find bar counts with and the highlighter keys its pieces by.</summary>
    public IReadOnlyList<Match> Matches => _matches;

    public int Count => _matches.Count;

    /// <summary>Blocks that hold at least one match, so a highlighter can visit only those.</summary>
    public IEnumerable<int> BlocksWithMatches => _byBlock.Keys;

    /// <summary>The ordinals belonging to one block: the first, and how many. False when the
    /// block has no matches.</summary>
    public bool TryGetBlockRange(int block, out int first, out int count)
    {
        if (_byBlock.TryGetValue(block, out var range))
        {
            (first, count) = range;
            return true;
        }
        (first, count) = (0, 0);
        return false;
    }
}
