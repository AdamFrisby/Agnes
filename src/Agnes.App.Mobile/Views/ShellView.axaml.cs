using Agnes.App.Mobile.Controls;
using Agnes.App.Mobile.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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


        _navGrid = this.FindControl<UniformGrid>("NavGrid")!;
        _paneGrid = this.FindControl<Grid>("PaneGrid")!;
        _detailPane = this.FindControl<Border>("DetailPane")!;

        // The window's size is the one input every adaptive decision derives from; the shell view is
        // where it is known. A fold, a rotation or a resize lands here and re-lays the shell in place.
        SizeChanged += (_, e) => Shell?.Layout.Update(e.NewSize.Width, e.NewSize.Height);
        DataContextChanged += (_, _) => Wire();
        Wire();
    }

    private UniformGrid _navGrid = null!;
    private Grid _paneGrid = null!;
    private Border _detailPane = null!;
    private ShellViewModel? _wired;

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void Wire()
    {
        if (_wired is not null)
        {
            _wired.Layout.PropertyChanged -= OnLayoutChanged;
            _wired.PropertyChanged -= OnShellChanged;
        }

        _wired = Shell;
        if (_wired is not null)
        {
            _wired.Layout.PropertyChanged += OnLayoutChanged;
            _wired.PropertyChanged += OnShellChanged;
            if (Bounds.Width > 0)
            {
                _wired.Layout.Update(Bounds.Width, Bounds.Height);
            }
            Arrange();
        }
    }

    private void OnLayoutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WindowLayout.UseRail) or nameof(WindowLayout.TwoPane) or nameof(WindowLayout.PaneWidth))
        {
            Arrange();
        }
    }

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsDetailShown))
        {
            Arrange();
        }
    }

    /// <summary>Puts the destinations where the window wants them and gives the detail its column.</summary>
    private void Arrange()
    {
        if (Shell is not { } shell)
        {
            return;
        }

        var layout = shell.Layout;
        if (layout.UseRail)
        {
            Grid.SetRow(_navBar, 0);
            Grid.SetColumn(_navBar, 0);
            Grid.SetRowSpan(_navBar, 2);
            _navBar.BorderThickness = new Thickness(0, 0, 1, 0);
            _navGrid.Rows = 0;
            _navGrid.Columns = 1;
            _navGrid.Height = double.NaN;
            _navGrid.Width = 84;
            _navGrid.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }
        else
        {
            Grid.SetRow(_navBar, 1);
            Grid.SetColumn(_navBar, 1);
            Grid.SetRowSpan(_navBar, 1);
            _navBar.BorderThickness = new Thickness(0, 1, 0, 0);
            _navGrid.Rows = 1;
            _navGrid.Columns = 0;
            _navGrid.Height = 60;
            _navGrid.Width = double.NaN;
            _navGrid.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        }

        var twoPanes = layout.TwoPane && shell.IsDetailShown;
        _paneGrid.ColumnDefinitions = twoPanes
            ? new ColumnDefinitions($"{layout.PaneWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)},*")
            : new ColumnDefinitions("*,0");
        _detailPane.IsVisible = twoPanes;
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

