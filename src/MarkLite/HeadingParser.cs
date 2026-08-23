using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkLite;

internal sealed record TocEntry(int Level, string Text, string Slug);

/*  Builds the heading list for the TOC from the RAW markdown text — the
    rendering engine exposes no document AST, and its HeaderScrolled event only
    reports headings relative to the viewport, not the full tree. ATX headings
    only (# .. ######); setext (===/---) headings are rare and unsupported.
    Fenced code blocks are skipped so comments like "# region" in code do not
    become headings. */
internal static partial class HeadingParser
{
    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^ {0,3}(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkOrImage();

    [GeneratedRegex(@"[`*_~]")]
    private static partial Regex InlineMarkers();

    [GeneratedRegex(@"[^a-z0-9 \-]")]
    private static partial Regex NonSlugChars();

    internal static List<TocEntry> Parse(string markdown)
    {
        var entries = new List<TocEntry>();
        var slugCounts = new Dictionary<string, int>();
        var inFence = false;
        string? fenceMarker = null;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            var fence = FenceLine().Match(line);
            if (fence.Success)
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = fence.Groups[1].Value[..1];
                }
                else if (fence.Groups[1].Value.StartsWith(fenceMarker!))
                {
                    inFence = false;
                }
                continue;
            }
            if (inFence)
            {
                continue;
            }

            var heading = AtxHeading().Match(line);
            if (!heading.Success)
            {
                continue;
            }

            var text = CleanInline(heading.Groups[2].Value);
            if (text.Length == 0)
            {
                continue;
            }

            entries.Add(new TocEntry(heading.Groups[1].Value.Length, text, MakeSlug(text, slugCounts)));
        }

        return entries;
    }

    private static string CleanInline(string text)
    {
        text = LinkOrImage().Replace(text, "$1");
        text = InlineMarkers().Replace(text, "");
        return text.Trim();
    }

    /// <summary>GitHub-style slug: lowercase, punctuation stripped, spaces to dashes, duplicates suffixed -1, -2, …</summary>
    private static string MakeSlug(string text, Dictionary<string, int> slugCounts)
    {
        var lowered = text.ToLowerInvariant();
        var stripped = NonSlugChars().Replace(lowered, "");
        var slug = new StringBuilder(stripped.Length);
        foreach (var ch in stripped)
        {
            slug.Append(ch == ' ' ? '-' : ch);
        }

        var baseSlug = slug.ToString();
        if (slugCounts.TryGetValue(baseSlug, out var seen))
        {
            slugCounts[baseSlug] = seen + 1;
            return $"{baseSlug}-{seen}";
        }
        slugCounts[baseSlug] = 1;
        return baseSlug;
    }
}
