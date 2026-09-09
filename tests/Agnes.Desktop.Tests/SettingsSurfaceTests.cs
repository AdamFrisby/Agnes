using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.App.Desktop.Keymaps;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The settings surfaces answering the questions people bring to them: which paired device is stale (and which
/// one am I on), whether a curated MCP preset is already installed, and what the keyboard page actually knows.
/// </summary>
public class SettingsSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static DeviceInfo Device(DateTimeOffset? lastSeen, bool isCurrent = false)
        => new("d1", "laptop", Now.AddDays(-40), lastSeen, "pairing", isCurrent);

    private static string Detail(DeviceInfo info, DateTimeOffset now) => new DeviceRowVm(info, now).Detail;

    [Theory]
    [InlineData(0, "active now")]
    [InlineData(20, "20 min ago")]
    public void A_recently_used_device_reads_as_recently_used(int minutesAgo, string expected)
        => Assert.Contains(expected, Detail(Device(Now.AddMinutes(-minutesAgo)), Now), StringComparison.Ordinal);

    [Fact]
    public void A_device_that_has_not_connected_in_days_says_how_long()
        => Assert.Contains("3d ago", Detail(Device(Now.AddDays(-3)), Now), StringComparison.Ordinal);

    [Fact]
    public void A_device_last_seen_months_ago_falls_back_to_a_date()
        => Assert.Contains("2026-01", Detail(Device(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)), Now), StringComparison.Ordinal);

    [Fact]
    public void A_device_that_never_connected_says_so_and_not_just_its_pairing_date()
    {
        var detail = Detail(Device(lastSeen: null), Now);
        Assert.Contains("never connected", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Revoking_takes_two_clicks_and_the_label_says_which_one_you_are_on()
    {
        var row = new DeviceRowVm(Device(Now), Now);
        Assert.Equal("Revoke", row.RevokeLabel);

        row.IsConfirmingRevoke = true;

        Assert.Equal("Really revoke?", row.RevokeLabel);
    }

    [Fact]
    public void Arming_the_device_you_are_using_warns_that_it_signs_you_out()
    {
        var row = new DeviceRowVm(Device(Now, isCurrent: true), Now) { IsConfirmingRevoke = true };

        Assert.True(row.IsCurrentDevice);
        Assert.Equal("Sign this device out?", row.RevokeLabel);
    }

    [Fact]
    public void An_installed_preset_offers_nothing_further()
    {
        var preset = new McpServerInfo("", "playwright", "host", true, "stdio", "npx", ["-y", "@playwright/mcp"], new Dictionary<string, string>(), null, null);

        var fresh = new McpPresetRowVm(preset, isInstalled: false);
        Assert.Equal("Install", fresh.ActionLabel);
        Assert.True(fresh.CanInstall);
        Assert.Equal("npx -y @playwright/mcp", fresh.Command);

        var already = new McpPresetRowVm(preset, isInstalled: true);
        Assert.Equal("Installed", already.ActionLabel);
        Assert.False(already.CanInstall);
    }

    [Fact]
    public void An_http_preset_shows_its_url_rather_than_an_empty_command()
    {
        var preset = new McpServerInfo("", "remote", "host", true, "http", null, [], new Dictionary<string, string>(), "https://server/mcp", null);

        Assert.Equal("https://server/mcp", new McpPresetRowVm(preset, isInstalled: false).Command);
    }

    [Fact]
    public void A_hosts_endpoint_carries_the_pin_its_hub_connection_uses()
    {
        // The settings REST calls and the hub have to make the same trust decision. Reading the pin off the
        // live connection is what stops them disagreeing — a settings page that builds a default client
        // against a self-signed host fails every call while the session beside it is perfectly healthy.
        const string pin = "aa11bb22cc33dd44ee55ff6600112233445566778899aabbccddeeff00112233";
        var endpoint = new HostEndpoint("https://box:5099", "token", pin);

        Assert.Same(
            Agnes.Client.AgnesHttp.HandlerFor(pin),
            Agnes.Client.AgnesHttp.HandlerFor(endpoint.Fingerprint));
        Assert.NotSame(
            Agnes.Client.AgnesHttp.HandlerFor(pin),
            Agnes.Client.AgnesHttp.HandlerFor(null));
    }

    [Fact]
    public void An_unpinned_host_still_gets_an_ordinary_client()
    {
        var endpoint = new HostEndpoint("https://box:5099", "token", null);

        Assert.Same(Agnes.Client.AgnesHttp.HandlerFor(null), Agnes.Client.AgnesHttp.HandlerFor(endpoint.Fingerprint));
        Assert.NotNull(endpoint.Http);
    }

    private static MainWindowViewModel NewVm()
    {
        var id = Guid.NewGuid().ToString("n");
        return new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-tabs-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-arch-{id}.json")));
    }

    private static ProjectDto Project(string id, string name) => new(
        id, name, $"repo/{id}",
        new SandboxImageDto("images:ubuntu/24.04/cloud", $"agnes-{id}", true, [], [], [], []),
        [], null, new ProjectDefaultsDto());

    [Fact]
    public void Switching_project_with_unsaved_edits_warns_before_discarding_them()
    {
        var vm = NewVm();
        var first = Project("a", "App");
        var second = Project("b", "Docs");
        vm.SelectProjectCommand.Execute(first);

        vm.ProjApt = "ripgrep";                                  // an edit the host doesn't have yet
        Assert.True(vm.IsProjectDirty);

        vm.SelectProjectCommand.Execute(second);

        // Still on the edited project, with the edit intact and an explanation of what would be lost.
        Assert.Equal("a", vm.SelectedProject?.Id);
        Assert.Equal("ripgrep", vm.ProjApt);
        Assert.Contains("unsaved changes", vm.ProjectsStatus, StringComparison.OrdinalIgnoreCase);

        vm.SelectProjectCommand.Execute(second);                 // second click means it

        Assert.Equal("b", vm.SelectedProject?.Id);
        Assert.Equal(string.Empty, vm.ProjApt);
    }

    [Fact]
    public void Switching_project_with_nothing_unsaved_just_switches()
    {
        var vm = NewVm();
        vm.SelectProjectCommand.Execute(Project("a", "App"));

        vm.SelectProjectCommand.Execute(Project("b", "Docs"));

        Assert.Equal("b", vm.SelectedProject?.Id);
        Assert.False(vm.IsProjectDirty);
    }

    [Fact]
    public void A_reload_does_not_overwrite_the_project_you_are_editing()
    {
        var vm = NewVm();
        vm.SelectProjectCommand.Execute(Project("a", "App"));
        vm.ProjNpm = "typescript";

        // What LoadProjectsAsync does on a refresh: re-select the same project from the host's copy.
        vm.SelectProjectCommand.Execute(Project("a", "App"));

        Assert.Equal("typescript", vm.ProjNpm);
    }

    [Fact]
    public void Deleting_a_sandbox_takes_two_clicks_and_says_so()
    {
        var row = new SandboxRowVm(new SandboxRecordDto(
            "s1", "agnes-vm-1", "incus", "claude-code", "/srv/app", null, "refactor", "stopped",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false));

        Assert.Equal("Delete", row.DeleteLabel);
        row.IsConfirmingDelete = true;
        Assert.Equal("Delete for good?", row.DeleteLabel);
    }

    [Fact]
    public void Every_keymap_command_has_discoverable_typed_metadata()
    {
        Assert.Equal(Enum.GetValues<AgnesCommand>().Length, CommandCatalogue.All.Count);
        Assert.All(CommandCatalogue.All, command =>
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Id));
            Assert.False(string.IsNullOrWhiteSpace(command.Description));
            Assert.NotEmpty(command.Contexts);
        });
    }
}
