using System.Globalization;

namespace Agnes.Sandbox.Incus.Graphical;

/// <summary>
/// Translates an X keysym name (<c>Return</c>, <c>ctrl</c>, <c>a</c>, <c>KP_0</c> — the vocabulary
/// <c>xdotool key</c> and Anthropic's computer-use tool speak) into the number QEMU's
/// <c>org.qemu.Display1.Keyboard.Press/Release</c> wants, plus whether Shift has to be held for it.
/// </summary>
/// <remarks>
/// <para>
/// The number is a QEMU <em>qnum</em>, not an evdev code and not an X keycode. QEMU's D-Bus keyboard
/// runs the argument through <c>qemu_input_key_number_to_qcode()</c>, whose input space is the AT set-1
/// ("XT") scancode with the <c>0xE0</c> escape folded into the high bit: <c>Up</c> is set-1 <c>E0 48</c>
/// and so qnum <c>0xC8</c>, while <c>a</c> is plain <c>0x1E</c>. Source of truth: the <c>qnum</c> column of
/// <c>keycodemapdb</c>'s <c>data/keymaps.csv</c>, which QEMU generates
/// <c>ui/input-keymap-qnum-to-qcode.c.inc</c> from; the low half coincides with Linux evdev codes for the
/// main block (evdev was derived from set-1) and diverges above it, which is exactly the trap this table
/// exists to avoid — <c>Up</c> is evdev 103 but qnum 0xC8.
/// </para>
/// <para>
/// Shift is modelled here rather than left to the caller because the vocabulary is keysyms: a caller
/// asking for <c>colon</c> or <c>A</c> means the character, and on a PC keyboard the character is a
/// shifted key. The caller never has to know which. Only the US layout is modelled — the guest image
/// is baked with a US layout for exactly this reason (see docs/graphical-sandbox.md).
/// </para>
/// </remarks>
internal static class QemuKeyMap
{
    /// <summary>Left Shift's qnum, held around a key that needs it.</summary>
    internal const uint ShiftLeft = 0x2A;

    /// <summary>Resolves a keysym name. Returns false for a name this table doesn't model.</summary>
    internal static bool TryResolve(string keysym, out uint qnum, out bool needsShift)
    {
        ArgumentNullException.ThrowIfNull(keysym);
        if (Unshifted.TryGetValue(keysym, out qnum))
        {
            needsShift = false;
            return true;
        }

        if (Shifted.TryGetValue(keysym, out qnum))
        {
            needsShift = true;
            return true;
        }

        // Single upper-case letters are the shifted form of their lower-case key. Spelled as a rule
        // rather than 26 more entries, because that is what they are.
        if (keysym.Length == 1 && char.IsAsciiLetterUpper(keysym[0]))
        {
            needsShift = true;
            return Unshifted.TryGetValue(keysym.ToLowerInvariant(), out qnum);
        }

        needsShift = false;
        return false;
    }

    /// <summary>Every keysym name this table knows (both cases), for tests and diagnostics.</summary>
    internal static IEnumerable<string> KnownKeys => Unshifted.Keys.Concat(Shifted.Keys);

    private static readonly Dictionary<string, uint> Unshifted = BuildUnshifted();
    private static readonly Dictionary<string, uint> Shifted = BuildShifted();

    private static Dictionary<string, uint> BuildUnshifted()
    {
        var m = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            // --- row 1 ---
            ["Escape"] = 0x01,
            ["Esc"] = 0x01,
            ["minus"] = 0x0C,
            ["equal"] = 0x0D,
            ["BackSpace"] = 0x0E,
            ["Tab"] = 0x0F,
            ["bracketleft"] = 0x1A,
            ["bracketright"] = 0x1B,
            ["Return"] = 0x1C,
            ["Enter"] = 0x1C,
            ["semicolon"] = 0x27,
            ["apostrophe"] = 0x28,
            ["grave"] = 0x29,
            ["backslash"] = 0x2B,
            ["comma"] = 0x33,
            ["period"] = 0x34,
            ["slash"] = 0x35,
            ["space"] = 0x39,
            ["Caps_Lock"] = 0x3A,
            ["Num_Lock"] = 0x45,
            ["Scroll_Lock"] = 0x46,

            // --- modifiers. Both the X names and xdotool's short aliases. ---
            ["Control_L"] = 0x1D,
            ["ctrl"] = 0x1D,
            ["Control"] = 0x1D,
            ["Control_R"] = 0x9D,
            ["Shift_L"] = ShiftLeft,
            ["shift"] = ShiftLeft,
            ["Shift"] = ShiftLeft,
            ["Shift_R"] = 0x36,
            ["Alt_L"] = 0x38,
            ["alt"] = 0x38,
            ["Alt"] = 0x38,
            ["Alt_R"] = 0xB8,
            ["ISO_Level3_Shift"] = 0xB8,
            ["Super_L"] = 0xDB,
            ["super"] = 0xDB,
            ["Super"] = 0xDB,
            ["Meta_L"] = 0xDB,
            ["Super_R"] = 0xDC,
            ["Menu"] = 0xDD,

            // --- navigation (all extended: 0x80 | set-1 low byte) ---
            ["Home"] = 0xC7,
            ["Up"] = 0xC8,
            ["Page_Up"] = 0xC9,
            ["Prior"] = 0xC9,
            ["Left"] = 0xCB,
            ["Right"] = 0xCD,
            ["End"] = 0xCF,
            ["Down"] = 0xD0,
            ["Page_Down"] = 0xD1,
            ["Next"] = 0xD1,
            ["Insert"] = 0xD2,
            ["Delete"] = 0xD3,
            ["Print"] = 0xB7,
            ["Sys_Req"] = 0xB7,
            ["Pause"] = 0xC6,

            // --- keypad ---
            ["KP_Multiply"] = 0x37,
            ["KP_Subtract"] = 0x4A,
            ["KP_Add"] = 0x4E,
            ["KP_7"] = 0x47,
            ["KP_Home"] = 0x47,
            ["KP_8"] = 0x48,
            ["KP_Up"] = 0x48,
            ["KP_9"] = 0x49,
            ["KP_Prior"] = 0x49,
            ["KP_4"] = 0x4B,
            ["KP_Left"] = 0x4B,
            ["KP_5"] = 0x4C,
            ["KP_Begin"] = 0x4C,
            ["KP_6"] = 0x4D,
            ["KP_Right"] = 0x4D,
            ["KP_1"] = 0x4F,
            ["KP_End"] = 0x4F,
            ["KP_2"] = 0x50,
            ["KP_Down"] = 0x50,
            ["KP_3"] = 0x51,
            ["KP_Next"] = 0x51,
            ["KP_0"] = 0x52,
            ["KP_Insert"] = 0x52,
            ["KP_Decimal"] = 0x53,
            ["KP_Delete"] = 0x53,
            ["KP_Divide"] = 0xB5,
            ["KP_Enter"] = 0x9C,
        };

        // Digits 1..9,0 sit contiguously at 0x02..0x0B.
        for (var d = 1; d <= 9; d++)
        {
            m[d.ToString(CultureInfo.InvariantCulture)] = (uint)(0x01 + d);
        }

        m["0"] = 0x0B;

        // Letters, in QWERTY scancode order — the layout of the keyboard, not the alphabet.
        const string Row1 = "qwertyuiop";
        const string Row2 = "asdfghjkl";
        const string Row3 = "zxcvbnm";
        for (var i = 0; i < Row1.Length; i++)
        {
            m[Row1[i].ToString()] = (uint)(0x10 + i);
        }

        for (var i = 0; i < Row2.Length; i++)
        {
            m[Row2[i].ToString()] = (uint)(0x1E + i);
        }

        for (var i = 0; i < Row3.Length; i++)
        {
            m[Row3[i].ToString()] = (uint)(0x2C + i);
        }

        // F1..F10 are contiguous; F11/F12 are not (they were added later, after the keypad block).
        for (var f = 1; f <= 10; f++)
        {
            m["F" + f.ToString(CultureInfo.InvariantCulture)] = (uint)(0x3A + f);
        }

        m["F11"] = 0x57;
        m["F12"] = 0x58;
        return m;
    }

    // The shifted half of a US layout: the keysym names for the characters you get with Shift held.
    private static Dictionary<string, uint> BuildShifted() => new(StringComparer.Ordinal)
    {
        ["exclam"] = 0x02,
        ["at"] = 0x03,
        ["numbersign"] = 0x04,
        ["dollar"] = 0x05,
        ["percent"] = 0x06,
        ["asciicircum"] = 0x07,
        ["ampersand"] = 0x08,
        ["asterisk"] = 0x09,
        ["parenleft"] = 0x0A,
        ["parenright"] = 0x0B,
        ["underscore"] = 0x0C,
        ["plus"] = 0x0D,
        ["braceleft"] = 0x1A,
        ["braceright"] = 0x1B,
        ["colon"] = 0x27,
        ["quotedbl"] = 0x28,
        ["asciitilde"] = 0x29,
        ["bar"] = 0x2B,
        ["less"] = 0x33,
        ["greater"] = 0x34,
        ["question"] = 0x35,
    };
}
