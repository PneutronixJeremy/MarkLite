using Avalonia.Media;

namespace MarkLite;

/*  In-document search, as the window drives it: type a term, get a count and a
    current match, step through them.

    Two implementations while both renderers exist. DocumentSearch searches the
    rendered control tree, which is the whole document under the classic viewer.
    VirtualDocumentSearch searches the PARSED MODEL and highlights only the
    blocks that happen to be realized — the virtualizing viewer never renders
    more than the viewport, so a tree walk there would find a fraction of the
    document and report that fraction as the total. */
internal interface IDocumentSearch
{
    /// <summary>Matches in the document. Zero when no search is active.</summary>
    int Count { get; }

    /// <summary>Zero-based index of the current match; -1 when there are none.</summary>
    int CurrentOrdinal { get; }

    /// <summary>How many of those matches currently carry a highlight in the control tree. Equal
    /// to <see cref="Count"/> once the whole document is rendered; a smaller number under the
    /// virtualizing viewer, where only realized blocks can be highlighted.</summary>
    int HighlightedCount { get; }

    void Apply(string term, IBrush matchBrush, IBrush currentBrush, IBrush currentForeground,
        bool scrollToCurrent);

    /// <summary>Reverts all highlighting and forgets the search.</summary>
    void Clear();

    /// <summary>Forgets the search without undoing highlights — for a control tree that is about
    /// to be discarded or rebuilt.</summary>
    void Detach();

    void MoveNext();

    void MovePrevious();
}
