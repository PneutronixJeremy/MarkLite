using System.Linq;

using Avalonia.Media;

using ColorCode.Styling;

using MarkLite.Rendering;

using Xunit;

namespace MarkLite.Tests;

/*  ColorCode ships malformed color literals in its own default dictionaries,
    and a FormatException raised while a code block renders is an unhandled
    throw on the UI thread: the app dies. These tests pin the normalization
    that keeps a bad literal cosmetic, and sweep both dictionaries so a package
    upgrade that adds another one fails here instead of on a user's screen. */
public class CodeHighlighterTests
{
    [Theory]
    // "XML Name" in ColorCode's dark dictionary — a doubled '#'. Killed the app on any xml fence.
    [InlineData("#FF#E6E6E6", 0xFF, 0xE6, 0xE6, 0xE6)]
    // "SQL System Function" — no leading '#' at all.
    [InlineData("FFFF00FF", 0xFF, 0xFF, 0x00, 0xFF)]
    // The well-formed majority, both with and without an alpha channel.
    [InlineData("#FF569CD6", 0xFF, 0x56, 0x9C, 0xD6)]
    [InlineData("#795E26", 0xFF, 0x79, 0x5E, 0x26)]
    public void MalformedHexStillYieldsTheIntendedColor(string hex, byte a, byte r, byte g, byte b)
    {
        var brush = Assert.IsType<SolidColorBrush>(CodeHighlighter.BrushFromHex(hex, "test"));

        Assert.Equal(Color.FromArgb(a, r, g, b), brush.Color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cornflowerblue")]
    [InlineData("#FFF")]
    [InlineData("#FF569CD6FF")]
    public void UnusableHexRendersUnstyledRatherThanThrowing(string hex)
    {
        Assert.Null(CodeHighlighter.BrushFromHex(hex, "test"));
    }

    [Fact]
    public void EveryColorCodeStyleColorSurvivesNormalization()
    {
        var styles = StyleDictionary.DefaultDark
            .Concat(StyleDictionary.DefaultLight)
            .SelectMany(style => new[]
            {
                (style.ScopeName, Color: style.Foreground),
                (style.ScopeName, Color: style.Background),
            })
            .Where(entry => !string.IsNullOrEmpty(entry.Color));

        foreach (var (scopeName, color) in styles)
        {
            Assert.True(
                CodeHighlighter.BrushFromHex(color, scopeName) is not null,
                $"'{color}' for scope '{scopeName}' does not normalize to a color.");
        }
    }
}
