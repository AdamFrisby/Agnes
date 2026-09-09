using Windows.System;

namespace Agnes.App.Controls;

/// <summary>
/// The browser's key codes, as the X keysym names the display channel speaks.
///
/// A browser gives real key events, so unlike the phone this head can forward presses and releases as
/// they happen — which is what makes a modifier chord (ctrl+C, alt+Tab) work at all. What it gives is
/// a <see cref="VirtualKey"/>, a Windows-shaped enum, and the guest wants an X keysym name; this is the
/// table between them.
///
/// Deliberately not exhaustive. Letters, digits, the navigation block, the function keys and the four
/// modifiers cover everything you would actually drive a sandbox with. Anything else returns null and is
/// dropped rather than guessed at, because a wrong keysym types a wrong character silently.
/// </summary>
public static class DisplayKeys
{
    public static string? Map(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => ((char)('a' + (key - VirtualKey.A))).ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - VirtualKey.Number0))).ToString(),
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => "KP_" + (key - VirtualKey.NumberPad0),
        >= VirtualKey.F1 and <= VirtualKey.F12 => "F" + (1 + (key - VirtualKey.F1)),

        VirtualKey.Enter => "Return",
        VirtualKey.Tab => "Tab",
        VirtualKey.Escape => "Escape",
        // "Back" is the browser's name for the key with the arrow on it; X calls it BackSpace, and
        // sending "Back" would be a no-op the user reads as a dropped keystroke.
        VirtualKey.Back => "BackSpace",
        VirtualKey.Delete => "Delete",
        VirtualKey.Space => "space",

        VirtualKey.Left => "Left",
        VirtualKey.Right => "Right",
        VirtualKey.Up => "Up",
        VirtualKey.Down => "Down",
        VirtualKey.Home => "Home",
        VirtualKey.End => "End",
        VirtualKey.PageUp => "Prior",
        VirtualKey.PageDown => "Next",
        VirtualKey.Insert => "Insert",

        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => "shift",
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "ctrl",
        // WinUI calls the alt key "Menu", a name inherited from Win32 that means nothing to a guest.
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => "alt",
        VirtualKey.LeftWindows or VirtualKey.RightWindows => "super",

        _ => null,
    };
}
