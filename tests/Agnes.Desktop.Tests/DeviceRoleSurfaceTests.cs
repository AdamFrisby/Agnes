using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.App.Desktop.Views;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The desktop's side of device roles: an owner is offered the controls that manage other devices, a
/// member is offered none of them and told why, and a member with nothing to see is given a sentence
/// instead of an empty screen.
///
/// The renders assert on presence and visibility rather than on clicking anything — this harness can't
/// drive commands through a settings data context (see <see cref="LocalModelsSettingsTests"/>) — but
/// presence is the property that actually regressed: a member with an owner's buttons gets silent 403s,
/// and an owner without them can't fix anybody.
/// </summary>
[Collection("Avalonia headless")]
public class DeviceRoleSurfaceTests
{
    private sealed class TestApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia();
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static MainWindowViewModel NewVm()
    {
        var id = Guid.NewGuid().ToString("n");
        return new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-tabs-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-arch-{id}.json")));
    }

    /// <summary>
    /// Shows a window and forces the layout pass that realizes item templates. <c>Show()</c> plus
    /// <c>RunJobs()</c> alone leaves an ItemsControl's rows unmaterialized in the headless harness, so a
    /// test looking for per-row controls finds none and reads as a missing feature rather than a missing
    /// layout.
    /// </summary>
    private static void Realize(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Fill(MainWindowViewModel vm, bool asOwner)
    {
        vm.Devices.Clear();
        vm.Devices.Add(new DeviceRowVm(
            new DeviceInfo("d1", "Workshop laptop", Now.AddDays(-40), Now.AddMinutes(-1), null, asOwner,
                DeviceRole.Owner, "pairing"), Now)
        { IsLastOwner = true });
        vm.Devices.Add(new DeviceRowVm(
            new DeviceInfo("d2", "Ada's phone", Now.AddDays(-3), Now.AddDays(-2), null, !asOwner,
                DeviceRole.Member, "approval"), Now));

        vm.IsHostRoleKnown = true;
        vm.IsHostOwner = asOwner;
    }

    [Fact]
    public void A_row_says_the_role_and_how_the_device_got_in()
    {
        var row = new DeviceRowVm(
            new DeviceInfo("d1", "Workshop laptop", Now.AddDays(-40), Now.AddMinutes(-1), null, false,
                DeviceRole.Owner, "approval"), Now);

        Assert.Equal("Owner", row.RoleChip);
        Assert.Equal("vouched for by a device", row.Admission);
        Assert.Contains("active now", row.Detail, StringComparison.Ordinal);
        Assert.Equal($"vouched for by a device · {row.Detail}", row.DetailLine);
        Assert.Equal("Make member", row.RoleActionLabel);
        Assert.Equal(DeviceRole.Member, row.TargetRole);
    }

    [Fact]
    public void The_last_owner_cannot_be_demoted_and_the_button_says_why()
    {
        var only = new DeviceRowVm(
            new DeviceInfo("d1", "Workshop laptop", Now, Now, null, false, DeviceRole.Owner, "pairing"), Now)
        { IsLastOwner = true };

        Assert.False(only.CanChangeRole);
        Assert.Contains("at least one owner", only.RoleActionTooltip, StringComparison.OrdinalIgnoreCase);

        var member = new DeviceRowVm(
            new DeviceInfo("d2", "Ada's phone", Now, Now, null, false, DeviceRole.Member, "pairing"), Now)
        { IsLastOwner = true };

        // "Last owner" is about demotion; promoting a member is never blocked by it.
        Assert.True(member.CanChangeRole);
    }

    [Fact]
    public void An_owner_can_manage_and_a_member_is_told_it_cannot()
    {
        var owner = NewVm();
        Fill(owner, asOwner: true);
        Assert.True(owner.CanManageDevices);
        Assert.False(owner.ShowOnlyOwnersNote);

        var member = NewVm();
        Fill(member, asOwner: false);
        Assert.False(member.CanManageDevices);
        Assert.True(member.ShowOnlyOwnersNote);
        Assert.Equal("Only an owner can change roles.", MainWindowViewModel.OnlyOwnersNote);
    }

    [Fact]
    public void Nothing_is_claimed_before_the_host_has_answered()
    {
        var vm = NewVm();

        // A host too old to answer /devices/me leaves the page exactly as it was before roles existed:
        // no owner buttons, and no line telling a possible owner it is only a member.
        Assert.False(vm.IsHostRoleKnown);
        Assert.False(vm.CanManageDevices);
        Assert.False(vm.ShowOnlyOwnersNote);
    }

    /// <remarks>
    /// Scope note — the same one <see cref="LocalModelsSettingsTests"/> makes, verified again here. This
    /// harness attaches the Settings view but does not drive its data context: its <c>ItemsControl</c>
    /// rows never materialize and its <c>IsVisible</c> bindings never evaluate, so a visibility assertion
    /// on this page passes or fails for a reason it doesn't name. What the render honestly proves is that
    /// the Devices page — chip, admission line, role button, prune button, the members' note — parses and
    /// attaches. Which control each role actually gets is asserted on the view model above, where it lives.
    /// </remarks>
    [Fact]
    public async Task The_devices_page_attaches_with_the_role_controls_present()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var vm = NewVm();
            // Category first: selecting it kicks off a (hostless, so emptying) load, which would wipe the
            // rows if they were staged before it.
            vm.SettingsCategory = "devices";
            Fill(vm, asOwner: true);

            var window = new Window { Width = 1280, Height = 900, Content = new SettingsTabView { DataContext = vm } };
            Realize(window);

            Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "PruneDevicesButton");
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "OnlyOwnersNote");

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_member_tab_explains_the_empty_session_list()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var vm = NewVm();
            var doc = new SessionDocument(new NullTabController(), ImmediateDispatcher.Instance)
            {
                HostName = "workshop",
                Stage = TabStage.PickAgent,
                HostRoleNotice = DeviceRoleText.EmptyStateForMember("workshop"),
            };

            var window = new Window { Width = 1280, Height = 900, Content = new SessionTabView { DataContext = doc } };
            Realize(window);

            var notice = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MemberRoleNotice");
            Assert.True(notice.IsVisible);
            Assert.Contains("member on workshop", doc.HostRoleNotice, StringComparison.Ordinal);
            Assert.Contains("Settings › Devices", doc.HostRoleNotice, StringComparison.Ordinal);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_owner_tab_shows_no_notice_at_all()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var doc = new SessionDocument(new NullTabController(), ImmediateDispatcher.Instance)
            {
                HostName = "workshop",
                Stage = TabStage.PickAgent,
            };

            var window = new Window { Width = 1280, Height = 900, Content = new SessionTabView { DataContext = doc } };
            Realize(window);

            Assert.False(doc.ShowHostRoleNotice);
            Assert.False(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MemberRoleNotice").IsVisible);

            window.Close();
        }, CancellationToken.None);
    }
}
