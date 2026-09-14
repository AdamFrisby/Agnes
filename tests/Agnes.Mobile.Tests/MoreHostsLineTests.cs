using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Client;
using Agnes.Ui.Core;

namespace Agnes.Mobile.Tests;

/// <summary>The More tab's host line follows the hosts. It said "0 of 1 online" beside a host that was
/// serving sessions, because it was read once and never again.</summary>
public sealed class MoreHostsLineTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-mobile-tests-" + Guid.NewGuid().ToString("n"));

    public MoreHostsLineTests() => JsonStore.UseDirectory(_state);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void The_hosts_line_follows_a_hosts_connection_state()
    {
        var shell = new ShellViewModel(new MobileConnector(), ImmediateDispatcher.Instance, new MobileSettings(), "Test device");
        var more = new MoreViewModel(shell);
        var changes = 0;
        more.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MoreViewModel.HostsDetail)) { changes++; } };

        var link = shell.Hosts.Add(new SavedHost("box", "https://box:5081", "token"));
        Assert.Equal("0 of 1 online", more.HostsDetail);

        link.State = AgnesConnectionState.Connected;

        Assert.Equal("1 of 1 online", more.HostsDetail);
        Assert.True(changes >= 2);

        shell.Hosts.Remove(link);
        Assert.Equal("None paired yet", more.HostsDetail);
    }
}
