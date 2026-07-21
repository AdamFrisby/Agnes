using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;

namespace Agnes.App.Desktop.Controls;

/// <summary>
/// Adds a right-click <b>Copy</b> / <b>Copy all</b> menu (and Ctrl+C) to read-only selectable
/// transcript surfaces. Avalonia's <see cref="SelectableTextBlock"/> and Markdown.Avalonia's
/// MarkdownScrollViewer both support text selection but ship no context menu, so selected transcript
/// text previously had no copy affordance (unlike the input TextBox, which has the full system menu).
/// Attach with <c>ctl:SelectionContextMenu.Enable="True"</c>.
/// </summary>
public static class SelectionContextMenu
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enable", typeof(SelectionContextMenu));

    public static void SetEnable(Control c, bool value) => c.SetValue(EnableProperty, value);
    public static bool GetEnable(Control c) => c.GetValue(EnableProperty);

    static SelectionContextMenu() => EnableProperty.Changed.AddClassHandler<Control>(OnEnableChanged);

    private static void OnEnableChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            control.ContextFlyout = BuildFlyout(control);
            control.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        }
        else
        {
            control.ContextFlyout = null;
            control.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        }
    }

    private static MenuFlyout BuildFlyout(Control control)
    {
        var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += (_, _) => CopyText(control, selectionOnly: true);

        var copyAll = new MenuItem { Header = "Copy all" };
        copyAll.Click += (_, _) => CopyText(control, selectionOnly: false);

        var flyout = new MenuFlyout();
        flyout.Items.Add(copy);
        flyout.Items.Add(copyAll);
        // "Copy" only makes sense with an active selection; "Copy all" is always available.
        flyout.Opening += (_, _) => copy.IsEnabled = HasSelection(control);
        return flyout;
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is Control control && e.Key == Key.C
            && e.KeyModifiers.HasFlag(KeyModifiers.Control) && HasSelection(control))
        {
            CopyText(control, selectionOnly: true);
            e.Handled = true;
        }
    }

    private static bool HasSelection(Control control) => !string.IsNullOrEmpty(GatherText(control, selectionOnly: true));

    private static void CopyText(Control control, bool selectionOnly)
    {
        var text = GatherText(control, selectionOnly);
        if (string.IsNullOrEmpty(text))
            return;
        _ = TopLevel.GetTopLevel(control)?.Clipboard?.SetTextAsync(text);
    }

    /// <summary>
    /// Pulls text out of whichever selectable surface this is. SelectableTextBlock exposes its own
    /// selection; a MarkdownScrollViewer renders as a tree of ColorTextBlock CTextBlocks, so gather
    /// each one's selected (or full) text and join across paragraphs/list-items/cells.
    /// </summary>
    private static string GatherText(Control control, bool selectionOnly)
    {
        if (control is SelectableTextBlock stb)
            return (selectionOnly ? stb.SelectedText : stb.Text) ?? string.Empty;

        var parts = control.GetVisualDescendants().OfType<CTextBlock>()
            .Select(t => selectionOnly ? t.GetSelectedText() : t.Text)
            .Where(s => !string.IsNullOrEmpty(s));
        return string.Join(selectionOnly ? "\n" : "\n\n", parts);
    }
}
