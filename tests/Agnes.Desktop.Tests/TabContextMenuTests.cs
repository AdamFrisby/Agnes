using Agnes.App.Desktop.Controls;
using Agnes.App.Desktop.Converters;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The tab strip's right-click menu. Its layout half can't bind Dock's IFactory methods directly —
/// Avalonia turns a method into a command only when it takes no parameters or a single object, and
/// Dock's take an IDockable — so it goes through <see cref="DockCommands"/>. A regression there is
/// invisible in XAML (the item just greys out), which is what these cover.
/// </summary>
public class TabContextMenuTests
{
    private static (string Tabs, string Hosts, string Archive) TempPaths()
        => (Path.Combine(Path.GetTempPath(), $"agnes-tabs-{Guid.NewGuid():n}.json"),
            Path.Combine(Path.GetTempPath(), $"agnes-hosts-{Guid.NewGuid():n}.json"),
            Path.Combine(Path.GetTempPath(), $"agnes-arch-{Guid.NewGuid():n}.json"));

    private static MainWindowViewModel NewVm()
    {
        var (t, h, a) = TempPaths();
        return new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(t), new HostRegistryStore(h), new NullPromptStore(), new SessionStateStore(a));
    }

    private static IDocumentDock DocumentDock(MainWindowViewModel vm)
        => (IDocumentDock)vm.Layout.VisibleDockables![0];

    [Fact]
    public async Task Close_closes_the_tab_it_was_invoked_on()
    {
        var vm = NewVm();
        await vm.RestoreAsync();
        vm.NewTabCommand.Execute(null);
        var dock = DocumentDock(vm);
        var before = dock.VisibleDockables!.Count;
        var tab = dock.VisibleDockables!.OfType<SessionDocument>().Last();

        Assert.True(DockCommands.Close.CanExecute(tab));
        DockCommands.Close.Execute(tab);

        Assert.Equal(before - 1, dock.VisibleDockables!.Count);
        Assert.DoesNotContain(tab, dock.VisibleDockables!);
    }

    [Fact]
    public async Task Close_other_tabs_leaves_only_the_one_it_was_invoked_on()
    {
        var vm = NewVm();
        await vm.RestoreAsync();
        vm.NewTabCommand.Execute(null);
        vm.NewTabCommand.Execute(null);
        var dock = DocumentDock(vm);
        Assert.True(dock.VisibleDockables!.Count > 1);
        var keep = dock.VisibleDockables!.OfType<SessionDocument>().Last();

        DockCommands.CloseOthers.Execute(keep);

        Assert.Equal(keep, Assert.Single(dock.VisibleDockables!));
    }

    [Fact]
    public async Task Every_layout_command_is_executable_on_a_real_tab()
    {
        // Each of these is a separate binding in TabMenu.axaml; a name that stops resolving disables
        // exactly one menu item and nothing else complains.
        var vm = NewVm();
        await vm.RestoreAsync();
        var tab = DocumentDock(vm).VisibleDockables!.OfType<SessionDocument>().Last();

        foreach (var command in new[]
                 {
                     DockCommands.Close, DockCommands.CloseOthers, DockCommands.CloseAll,
                     DockCommands.CloseLeft, DockCommands.CloseRight, DockCommands.Float,
                     DockCommands.FloatAll, DockCommands.NewHorizontalDock, DockCommands.NewVerticalDock,
                     DockCommands.TabsLeft, DockCommands.TabsTop, DockCommands.TabsRight,
                     DockCommands.LayoutTabbed, DockCommands.LayoutMdi,
                 })
        {
            Assert.True(command.CanExecute(tab));
        }
    }

    [Fact]
    public void A_dockable_with_no_factory_is_not_executable_rather_than_throwing()
    {
        // The menu is a shared flyout, so it can be evaluated against a tab that isn't docked yet.
        Assert.False(DockCommands.Close.CanExecute(null));
        Assert.False(DockCommands.Close.CanExecute("not a dockable"));
        DockCommands.Close.Execute(null); // no-op, not a crash
    }

    [Fact]
    public async Task Only_session_tabs_offer_the_session_half_of_the_menu()
    {
        var vm = NewVm();
        await vm.RestoreAsync();
        var converter = new IsSessionTabConverter();
        var dock = DocumentDock(vm);
        var session = dock.VisibleDockables!.OfType<SessionDocument>().Last();

        vm.OpenSettingsCommand.Execute(null);
        var settings = dock.VisibleDockables!.OfType<IDockable>().First(d => d is SettingsDocument);

        Assert.Equal(true, converter.Convert(session, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert(settings, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
    }
}
