namespace Agnes.App.Mobile.Controls;

/// <summary>
/// Characters, as the X keysym names the display channel speaks (<c>DisplayKey.Key</c>).
///
/// A phone has no key events worth forwarding: the IME hands you finished <em>text</em>, and what the
/// user physically pressed to produce it (a long press, a swipe, a suggestion, an emoji sheet) is not
/// recoverable and would be the wrong thing to send anyway. So the keyboard affordance collects text
/// and this turns each character into the press the guest expects — <c>a</c>, <c>A</c>, <c>parenleft</c>.
///
/// Only the ASCII range maps. Anything else (an accented letter, CJK, an emoji) has no keysym name that
/// <c>xdotool key</c> would accept, and guessing one would type the wrong character rather than none —
/// <see cref="TryMap"/> says no and the caller drops it. Typing prose into a graphical sandbox from a
/// phone is not what this is for; typing a filename, a command and Return is.
/// </summary>
public static class DisplayKeysyms
{
    private static readonly Dictionary<char, string> Named = new()
    {
        [' '] = "space",
        ['!'] = "exclam",
        ['"'] = "quotedbl",
        ['#'] = "numbersign",
        ['$'] = "dollar",
        ['%'] = "percent",
        ['&'] = "ampersand",
        ['\''] = "apostrophe",
        ['('] = "parenleft",
        [')'] = "parenright",
        ['*'] = "asterisk",
        ['+'] = "plus",
        [','] = "comma",
        ['-'] = "minus",
        ['.'] = "period",
        ['/'] = "slash",
        [':'] = "colon",
        [';'] = "semicolon",
        ['<'] = "less",
        ['='] = "equal",
        ['>'] = "greater",
        ['?'] = "question",
        ['@'] = "at",
        ['['] = "bracketleft",
        ['\\'] = "backslash",
        [']'] = "bracketright",
        ['^'] = "asciicircum",
        ['_'] = "underscore",
        ['`'] = "grave",
        ['{'] = "braceleft",
        ['|'] = "bar",
        ['}'] = "braceright",
        ['~'] = "asciitilde",
        ['\n'] = Return,
        ['\r'] = Return,
        ['\t'] = "Tab",
        ['\b'] = BackSpace,
    };

    public const string Return = "Return";
    public const string BackSpace = "BackSpace";
    public const string Escape = "Escape";
    public const string Tab = "Tab";

    /// <summary>The keysym name for a character, or false when there isn't one.</summary>
    public static bool TryMap(char c, out string keysym)
    {
        if (Named.TryGetValue(c, out var named))
        {
            keysym = named;
            return true;
        }

        // Letters and digits are their own keysym names — "a", "A", "7" — so they need no table.
        if (char.IsAsciiLetterOrDigit(c))
        {
            keysym = c.ToString();
            return true;
        }

        keysym = string.Empty;
        return false;
    }

    /// <summary>Every keysym a string types, skipping the characters that have none.</summary>
    public static IReadOnlyList<string> Map(string text)
    {
        var keys = new List<string>(text.Length);
        foreach (var c in text)
        {
            if (TryMap(c, out var keysym))
            {
                keys.Add(keysym);
            }
        }

        return keys;
    }
}
