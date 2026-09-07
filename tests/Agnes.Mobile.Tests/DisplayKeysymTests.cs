using Agnes.App.Mobile.Controls;

namespace Agnes.Mobile.Tests;

/// <summary>
/// Characters to X keysym names. The failure this guards against is silent: an unmapped character that
/// gets sent anyway types <em>something else</em> in the guest, and nobody looking at a phone screen
/// would know which key it was.
/// </summary>
public sealed class DisplayKeysymTests
{
    [Theory]
    [InlineData('a', "a")]
    [InlineData('Z', "Z")]
    [InlineData('7', "7")]
    [InlineData(' ', "space")]
    [InlineData('/', "slash")]
    [InlineData('-', "minus")]
    [InlineData('.', "period")]
    [InlineData('_', "underscore")]
    [InlineData('~', "asciitilde")]
    [InlineData('|', "bar")]
    [InlineData('\\', "backslash")]
    [InlineData('\'', "apostrophe")]
    [InlineData('"', "quotedbl")]
    [InlineData('\n', "Return")]
    [InlineData('\t', "Tab")]
    public void Ascii_maps_to_the_name_xdotool_expects(char c, string expected)
    {
        Assert.True(DisplayKeysyms.TryMap(c, out var keysym));
        Assert.Equal(expected, keysym);
    }

    [Theory]
    [InlineData('é')]
    [InlineData('日')]
    [InlineData(' ')]
    public void Anything_without_a_keysym_is_refused_rather_than_guessed(char c)
    {
        Assert.False(DisplayKeysyms.TryMap(c, out var keysym));
        Assert.Equal(string.Empty, keysym);
    }

    [Fact]
    public void A_path_and_a_newline_are_the_thing_this_is_actually_for()
    {
        Assert.Equal(
            new[] { "c", "d", "space", "slash", "t", "m", "p", "Return" },
            DisplayKeysyms.Map("cd /tmp\n"));
    }

    [Fact]
    public void Unmappable_characters_are_dropped_and_the_rest_still_types()
    {
        Assert.Equal(new[] { "c", "a", "f", "e" }, DisplayKeysyms.Map("caf\u00e9e"));
        Assert.Equal(new[] { "o", "k" }, DisplayKeysyms.Map("o\u00e9k"));
    }
}
