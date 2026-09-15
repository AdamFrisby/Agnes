using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;

namespace Agnes.Desktop.Tests;

/// <summary>The settings rail groups its pages by where the setting lives, and a group leaves with its pages
/// when a search hides them — so a person searching "theme" sees "This device · Appearance", not four
/// orphaned headings.</summary>
public class SettingsNavTests
{
    private static MainWindowViewModel NewVm()
    {
        var id = Guid.NewGuid().ToString("n");
        return new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-tabs-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-arch-{id}.json")));
    }

    [Fact]
    public void Every_category_sits_in_exactly_one_group()
    {
        var vm = NewVm();
        var placed = vm.SettingsNavGroups.SelectMany(g => g.Items.Select(i => i.Id)).ToArray();

        Assert.Equal(placed.Length, placed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(vm.SettingsCategories.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal), placed.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(["This device", "Host", "Library", "Help"], vm.SettingsNavGroups.Select(g => g.Title));
        Assert.Equal("appearance", vm.SettingsNavGroups[0].Items[0].Id);
    }

    [Fact]
    public void A_search_hides_the_groups_it_empties()
    {
        var vm = NewVm();
        Assert.All(vm.SettingsNavGroups, g => Assert.True(g.IsVisible));

        vm.SettingsSearch = "theme";
        var thisDevice = vm.SettingsNavGroups.Single(g => g.Title == "This device");
        Assert.True(thisDevice.IsVisible);
        Assert.False(vm.SettingsNavGroups.Single(g => g.Title == "Help").IsVisible);

        vm.SettingsSearch = string.Empty;
        Assert.All(vm.SettingsNavGroups, g => Assert.True(g.IsVisible));
    }
}
