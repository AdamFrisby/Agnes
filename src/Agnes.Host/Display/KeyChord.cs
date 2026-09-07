using System.Text;
using Agnes.Sandbox;

namespace Agnes.Host.Display;

/// <summary>
/// One key press written the way <c>xdotool key</c> writes it — <c>ctrl+shift+t</c>, <c>Return</c>,
/// <c>alt+F4</c>. That vocabulary is deliberate: it is what every model with computer-use training has
/// already seen, so the tool layer takes it verbatim and this type is the only place that has to understand
/// it. A chord expands to a press/release <i>sequence</i>, because a guest sees hardware, not intent: hold
/// the modifiers, tap the key, let go in reverse.
/// </summary>
/// <param name="Modifiers">In the order the caller wrote them — that is the order they are pressed.</param>
/// <param name="Key">The X keysym / xdotool name of the key itself, spelled as the caller wrote it (keysym
/// names are case-sensitive: <c>Return</c> is not <c>return</c>).</param>
public sealed record KeyChord(IReadOnlyList<string> Modifiers, string Key)
{
    /// <summary>The modifier names this parser recognizes, mapped to the single spelling injected downstream.
    /// Anything not in here is the key, not a modifier — so an unknown name fails as a bad key rather than
    /// being silently swallowed as a modifier nobody pressed.</summary>
    private static readonly IReadOnlyDictionary<string, string> ModifierAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = "ctrl",
            ["control"] = "ctrl",
            ["alt"] = "alt",
            ["option"] = "alt",
            ["shift"] = "shift",
            ["super"] = "super",
            ["cmd"] = "super",
            ["command"] = "super",
            ["win"] = "super",
            ["windows"] = "super",
            ["meta"] = "meta",
        };

    /// <summary>Canonical ordering for the matching form only. Injection keeps the caller's order.</summary>
    private static readonly string[] CanonicalOrder = ["ctrl", "alt", "shift", "super", "meta"];

    /// <summary>
    /// Parses <c>ctrl+shift+t</c>. Every token but the last must be a known modifier; the last is the key.
    /// </summary>
    /// <exception cref="ArgumentException">Empty, or a modifier appears where the key should be (e.g. a bare
    /// <c>ctrl+</c>). The message is written for the model, because it is surfaced as the tool's error.</exception>
    public static KeyChord Parse(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
        {
            throw new ArgumentException("A key is required, written like 'Return', 'a' or 'ctrl+shift+t'.", nameof(chord));
        }

        var tokens = chord.Trim().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new ArgumentException($"'{chord}' is not a key. Write it like 'Return', 'a' or 'ctrl+shift+t'.", nameof(chord));
        }

        var modifiers = new List<string>(tokens.Length - 1);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!ModifierAliases.TryGetValue(tokens[i], out var canonical))
            {
                throw new ArgumentException(
                    $"'{tokens[i]}' in '{chord}' is not a modifier. Use ctrl, alt, shift, super or meta before the key.",
                    nameof(chord));
            }

            modifiers.Add(canonical);
        }

        return new KeyChord(modifiers, tokens[^1]);
    }

    /// <summary>
    /// The lower-case, canonically-ordered form used to match against a blocked-chord pattern. Ordering is
    /// normalized here and nowhere else, so <c>alt+ctrl+Delete</c> and <c>ctrl+alt+Delete</c> cannot be two
    /// different answers to the same policy question.
    /// </summary>
    public string Normalize()
    {
        var ordered = Modifiers
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => Array.IndexOf(CanonicalOrder, m))
            .ToArray();
        return ordered.Length == 0
            ? Key.ToLowerInvariant()
            : string.Join('+', ordered) + "+" + Key.ToLowerInvariant();
    }

    /// <summary>
    /// Whether this chord matches one of the operator's blocked patterns. A pattern is either an exact
    /// normalized chord (<c>ctrl+shift+i</c>) or modifiers plus <c>*</c> (<c>ctrl+alt+*</c>), which matches
    /// any key held with exactly those modifiers.
    /// </summary>
    public bool IsBlockedBy(IReadOnlyList<string> patterns)
    {
        var normalized = Normalize();
        var prefix = normalized.LastIndexOf('+') is var cut and >= 0 ? normalized[..(cut + 1)] : string.Empty;

        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().ToLowerInvariant();
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern.EndsWith("+*", StringComparison.Ordinal))
            {
                // "ctrl+alt+*" — the modifiers must match exactly; the key is free.
                if (prefix.Length > 0 && string.Equals(prefix, pattern[..^1], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (string.Equals(pattern, normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The hardware the guest sees: each modifier down in written order, the key down, the key up, then the
    /// modifiers up in reverse. Repeated <paramref name="count"/> times with the modifiers held across the
    /// repeats — which is what a person holding ctrl and tapping T twice actually does.
    /// </summary>
    public IReadOnlyList<DisplayInput> ToInputs(int count = 1)
    {
        var repeats = Math.Max(1, count);
        var inputs = new List<DisplayInput>(Modifiers.Count * 2 + repeats * 2);
        foreach (var modifier in Modifiers)
        {
            inputs.Add(new KeyPress(modifier, Down: true));
        }

        for (var i = 0; i < repeats; i++)
        {
            inputs.Add(new KeyPress(Key, Down: true));
            inputs.Add(new KeyPress(Key, Down: false));
        }

        for (var i = Modifiers.Count - 1; i >= 0; i--)
        {
            inputs.Add(new KeyPress(Modifiers[i], Down: false));
        }

        return inputs;
    }
}

/// <summary>
/// Turning a string into keystrokes. There is no "type this text" primitive at the display seam — the guest
/// is handed emulated hardware — so text becomes a key sequence here, one character at a time.
/// <para>
/// ASCII only, on purpose. A keysym press reaches the guest through its <em>own</em> keyboard layout, and
/// only the ASCII range is a stable, layout-independent mapping; a keysym for <c>é</c> lands wherever the
/// guest's current layout happens to put it, which is usually nowhere. So a non-ASCII character is refused
/// with an error the model can act on rather than typed into the void.
/// </para>
/// </summary>
public static class TypedText
{
    /// <summary>Characters that need shift held, and the unshifted keysym under them on a US layout.</summary>
    private static readonly IReadOnlyDictionary<char, string> ShiftedSymbols = new Dictionary<char, string>
    {
        ['!'] = "1", ['@'] = "2", ['#'] = "3", ['$'] = "4", ['%'] = "5",
        ['^'] = "6", ['&'] = "7", ['*'] = "8", ['('] = "9", [')'] = "0",
        ['_'] = "minus", ['+'] = "equal", ['{'] = "bracketleft", ['}'] = "bracketright",
        ['|'] = "backslash", [':'] = "semicolon", ['"'] = "apostrophe", ['<'] = "comma",
        ['>'] = "period", ['?'] = "slash", ['~'] = "grave",
    };

    /// <summary>Unshifted punctuation, by the X keysym name the display seam expects.</summary>
    private static readonly IReadOnlyDictionary<char, string> PlainSymbols = new Dictionary<char, string>
    {
        [' '] = "space", ['-'] = "minus", ['='] = "equal", ['['] = "bracketleft", [']'] = "bracketright",
        ['\\'] = "backslash", [';'] = "semicolon", ['\''] = "apostrophe", [','] = "comma",
        ['.'] = "period", ['/'] = "slash", ['`'] = "grave",
        ['\n'] = "Return", ['\r'] = "Return", ['\t'] = "Tab",
    };

    /// <summary>
    /// The key sequence that types <paramref name="text"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The text contains a character that cannot be typed as a key. The
    /// message names the characters and says what to do instead, because the model reads it.</exception>
    public static IReadOnlyList<DisplayInput> ToInputs(string text)
    {
        var inputs = new List<DisplayInput>(text.Length * 2);
        var unsupported = new SortedSet<char>();

        foreach (var c in text)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                Tap(inputs, c.ToString(), shift: false);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                Tap(inputs, char.ToLowerInvariant(c).ToString(), shift: true);
            }
            else if (ShiftedSymbols.TryGetValue(c, out var shifted))
            {
                Tap(inputs, shifted, shift: true);
            }
            else if (PlainSymbols.TryGetValue(c, out var plain))
            {
                Tap(inputs, plain, shift: false);
            }
            else
            {
                unsupported.Add(c);
            }
        }

        if (unsupported.Count > 0)
        {
            var listed = string.Join(" ", unsupported.Select(c => $"'{c}'"));
            throw new ArgumentException(
                $"These characters can't be typed as keystrokes: {listed}. computer_type sends ASCII key presses, "
                + "which is all a guest keyboard layout reliably maps. Write the text to a file in the workspace "
                + "and open it in the guest instead.",
                nameof(text));
        }

        return inputs;
    }

    private static void Tap(List<DisplayInput> inputs, string key, bool shift)
    {
        if (shift)
        {
            inputs.Add(new KeyPress("shift", Down: true));
        }

        inputs.Add(new KeyPress(key, Down: true));
        inputs.Add(new KeyPress(key, Down: false));

        if (shift)
        {
            inputs.Add(new KeyPress("shift", Down: false));
        }
    }

    /// <summary>How many key events <paramref name="text"/> costs, without building them (for the budget check
    /// that must happen before anything is injected).</summary>
    public static int ByteLength(string text) => Encoding.UTF8.GetByteCount(text);
}
