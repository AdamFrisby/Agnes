using System;
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

    // The smallest content size (in unscaled DIPs) we keep the UI at before letting it scroll. Matches the
    // window's MinWidth/MinHeight so at scale 1 the content fills exactly and never scrolls.
    private const double MinContentWidth = 720;
    private const double MinContentHeight = 480;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
                UpdateScaleRoot();
            }
        };
        // Re-pin the scaled root whenever the window resizes; FontScale changes are handled in
        // OnViewModelPropertyChanged. Loaded gives us a valid ClientSize for the first pass.
        SizeChanged += (_, _) => UpdateScaleRoot();
        Loaded += (_, _) => UpdateScaleRoot();

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
        // A little vertical breathing room above/below the search box and the right-hand buttons.
        leftBar.Margin = new Avalonia.Thickness(10, 7, 8, 7);
        rightBar.Margin = new Avalonia.Thickness(8, 7, 10, 7);
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
        else if (e.PropertyName == nameof(MainWindowViewModel.FontScale))
        {
            UpdateScaleRoot();
        }
    }

    // Pin the scaled content to (client / scale), floored at the minimum content size. When the scaled
    // content exceeds the viewport (large scale on a small window), the wrapping ScrollViewer lets the user
    // reach it instead of it being clipped; otherwise the content fills the window exactly.
    private void UpdateScaleRoot()
    {
        if (this.FindControl<Control>("ScaleRoot") is not { } root)
        {
            return;
        }

        var scale = (DataContext as MainWindowViewModel)?.FontScale ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var w = Math.Max(ClientSize.Width / scale, MinContentWidth);
        var h = Math.Max(ClientSize.Height / scale, MinContentHeight);
        if (Math.Abs(root.Width - w) > 0.5)
        {
            root.Width = w;
        }

        if (Math.Abs(root.Height - h) > 0.5)
        {
            root.Height = h;
        }

        // Only let the wrapping ScrollViewer scroll when zoomed past 100% (the only case that can overflow
        // the viewport). At <=100% the content is pinned to fit exactly, and keeping scrolling Disabled stops
        // a child BringIntoView (e.g. the transcript auto-scrolling to the latest message) from dragging the
        // whole UI — including the top chrome — off-screen.
        if (this.FindControl<ScrollViewer>("ScaleScroller") is { } scroller)
        {
            var vis = scale > 1.001
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            scroller.HorizontalScrollBarVisibility = vis;
            scroller.VerticalScrollBarVisibility = vis;
        }
    }

    // Focus the global-search box as soon as its flyout opens, so typing starts immediately (the bar that
    // opens it only looks like an input — the real one lives in the popover).
    private void OnGlobalSearchAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            Dispatcher.UIThread.Post(() => box.Focus());
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
