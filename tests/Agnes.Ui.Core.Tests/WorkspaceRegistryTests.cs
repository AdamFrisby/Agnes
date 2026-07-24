using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The client side of the multi-machine workspace model (connectivity/05): the <see cref="WorkspaceRegistry"/>
/// unions checkouts across the multi-host pool and groups them into logical workspaces by normalized repository
/// URL, and the new-session "which checkout" step surfaces a choice only when a workspace has more than one.
/// Exercised offline against two simultaneous <see cref="SimulatedHost"/>s.
/// </summary>
public class WorkspaceRegistryTests
{
    private const string Laptop = "sim://laptop";
    private const string Cloud = "sim://cloud";
    private const string RepoUrl = "https://github.com/acme/app.git";
    private const string OtherRepoUrl = "https://github.com/acme/other.git";

    private static async Task<(SimulatedHost Laptop, SimulatedHost Cloud)> ConnectPairAsync()
    {
        var laptop = new SimulatedHost(Laptop);
        var cloud = new SimulatedHost(Cloud);
        await laptop.ConnectAsync();
        await cloud.ConnectAsync();
        return (laptop, cloud);
    }

    [Fact]
    public async Task Groups_two_hosts_checkouts_of_the_same_repo_into_one_workspace()
    {
        var (laptop, cloud) = await ConnectPairAsync();
        // Same repository, checked out on both machines on different branches.
        laptop.SeedCheckout(RepoUrl, "/home/me/app", "feature/x");
        cloud.SeedCheckout(RepoUrl, "/srv/app", "main");

        var registry = new WorkspaceRegistry();
        await registry.RefreshAsync([laptop, cloud]);

        var workspace = Assert.Single(registry.Workspaces);
        Assert.Equal("github.com/acme/app", workspace.Workspace.Id);
        Assert.Equal(RepoUrl, workspace.Workspace.RepositoryUrl);

        // Both hosts' checkouts, each tagged with the right host id + branch.
        Assert.Equal(2, workspace.Checkouts.Count);
        var onLaptop = Assert.Single(workspace.Checkouts, c => c.HostId == Laptop);
        var onCloud = Assert.Single(workspace.Checkouts, c => c.HostId == Cloud);
        Assert.Equal("feature/x", onLaptop.Branch);
        Assert.Equal("main", onCloud.Branch);
        Assert.All(workspace.Checkouts, c => Assert.Equal(workspace.Workspace.Id, c.WorkspaceId));
    }

    [Fact]
    public async Task A_different_repo_url_is_a_separate_workspace()
    {
        var (laptop, cloud) = await ConnectPairAsync();
        laptop.SeedCheckout(RepoUrl, "/home/me/app", "main");
        cloud.SeedCheckout(OtherRepoUrl, "/srv/other", "main");

        var registry = new WorkspaceRegistry();
        await registry.RefreshAsync([laptop, cloud]);

        Assert.Equal(2, registry.Workspaces.Count);
        var app = registry.Find("github.com/acme/app");
        var other = registry.Find("github.com/acme/other");
        Assert.NotNull(app);
        Assert.NotNull(other);
        Assert.Equal(Laptop, Assert.Single(app!.Checkouts).HostId);
        Assert.Equal(Cloud, Assert.Single(other!.Checkouts).HostId);
    }

    [Fact]
    public async Task Workspace_with_multiple_checkouts_requires_a_new_session_checkout_choice()
    {
        var (laptop, cloud) = await ConnectPairAsync();
        laptop.SeedCheckout(RepoUrl, "/home/me/app", "feature/x");
        cloud.SeedCheckout(RepoUrl, "/srv/app", "main");

        var registry = new WorkspaceRegistry();
        await registry.RefreshAsync([laptop, cloud]);

        var launch = new WorkspaceLaunchViewModel(registry.Find("github.com/acme/app")!);

        // More than one checkout: the user must pick, nothing preselected.
        Assert.True(launch.RequiresCheckoutChoice);
        Assert.Equal(2, launch.Choices.Count);
        Assert.Null(launch.SelectedCheckout);
        Assert.False(launch.CanLaunch);

        // Choosing one enables launch.
        launch.SelectedCheckout = launch.Choices.First(c => c.HostId == Cloud);
        Assert.True(launch.CanLaunch);
        Assert.Equal("main", launch.SelectedCheckout!.Branch);
    }

    [Fact]
    public async Task Single_checkout_workspace_needs_no_extra_step()
    {
        var (laptop, _) = await ConnectPairAsync();
        laptop.SeedCheckout(RepoUrl, "/home/me/app", "main");

        var registry = new WorkspaceRegistry();
        await registry.RefreshAsync([laptop]);

        var launch = new WorkspaceLaunchViewModel(registry.Find("github.com/acme/app")!);

        // Exactly one checkout: preselected, no choice required, ready to launch.
        Assert.False(launch.RequiresCheckoutChoice);
        Assert.NotNull(launch.SelectedCheckout);
        Assert.Equal(Laptop, launch.SelectedCheckout!.HostId);
        Assert.True(launch.CanLaunch);
    }
}
