using Agnes.App.Mobile.Controls;
using Agnes.App.Mobile.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Agnes.App.Mobile.Views;

/// <summary>
/// The app frame. Owns two things the view models can't: the system back gesture, and the display
/// cutouts.
///
/// Safe areas are applied here rather than by Avalonia's automatic padding because the app wants
/// different treatment per edge — the top bar's background should run under the status bar while its
/// content sits below it, and the bottom navigation should extend into the gesture area while keeping
/// its targets above it. Automatic padding would inset the whole surface and leave letterboxed bands.
/// </summary>
public partial class ShellView : UserControl
{
    private Border _navBar = null!;
    private SheetHost _sheets = null!;
    private Border _toast = null!;

    public ShellView()
    {
        AvaloniaXamlLoader.Load(this);
        _navBar = this.FindControl<Border>("NavBar")!;
        _sheets = this.FindControl<SheetHost>("Sheets")!;
        _toast = this.FindControl<Border>("Toast")!;

        _sheets.Dismissed += (_, _) => (DataContext as ShellViewModel)?.CloseSheet();

    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        // One back handler for the whole app: the shell decides whether it closes a sheet, pops a page,
        // returns to the first tab, or lets Android leave.
        top.BackRequested += (_, args) =>
        {
            if (DataContext is ShellViewModel shell && shell.GoBack())
            {
                args.Handled = true;
            }
        };

        if (top.InsetsManager is { } insets)
        {
            ApplyInsets(insets.SafeAreaPadding);
            insets.SafeAreaChanged += (_, args) => ApplyInsets(args.SafeAreaPadding);
        }
    }

    // Everything else positions itself with a SafeSpacer strut; these two are set here because a Border's
    // padding and a floating element's margin aren't expressible as a child.
    private void ApplyInsets(Thickness safeArea)
    {
        // Bottom nav: background runs to the screen edge, targets stay above the gesture bar.
        _navBar.Padding = new Thickness(0, 0, 0, safeArea.Bottom);

        // Toast clears the status bar / notch.
        _toast.Margin = new Thickness(12, safeArea.Top + 10, 12, 0);
    }
}

