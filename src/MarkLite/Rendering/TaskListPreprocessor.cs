using System.Text.RegularExpressions;

namespace MarkLite.Rendering;

/*  The markdown engine has no GFM task-list support, so raw "[x]" / "[ ]"
    markers at the start of list items are rewritten into private-use-area
    sentinels (U+E000..U+E001) before parsing. A matching inline parser in
    MarkLitePlugin turns the sentinels into checkbox visuals. The sentinels
    cannot appear in normal documents, so prose like "array[x]" is unaffected. */
internal static partial class TaskListPreprocessor
{
    internal const string SentinelOpen = "\uE000";
    internal const string SentinelClose = "\uE001";

    [GeneratedRegex(@"^([ \t]*(?:[-*+]|\d+\.)[ \t]+)\[([ xX])\](?=[ \t])", RegexOptions.Multiline)]
    private static partial Regex TaskMarker();

    internal static string Apply(string markdown)
    {
        return TaskMarker().Replace(markdown, static m =>
        {
            var state = m.Groups[2].Value is " " ? ' ' : 'x';
            return $"{m.Groups[1].Value}{SentinelOpen}{state}{SentinelClose}";
        });
    }
}
