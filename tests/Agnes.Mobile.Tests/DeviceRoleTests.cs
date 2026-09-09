using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The phone's side of device roles, drawn rather than described.
///
/// The bug these exist against is a screen that is silently *smaller* than someone else's: a member with
/// no sessions and no explanation, or a member offered an "let in as owner" button whose only possible
/// outcome is a refusal from the host. Both are absences, and an absence is exactly what a view-model
/// test can't see — so these lay out the real screens and read what is on them.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class DeviceRoleTests : IDisposable
{
    private readonly AvaloniaSession _avalonia;

    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "agnes-device-role-" + Guid.NewGuid().ToString("n"));

    public DeviceRoleTests(AvaloniaSession avalonia)
    {
        _avalonia = avalonia;
        JsonStore.UseDirectory(_state);
    }

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

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_owner_is_offered_both_ways_to_let_a_device_in()
    {
        await _avalonia.Run(() =>
        {
            var (shell, window) = OpenShell();
            Waiting(shell, DeviceRole.Owner);

            var texts = Texts(window);
            Assert.Contains("Let in as member", texts);
            Assert.Contains("Let in as owner", texts);
            Assert.Contains("Decline", texts);

            // The digits stay the loudest thing on the row: they are the whole security check.
            Assert.Contains("418302", texts);

            window.Close();
        });
    }

    [Fact]
    public async Task A_member_is_offered_only_the_member_way()
    {
        await _avalonia.Run(() =>
        {
            var (shell, window) = OpenShell();
            Waiting(shell, DeviceRole.Member);

            var texts = Texts(window);
            Assert.Contains("Let in as member", texts);
            Assert.DoesNotContain("Let in as owner", texts);
            Assert.Contains("418302", texts);

            window.Close();
        });
    }

    [Fact]
    public async Task A_member_with_no_sessions_is_told_why_and_where_to_go()
    {
        await _avalonia.Run(() =>
        {
            Remember(DeviceRole.Member);
            var (shell, window) = OpenShell();
            shell.SelectTab(ShellTab.Sessions);
            Pump();

            var texts = Texts(window);
            Assert.Contains(texts, t => t.Contains("member on workshop", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("More › Devices", StringComparison.Ordinal));
            Assert.Contains("Open More › Devices", texts);

            // The generic teaching line is replaced, not stacked on top of the explanation.
            Assert.DoesNotContain(texts, t => t.StartsWith("One host runs the agents", StringComparison.Ordinal));

            window.Close();
        });
    }

    [Fact]
    public async Task An_owner_with_no_sessions_still_gets_the_ordinary_empty_state()
    {
        await _avalonia.Run(() =>
        {
            Remember(DeviceRole.Owner);
            var (shell, window) = OpenShell();
            shell.SelectTab(ShellTab.Sessions);
            Pump();

            var texts = Texts(window);
            Assert.Contains(texts, t => t.StartsWith("One host runs the agents", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, t => t.Contains("member on", StringComparison.Ordinal));

            window.Close();
        });
    }

    [Fact]
    public async Task A_host_that_never_said_leaves_the_empty_state_alone()
    {
        await _avalonia.Run(() =>
        {
            Remember(null);
            var (shell, window) = OpenShell();

            // No role recorded — an older host, or one not asked yet. Explaining nothing is the right
            // answer: an owner must never be told, even briefly, that it is a member.
            shell.SelectTab(ShellTab.Sessions);
            Pump();

            Assert.False(shell.Sessions.HasMemberNotice);
            Assert.DoesNotContain(Texts(window), t => t.Contains("member on", StringComparison.Ordinal));

            window.Close();
        });
    }

    [Fact]
    public async Task The_devices_page_says_the_role_the_admission_and_the_last_use()
    {
        await _avalonia.Run(() =>
        {
            var (shell, window) = OpenShell();
            var page = Devices(shell, canManage: true);
            Pump();

            var texts = Texts(window);
            Assert.Contains("Workshop laptop", texts);
            Assert.Contains("Owner", texts);
            Assert.Contains("Member", texts);
            Assert.Contains("paired with code", texts);
            Assert.Contains("vouched for by a device", texts);
            Assert.Contains(texts, t => t.Contains("active now", StringComparison.Ordinal));

            // An owner gets the buttons that manage other devices, and no "you can't" note.
            Assert.Contains("Make member", texts);
            Assert.Contains("Make owner", texts);
            Assert.Contains(page.PruneButtonLabel, texts);
            Assert.DoesNotContain(DevicesPageViewModel.OnlyOwnersNote, texts);

            window.Close();
        });
    }

    [Fact]
    public async Task The_devices_page_is_read_only_for_a_member()
    {
        await _avalonia.Run(() =>
        {
            var (shell, window) = OpenShell();
            var page = Devices(shell, canManage: false);
            Pump();

            var texts = Texts(window);
            Assert.Contains("Workshop laptop", texts);
            Assert.Contains("Owner", texts);

            Assert.DoesNotContain("Make member", texts);
            Assert.DoesNotContain("Make owner", texts);
            Assert.DoesNotContain(page.PruneButtonLabel, texts);
            Assert.Contains(DevicesPageViewModel.OnlyOwnersNote, texts);

            window.Close();
        });
    }

    [Fact]
    public void The_only_owner_left_cannot_be_demoted()
    {
        var only = new MobileDeviceRow(
            new DeviceInfo("d1", "Workshop laptop", Now, Now, null, false, DeviceRole.Owner, "pairing"),
            Now, isLastOwner: true);

        Assert.False(only.CanChangeRole);
        Assert.Equal("Make member", only.RoleActionLabel);

        var member = new MobileDeviceRow(
            new DeviceInfo("d2", "Ada's phone", Now, Now, null, false, DeviceRole.Member, "approval"),
            Now, isLastOwner: true);

        Assert.True(member.CanChangeRole);
        Assert.Equal("Make owner", member.RoleActionLabel);
    }

    /// <summary>Puts one device asking to join on a host whose role we control, the way the preview does —
    /// the simulated host can't produce a pending request.</summary>
    private static void Waiting(ShellViewModel shell, DeviceRole hostRole)
    {
        var link = shell.Hosts.Add(new SavedHost("workshop", "https://workshop:5099", "tok", null, hostRole));

        // Select the tab and let its own refresh run first: it clears the list, and would wipe an
        // injected row that arrived before it.
        shell.SelectTab(ShellTab.Inbox);
        Pump();
        shell.Inbox.PendingDevices.Add(new PendingDeviceRow(
            link,
            new PendingPairApproval("req-1", "Ada's laptop", "418302", Now, Now.AddMinutes(10))));
        Pump();
    }

    /// <summary>Opens the Devices page with a mixed list already in it. Loaded directly rather than over a
    /// host: what is under test is which controls each role is shown, not the fetch.</summary>
    private static DevicesPageViewModel Devices(ShellViewModel shell, bool canManage)
    {
        var page = new DevicesPageViewModel(shell)
        {
            IsRoleKnown = true,
            CanManage = canManage,
            Status = "2 paired with workshop.",
        };

        page.Devices.Clear();
        page.Devices.Add(new MobileDeviceRow(
            new DeviceInfo("d1", "Workshop laptop", Now.AddDays(-40), DateTimeOffset.UtcNow, null, false,
                DeviceRole.Owner, "pairing"), DateTimeOffset.UtcNow, isLastOwner: false));
        page.Devices.Add(new MobileDeviceRow(
            new DeviceInfo("d2", "Ada's phone", Now.AddDays(-3), Now.AddDays(-2), null, true,
                DeviceRole.Member, "approval"), Now, isLastOwner: false));

        shell.Push(page);
        return page;
    }

    /// <summary>Writes a paired host to the device's own store before the shell reads it — the state a
    /// relaunch actually starts in, and the one where the remembered role has to be usable before any
    /// host has answered.</summary>
    private static void Remember(DeviceRole? role)
        => HostRegistry.Save([new SavedHost("workshop", "https://workshop:5099", "tok", null, role)]);

    private static (ShellViewModel Shell, Window Window) OpenShell()
    {
        var shell = new ShellViewModel(
            new MobileConnector(), new MobileDispatcher(), new MobileSettings(), "Role test");

        var window = new Window
        {
            Width = 412,
            Height = 915,
            Content = new ShellView { DataContext = shell },
        };
        window.Show();
        Pump();
        return (shell, window);
    }

    private static IReadOnlyList<string> Texts(Visual root)
        => [.. root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? string.Empty)];

    private static void Pump()
    {
        for (var n = 0; n < 20; n++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }
}
