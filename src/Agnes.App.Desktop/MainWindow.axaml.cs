using System.ComponentModel;
using System.Linq;
using Agnes.App.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;

namespace Agnes.App.Desktop;

public partial class MainWindow : Window
{
    private bool _toolbarWired;
    private int _relocateAttempts;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        };

        // Merge the toolbar into the Dock document tab strip so tabs + toolbar share one row. Dock's
        // DocumentTabStrip exposes LeftContent/RightContent slots on either side of the tabs; we move the
        // top bar's LeftBar/RightBar into them (keeping the strip's drag/reorder/detach). The strip only
        // exists after the first document renders, so we retry on deferred dispatcher passes (never inside
        // a layout pass — reparenting there loops). The standalone top bar stays as a fallback until then.
        Loaded += (_, _) => ScheduleRelocateToolbar();
    }

    private void ScheduleRelocateToolbar()
    {
        if (_toolbarWired)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_toolbarWired || DataContext is null)
            {
                return;
            }

            if (this.FindControl<DockControl>("DockRoot") is { } dock
                && dock.GetVisualDescendants().OfType<DocumentTabStrip>().FirstOrDefault() is { } strip)
            {
                RelocateToolbar(strip);
            }
            else if (_relocateAttempts++ < 60)
            {
                ScheduleRelocateToolbar(); // the strip isn't realized yet — try again next cycle.
            }
        }, DispatcherPriority.Background);
    }

    private void RelocateToolbar(DocumentTabStrip strip)
    {
        // This project doesn't populate x:Name fields, so resolve by name.
        var leftBar = this.FindControl<Control>("LeftBar");
        var rightBar = this.FindControl<Control>("RightBar");
        if (leftBar is null || rightBar is null)
        {
            return;
        }

        // Detach the groups from the fallback top bar, then pin their DataContext to the window's VM so
        // their bindings keep resolving against it (not the DocumentDock the strip lives in).
        if (leftBar.Parent is Panel leftParent)
        {
            leftParent.Children.Remove(leftBar);
        }

        if (rightBar.Parent is Panel rightParent)
        {
            rightParent.Children.Remove(rightBar);
        }

        leftBar.DataContext = DataContext;
        rightBar.DataContext = DataContext;
        leftBar.Margin = new Avalonia.Thickness(10, 0, 8, 0);
        rightBar.Margin = new Avalonia.Thickness(8, 0, 10, 0);
        strip.LeftContent = leftBar;
        strip.RightContent = rightBar;

        if (this.FindControl<Control>("TopBar") is { } topBar)
        {
            topBar.IsVisible = false; // the toolbar now lives in the tab strip row.
        }

        _toolbarWired = true;
    }

    // Focus the palette's search box the moment it opens, so the user can type immediately.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsPaletteOpen)
            && sender is MainWindowViewModel { IsPaletteOpen: true }
            && this.FindControl<TextBox>("PaletteBox") is { } box)
        {
            Dispatcher.UIThread.Post(() => box.Focus());
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
