using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The multi-server VM surface (connectivity/02): the merged host list + cross-host session aggregate over an
/// <see cref="IAgnesConnector"/>, exercised offline against two simultaneous <see cref="SimulatedHost"/>s.
/// </summary>
public class MultiHostViewModelTests
{
    private const string HostA = "sim://host-a";
    private const string HostB = "sim://host-b";

    [Fact]
    public async Task Holds_two_hosts_at_once_each_with_its_own_session_list()
    {
        var connector = new SimulatedConnector();
        var vm = new MultiHostViewModel(connector, ImmediateDispatcher.Instance);

        var a = await vm.AddHostAsync(HostA, "token-a");
        var b = await vm.AddHostAsync(HostB, "token-b");

        // Distinct sessions on each host.
        await a.OpenSessionAsync("claude-code", "/work/a");
        await b.OpenSessionAsync("opencode", "/work/b1");
        await b.OpenSessionAsync("opencode", "/work/b2");
        vm.Refresh();

        Assert.Equal(2, vm.HostCount);
        Assert.All(vm.Hosts, h => Assert.True(h.IsConnected));

        var rowA = Assert.Single(vm.Hosts, h => h.HostId == HostA);
        var rowB = Assert.Single(vm.Hosts, h => h.HostId == HostB);
        Assert.Equal(1, rowA.SessionCount);
        Assert.Equal(2, rowB.SessionCount);
    }

    [Fact]
    public async Task Aggregate_unions_sessions_across_hosts_each_tagged_with_its_host()
    {
        var connector = new SimulatedConnector();
        var vm = new MultiHostViewModel(connector, ImmediateDispatcher.Instance);

        var a = await vm.AddHostAsync(HostA, "token-a");
        var b = await vm.AddHostAsync(HostB, "token-b");
        await a.OpenSessionAsync("claude-code", "/work/a");
        await b.OpenSessionAsync("opencode", "/work/b");
        vm.Refresh();

        Assert.Equal(2, vm.SessionCount);
        Assert.Contains(vm.Sessions, s => s.HostId == HostA);
        Assert.Contains(vm.Sessions, s => s.HostId == HostB);
        // Every session in the aggregate is attributable to a real host in the list.
        Assert.All(vm.Sessions, s => Assert.Contains(vm.Hosts, h => h.HostId == s.HostId));
    }

    [Fact]
    public async Task Removing_a_host_drops_only_its_sessions_from_the_aggregate()
    {
        var connector = new SimulatedConnector();
        var vm = new MultiHostViewModel(connector, ImmediateDispatcher.Instance);

        var a = await vm.AddHostAsync(HostA, "token-a");
        var b = await vm.AddHostAsync(HostB, "token-b");
        await a.OpenSessionAsync("claude-code", "/work/a");
        await b.OpenSessionAsync("opencode", "/work/b");
        vm.Refresh();
        Assert.Equal(2, vm.HostCount);
        Assert.Equal(2, vm.SessionCount);

        await vm.RemoveHostAsync(HostA);

        // Only host B (and its session) remain; it is still connected.
        Assert.Equal(1, vm.HostCount);
        var remaining = Assert.Single(vm.Hosts);
        Assert.Equal(HostB, remaining.HostId);
        Assert.True(remaining.IsConnected);
        var session = Assert.Single(vm.Sessions);
        Assert.Equal(HostB, session.HostId);
    }

    [Fact]
    public async Task Adding_a_host_updates_the_aggregate()
    {
        var connector = new SimulatedConnector();
        var vm = new MultiHostViewModel(connector, ImmediateDispatcher.Instance);

        var a = await vm.AddHostAsync(HostA, "token-a");
        await a.OpenSessionAsync("claude-code", "/work/a");
        vm.Refresh();
        Assert.Equal(1, vm.HostCount);

        var b = await vm.AddHostAsync(HostB, "token-b");
        await b.OpenSessionAsync("opencode", "/work/b");
        vm.Refresh();

        Assert.Equal(2, vm.HostCount);
        Assert.Equal(2, vm.SessionCount);
    }
}
