using Agnes.Host.Display;
using Agnes.Sandbox;

namespace Agnes.Host.Tests.Display;

/// <summary>
/// Chord parsing and text typing. The expansion order is the whole substance: a guest sees hardware, so
/// pressing the key before its modifiers, or forgetting to release one, is not a cosmetic difference — it is
/// a different keystroke and a modifier left stuck down for everything that follows.
/// </summary>
public class KeyChordTests
{
    [Fact]
    public void A_chord_presses_modifiers_then_the_key_and_releases_in_reverse()
    {
        var inputs = KeyChord.Parse("ctrl+shift+t").ToInputs();

        Assert.Equal(
            new DisplayInput[]
            {
                new KeyPress("ctrl", true),
                new KeyPress("shift", true),
                new KeyPress("t", true),
                new KeyPress("t", false),
                new KeyPress("shift", false),
                new KeyPress("ctrl", false),
            },
            inputs);
    }

    [Fact]
    public void A_bare_key_keeps_its_keysym_spelling()
    {
        // X keysym names are case-sensitive: Return is a key, "return" is not.
        var inputs = KeyChord.Parse("Return").ToInputs();
        Assert.Equal(new DisplayInput[] { new KeyPress("Return", true), new KeyPress("Return", false) }, inputs);
    }

    [Fact]
    public void Repeats_hold_the_modifiers_across_the_taps()
    {
        var inputs = KeyChord.Parse("ctrl+n").ToInputs(count: 2);

        Assert.Equal(
            new DisplayInput[]
            {
                new KeyPress("ctrl", true),
                new KeyPress("n", true),
                new KeyPress("n", false),
                new KeyPress("n", true),
                new KeyPress("n", false),
                new KeyPress("ctrl", false),
            },
            inputs);
    }

    [Theory]
    [InlineData("control+c", "ctrl+c")]
    [InlineData("cmd+s", "super+s")]
    [InlineData("Command+S", "super+s")]
    [InlineData("shift+ctrl+z", "ctrl+shift+z")]  // canonical order, whatever the caller wrote
    public void Normalization_is_the_one_answer_to_which_chord_this_is(string written, string expected)
        => Assert.Equal(expected, KeyChord.Parse(written).Normalize());

    [Fact]
    public void An_unknown_modifier_is_refused_rather_than_swallowed()
    {
        var bad = Assert.Throws<ArgumentException>(() => KeyChord.Parse("hyper+x"));
        Assert.Contains("is not a modifier", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_expands_to_key_presses_with_shift_where_it_belongs()
    {
        Assert.Equal(
            new DisplayInput[]
            {
                new KeyPress("shift", true),
                new KeyPress("h", true),
                new KeyPress("h", false),
                new KeyPress("shift", false),
                new KeyPress("i", true),
                new KeyPress("i", false),
                new KeyPress("shift", true),
                new KeyPress("1", true),   // '!' is shift+1 on the layout we target
                new KeyPress("1", false),
                new KeyPress("shift", false),
            },
            TypedText.ToInputs("Hi!"));
    }

    [Fact]
    public void Newline_and_space_are_named_keys()
    {
        var inputs = TypedText.ToInputs(" \n");
        Assert.Equal(
            new DisplayInput[]
            {
                new KeyPress("space", true),
                new KeyPress("space", false),
                new KeyPress("Return", true),
                new KeyPress("Return", false),
            },
            inputs);
    }

    [Fact]
    public void Non_ascii_is_refused_with_an_instruction_the_model_can_act_on()
    {
        var refused = Assert.Throws<ArgumentException>(() => TypedText.ToInputs("café ☕"));
        Assert.Contains("'é'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("write the text to a file", refused.Message, StringComparison.OrdinalIgnoreCase);
    }
}
