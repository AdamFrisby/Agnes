using Avalonia.Input;

namespace Agnes.App.Desktop.Controls;

/// <summary>
/// Avalonia's <see cref="Key"/> to the X keysym names the guest's input tool speaks
/// (<c>Return</c>, <c>ctrl</c>, <c>a</c>, <c>Page_Up</c>) — the vocabulary
/// <see cref="Agnes.Protocol.DisplayKey"/> documents.
/// </summary>
/// <remarks>
/// <para>
/// Only keys with a stable keysym name are here. Anything that produces a character — punctuation whose
/// symbol depends on the layout, accented letters, anything behind AltGr — deliberately is NOT, because a
/// table that guessed at those would be wrong on every non-US keyboard. Those arrive as
/// <c>TextInput</c> instead and go over as the character itself, which is a valid keysym name for the
/// printable Latin-1 range and is what the layout actually produced.
/// </para>
/// <para>
/// The modifier names are the short xdotool spellings (<c>ctrl</c>, not <c>Control_L</c>) because those are
/// what the host's computer-use vocabulary uses; the rest are the plain X11 names.
/// </para>
/// </remarks>
public static class X11Keysyms
{
    private static readonly Dictionary<Key, string> Map = Build();

    /// <summary>The keysym for a key, or null when the key only makes sense as text input.</summary>
    public static string? For(Key key) => Map.GetValueOrDefault(key);

    /// <summary>
    /// The keysym for a typed character, for what the key map cannot express. Space is named rather than
    /// sent literally, since a bare space is not a usable token.
    /// </summary>
    public static string? ForText(string? text)
        => text is not { Length: 1 } ? null : text[0] switch
        {
            ' ' => "space",
            var c when char.IsControl(c) => null,
            var c => c.ToString(),
        };

    private static Dictionary<Key, string> Build()
    {
        var map = new Dictionary<Key, string>
        {
            // Editing and whitespace.
            [Key.Return] = "Return",
            [Key.Enter] = "Return",
            [Key.Tab] = "Tab",
            [Key.Escape] = "Escape",
            [Key.Back] = "BackSpace",
            [Key.Delete] = "Delete",
            [Key.Space] = "space",
            [Key.Insert] = "Insert",

            // Navigation.
            [Key.Left] = "Left",
            [Key.Right] = "Right",
            [Key.Up] = "Up",
            [Key.Down] = "Down",
            [Key.Home] = "Home",
            [Key.End] = "End",
            [Key.PageUp] = "Page_Up",
            [Key.PageDown] = "Page_Down",

            // Modifiers, in the short spellings the guest's input tool takes.
            [Key.LeftShift] = "shift",
            [Key.RightShift] = "shift",
            [Key.LeftCtrl] = "ctrl",
            [Key.RightCtrl] = "ctrl",
            [Key.LeftAlt] = "alt",
            [Key.RightAlt] = "alt",
            [Key.LWin] = "super",
            [Key.RWin] = "super",
            [Key.CapsLock] = "Caps_Lock",

            // Occasionally load-bearing in a terminal or an editor.
            [Key.PrintScreen] = "Print",
            [Key.Pause] = "Pause",
            [Key.NumLock] = "Num_Lock",
            [Key.Scroll] = "Scroll_Lock",
            [Key.Apps] = "Menu",
        };

        // Letters: X names them by their lower-case character, and shift is carried as its own key event
        // rather than by renaming the letter.
        for (var key = Key.A; key <= Key.Z; key++)
        {
            map[key] = ((char)('a' + (key - Key.A))).ToString();
        }

        // Digit row.
        for (var key = Key.D0; key <= Key.D9; key++)
        {
            map[key] = ((char)('0' + (key - Key.D0))).ToString();
        }

        // Numeric keypad, which is distinct from the digit row to anything that reads raw keysyms.
        for (var key = Key.NumPad0; key <= Key.NumPad9; key++)
        {
            map[key] = $"KP_{(char)('0' + (key - Key.NumPad0))}";
        }

        map[Key.Add] = "KP_Add";
        map[Key.Subtract] = "KP_Subtract";
        map[Key.Multiply] = "KP_Multiply";
        map[Key.Divide] = "KP_Divide";
        map[Key.Decimal] = "KP_Decimal";

        // Function keys.
        for (var key = Key.F1; key <= Key.F12; key++)
        {
            map[key] = $"F{key - Key.F1 + 1}";
        }

        return map;
    }
}
