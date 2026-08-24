using Avalonia.Media;
using MarkView.Avalonia;

namespace MarkLite;

/*  TEMPORARY STUB during the Markdig-stack migration. The whistyun-era
    implementation (run-splitting over CTextBlock/CInline trees) died with that
    engine; the MarkView port — run-splitting over standard Avalonia Inlines,
    match enumeration over the viewer's DocumentSelectionLayer text index —
    lands in migration phase 10C. Until then the find bar reports 0 results.
    The old implementation is recoverable from git history at tag/commit
    "Phase 7" for reference. */
internal sealed class DocumentSearch
{
    public DocumentSearch(MarkdownViewer viewer)
    {
        _ = viewer;
    }

    public int Count => 0;

    public int CurrentOrdinal => -1;

    public void Apply(string term, IBrush matchBrush, IBrush currentBrush, IBrush currentForeground, bool scrollToCurrent)
    {
        _ = term;
        _ = matchBrush;
        _ = currentBrush;
        _ = currentForeground;
        _ = scrollToCurrent;
        if (term.Length > 0)
        {
            DebugLog.Write("search inactive: pending migration phase 10C");
        }
    }

    public void MoveNext()
    {
    }

    public void MovePrevious()
    {
    }

    public void Clear()
    {
    }

    public void Detach()
    {
    }
}
