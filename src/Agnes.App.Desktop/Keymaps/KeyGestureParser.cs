using Avalonia.Input;

namespace Agnes.App.Desktop.Keymaps;

public static class KeyGestureParser
{
    private static readonly Dictionary<string, Key> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = Key.Enter,
        ["escape"] = Key.Escape,
        ["esc"] = Key.Escape,
        ["tab"] = Key.Tab,
        ["space"] = Key.Space,
        ["up"] = Key.Up,
        ["down"] = Key.Down,
        ["left"] = Key.Left,
        ["right"] = Key.Right,
        ["pageup"] = Key.PageUp,
        ["pagedown"] = Key.PageDown,
        ["home"] = Key.Home,
        ["end"] = Key.End,
        ["delete"] = Key.Delete,
        ["backspace"] = Key.Back,
    };

    public static bool TryParse(string text, out KeyGesture gesture, out string error)
    {
        gesture = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The key must not be empty.";
            return false;
        }

        if (text.Trim().Any(char.IsWhiteSpace))
        {
            error = "Chords are not supported in keymap v1.";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "The key must include one non-modifier key.";
            return false;
        }

        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyModifiers.Control,
                "shift" => KeyModifiers.Shift,
                "alt" or "option" => KeyModifiers.Alt,
                "cmd" or "command" or "meta" => KeyModifiers.Meta,
                _ => KeyModifiers.None,
            };
            if (modifier == KeyModifiers.None)
            {
                error = $"Unknown modifier '{parts[i]}'.";
                return false;
            }

            modifiers |= modifier;
        }

        var keyName = parts[^1];
        Key key;
        if (keyName.Length == 1 && char.IsDigit(keyName[0]))
        {
            key = (Key)((int)Key.D0 + (keyName[0] - '0'));
        }
        else if (keyName.Length == 1 && char.IsLetter(keyName[0]))
        {
            key = (Key)((int)Key.A + (char.ToUpperInvariant(keyName[0]) - 'A'));
        }
        else if (!NamedKeys.TryGetValue(keyName, out key)
                 && !(keyName.Length is 2 or 3 && keyName[0] is 'f' or 'F'
                      && int.TryParse(keyName[1..], System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out var function) && function is >= 1 and <= 24
                      && Enum.TryParse($"F{function}", out key)))
        {
            error = $"Unknown key '{keyName}'.";
            return false;
        }

        gesture = new KeyGesture(key, modifiers);
        return true;
    }

    public static string Display(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Cmd");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        var key = gesture.Key is >= Key.D0 and <= Key.D9
            ? ((int)gesture.Key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : gesture.Key == Key.Enter ? "Enter" : gesture.Key switch
            {
                Key.PageUp => "PageUp",
                Key.PageDown => "PageDown",
                _ => gesture.Key.ToString(),
            };
        parts.Add(key);
        return string.Join('+', parts);
    }

    public static string ToKeymapString(KeyGesture gesture) => Display(gesture).ToLowerInvariant();
}
