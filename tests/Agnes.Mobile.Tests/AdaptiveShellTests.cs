using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The shell answers the window's size: a bar on a phone, a rail in landscape and on anything wider, two
/// panes where there is room for a list beside a detail, and a detail that follows a fold or a rotation
/// between the pane and the stack without closing. The geometries are the devices this is for.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class AdaptiveShellTests(AvaloniaSession avalonia)
{
    /// <summary>A detail page with no view of its own: what the shell does with it is the subject.</summary>
    private sealed class Probe : PageViewModel
    {
        public override string Title => "probe";
        public override bool IsDetail => true;
    }

    [Theory]
    [InlineData(411, 891, WidthClass.Compact, false, false, false, 2, "a phone")]
    [InlineData(891, 411, WidthClass.Expanded, true, false, true, 3, "a phone in landscape")]
    [InlineData(378, 927, WidthClass.Compact, false, false, false, 2, "a Galaxy Z Fold 6, folded")]
    [InlineData(794, 924, WidthClass.Medium, true, true, true, 3, "a Galaxy Z Fold 6, open")]
    [InlineData(890, 923, WidthClass.Expanded, true, true, true, 3, "a Pixel 9 Pro Fold, open")]
    [InlineData(640, 1072, WidthClass.Medium, true, false, false, 2, "the 10-inch tablet, portrait")]
    [InlineData(1072, 640, WidthClass.Expanded, true, true, true, 4, "the 10-inch tablet, landscape")]
    public void The_window_decides_bar_or_rail_one_pane_or_two(
        double width, double height, WidthClass widthClass, bool rail, bool twoPane, bool sideSheets, int columns, string device)
    {
        var layout = new WindowLayout();
        layout.Update(width, height);

        Assert.Equal(widthClass, layout.WidthClass);
        Assert.Equal(rail, layout.UseRail);
        Assert.Equal(twoPane, layout.TwoPane);
        Assert.Equal(sideSheets, layout.SideSheets);
        Assert.Equal(columns, layout.GridColumns);
        Assert.Contains(device.Length > 0 ? (rail ? "rail" : "bar") : "", layout.Describe);
    }

    [Fact]
    public async Task The_destinations_are_a_bar_on_a_phone_and_a_rail_in_landscape()
    {
        await avalonia.Run(() =>
        {
            var (shell, window, view) = Open(411, 891);
            var navBar = view.FindControl<Border>("NavBar")!;
            var navGrid = view.FindControl<UniformGrid>("NavGrid")!;
            Assert.False(shell.Layout.UseRail);
            Assert.Equal(1, Grid.GetRow(navBar));
            Assert.Equal(1, navGrid.Rows);
            Assert.True(navBar.Bounds.Width > navBar.Bounds.Height, "a bar is wider than it is tall");

            // Rotate.
            window.Width = 891;
            window.Height = 411;
            Pump(window);

            Assert.True(shell.Layout.UseRail);
            Assert.Equal(0, Grid.GetColumn(navBar));
            Assert.Equal(2, Grid.GetRowSpan(navBar));
            Assert.Equal(1, navGrid.Columns);
            Assert.True(navBar.Bounds.Height > navBar.Bounds.Width, "a rail is taller than it is wide");
            Assert.True(navBar.Bounds.Width <= 90, "the rail is narrow");
            window.Close();
        });
    }

    [Fact]
    public async Task A_detail_opens_beside_the_list_with_two_panes_and_over_it_with_one()
    {
        await avalonia.Run(() =>
        {
            // A Pixel 9 Pro Fold, open.
            var (shell, window, view) = Open(890, 923);
            Assert.True(shell.Layout.TwoPane);

            var probe = new Probe();
            shell.Push(probe);

            Assert.Same(probe, shell.Detail);
            Assert.Empty(shell.Stack);
            Assert.True(shell.ShowTabs, "the list and the rail stay when a detail opens beside them");
            Pump(window);
            var paneGrid = view.FindControl<Grid>("PaneGrid")!;
            Assert.True(view.FindControl<Border>("DetailPane")!.IsVisible);
            Assert.Equal(WindowLayout.PaneWidthNarrow, paneGrid.ColumnDefinitions[0].ActualWidth, 0.5);

            // Fold it: the same page moves onto the stack and covers the screen.
            window.Width = 378;
            window.Height = 927;
            Pump(window);

            Assert.Null(shell.Detail);
            Assert.Same(probe, shell.CurrentPage);
            Assert.Single(shell.Stack);
            Assert.False(shell.ShowTabs);

            // Open it again: back beside the list, nothing lost.
            window.Width = 890;
            window.Height = 923;
            Pump(window);

            Assert.Same(probe, shell.Detail);
            Assert.Empty(shell.Stack);
            Assert.True(shell.ShowTabs);

            // Back closes the detail, and the list is alone again.
            Assert.True(shell.GoBack());
            Assert.Null(shell.Detail);
            Pump(window);
            Assert.False(view.FindControl<Border>("DetailPane")!.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public async Task A_phone_in_landscape_keeps_one_pane_because_it_has_no_height_for_two()
    {
        await avalonia.Run(() =>
        {
            var (shell, window, _) = Open(891, 411);
            Assert.True(shell.Layout.UseRail);
            Assert.False(shell.Layout.TwoPane);

            var probe = new Probe();
            shell.Push(probe);
            Assert.Null(shell.Detail);
            Assert.Same(probe, shell.CurrentPage);
            window.Close();
        });
    }

    private static (ShellViewModel Shell, Window Window, ShellView View) Open(double width, double height)
    {
        JsonStore.UseDirectory(Path.Combine(Path.GetTempPath(), "agnes-adaptive-" + Guid.NewGuid().ToString("n")));
        new CodeyBoxConfig().Save();
        var shell = new ShellViewModel(new MobileConnector(), new MobileDispatcher(), new MobileSettings(), "Adaptive", codeyBoxClient: _ => null);
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Pump(window);
        return (shell, window, view);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
