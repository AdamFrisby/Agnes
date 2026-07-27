using System.Collections.Generic;
using System.Linq;

namespace Agnes.App.Desktop;

/// <summary>One keyboard shortcut: the gesture as the user would read it, and what it does.</summary>
/// <param name="Gesture">The gesture, written the way the settings page shows it (e.g. <c>Ctrl+Shift+Tab</c>).</param>
/// <param name="Description">What pressing it does, in the user's terms.</param>
public sealed record KeyboardShortcut(string Gesture, string Description);

/// <summary>A named group of shortcuts (the scope they apply in).</summary>
public sealed record KeyboardShortcutGroup(string Title, IReadOnlyList<KeyboardShortcut> Shortcuts);

/// <summary>
/// The shortcuts the desktop app binds, as data rather than as prose typed into the settings view. The
/// previous list was five hardcoded lines that had already fallen behind the bindings in
/// <c>MainWindow.axaml</c> / <c>SessionTabView.axaml</c> — it omitted previous-tab, tab-by-number, the
/// dashboard, send-now and closing the find bar. Keeping one catalogue means the page and the search
/// keywords can't disagree with each other, and a new binding is one line here.
///
/// The gestures themselves still live where Avalonia needs them (XAML <c>KeyBinding</c>s); this is the
/// documented view of them, so a binding added there has to be added here to become discoverable. Rebinding
/// isn't supported yet, and the page says so rather than leaving the user hunting for the setting.
/// </summary>
public static class KeyboardShortcuts
{
    public static IReadOnlyList<KeyboardShortcutGroup> Groups { get; } =
    [
        new("Tabs and windows",
        [
            new("Ctrl+T", "New tab"),
            new("Ctrl+W", "Close the current tab"),
            new("Ctrl+Tab", "Next tab"),
            new("Ctrl+Shift+Tab", "Previous tab"),
            new("Ctrl+PageDown", "Next tab"),
            new("Ctrl+PageUp", "Previous tab"),
            new("Ctrl+1 … Ctrl+9", "Jump to a tab by position"),
            new("Ctrl+K", "Command palette"),
            new("Ctrl+Shift+D", "Status dashboard"),
        ]),

        new("Writing a prompt",
        [
            new("Ctrl+Enter", "Send (Cmd+Enter on macOS)"),
            new("Ctrl+Shift+Enter", "Send now — interrupts the running turn instead of queueing behind it"),
            new("Alt+↑", "Recall the previous prompt you sent"),
            new("Alt+↓", "Recall the next prompt"),
        ]),

        new("Moving around a transcript",
        [
            new("Ctrl+F", "Find in this session"),
            new("F3", "Next match"),
            new("Shift+F3", "Previous match"),
            new("Escape", "Close the find bar"),
            new("F8", "Next prompt"),
            new("F7", "Previous prompt"),
            new("Ctrl+F8", "Next file change"),
            new("Ctrl+F7", "Previous file change"),
        ]),

        new("Command palette (while it's open)",
        [
            new("↑ / ↓", "Move the selection"),
            new("Enter", "Run the selected command"),
            new("Escape", "Close the palette"),
        ]),
    ];

    /// <summary>
    /// Every gesture and description as lowercase search text, so the Settings search finds the Keyboard page
    /// by what a shortcut *does* ("palette", "interrupt", "find") and not only by the word "keyboard".
    /// </summary>
    public static string SearchKeywords { get; } = string.Join(
        ' ',
        Groups.SelectMany(g => g.Shortcuts.Select(s => $"{s.Gesture} {s.Description}")).Prepend("keyboard shortcuts keys bindings gestures"))
        .ToLowerInvariant();
}
