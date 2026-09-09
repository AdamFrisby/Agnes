using Agnes.Host.Sessions;
using Agnes.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class SandboxImageManagerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"agnes-img-{Guid.NewGuid():n}.json");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    internal sealed class FakeImageBuilder : ISandboxImageBuilder
    {
        public bool Exists;
        public int Builds;
        public SandboxImageManifest? LastManifest;

        public Task<bool> ImageExistsAsync(string alias, CancellationToken cancellationToken = default) => Task.FromResult(Exists);

        public Task BuildImageAsync(SandboxImageManifest manifest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Builds++;
            LastManifest = manifest;
            Exists = true;
            progress?.Report("built");
            return Task.CompletedTask;
        }
    }

    private SandboxImageManager Manager(FakeImageBuilder builder)
        => new(builder, _file, NullLogger<SandboxImageManager>.Instance);

    [Fact]
    public async Task Ensure_bakes_when_missing_then_not_again()
    {
        var builder = new FakeImageBuilder { Exists = false };
        var mgr = Manager(builder);

        await mgr.EnsureAsync();
        Assert.Equal(1, builder.Builds);
        Assert.Equal(SandboxImageState.Ready, mgr.Status.State);

        await mgr.EnsureAsync(); // image now exists → no rebuild
        Assert.Equal(1, builder.Builds);
    }

    [Fact]
    public async Task Ensure_for_project_bakes_the_projects_manifest_to_its_own_alias()
    {
        var builder = new FakeImageBuilder { Exists = false };
        var mgr = Manager(builder);
        var project = new Agnes.Host.Projects.Project
        {
            Id = "abc",
            Name = "Work",
            Sandbox = new SandboxImageManifest { Node = false, AptPackages = ["git"] },
        };

        var alias = await mgr.EnsureForProjectAsync(project);

        Assert.Equal("agnes-proj-abc", alias);
        Assert.Equal(1, builder.Builds);
        Assert.Equal("agnes-proj-abc", builder.LastManifest!.Alias);  // baked to the project's own alias
        Assert.False(builder.LastManifest.Node);                       // from the project's manifest
        Assert.Equal(SandboxImageState.Ready, mgr.StatusFor(alias).State);

        await mgr.EnsureForProjectAsync(project); // now exists → no rebuild
        Assert.Equal(1, builder.Builds);
    }

    /// <summary>
    /// A session with a screen must launch from an image that HAS a screen. The headless tiers carry no X
    /// server, so booting a graphical session from one produces a VM that renders nothing and a capture that
    /// waits forever for a first scanout — a failure with no error anywhere.
    /// </summary>
    [Fact]
    public async Task Ensure_graphical_bakes_the_desktop_tier_under_its_own_alias()
    {
        var builder = new FakeImageBuilder { Exists = false };
        var mgr = Manager(builder);

        var alias = await mgr.EnsureGraphicalAsync();

        Assert.Equal("agnes-graphical", alias);
        Assert.NotEqual(mgr.Alias, alias);                                 // never the headless baseline
        Assert.Equal(1, builder.Builds);
        Assert.Contains("xserver-xorg-core", builder.LastManifest!.AptPackages);
        Assert.Contains("openbox", builder.LastManifest.AptPackages);
        Assert.Contains("git", builder.LastManifest.AptPackages);           // …on top of the baseline's own
        Assert.Equal(SandboxImageState.Ready, mgr.StatusFor(alias).State);

        await mgr.EnsureGraphicalAsync(); // now exists → no rebuild
        Assert.Equal(1, builder.Builds);
    }

    [Fact]
    public async Task Ensure_skips_the_bake_when_the_image_is_present()
    {
        var builder = new FakeImageBuilder { Exists = true };
        var mgr = Manager(builder);

        await mgr.EnsureAsync();
        Assert.Equal(0, builder.Builds);
        Assert.Equal(SandboxImageState.Ready, mgr.Status.State);
    }

    [Fact]
    public async Task Save_and_rebuild_persists_the_manifest_and_bakes()
    {
        var builder = new FakeImageBuilder { Exists = true };
        var mgr = Manager(builder);

        await mgr.SaveAndRebuildAsync(new SandboxImageManifest { NpmGlobals = ["a-server"] });

        Assert.Equal(1, builder.Builds);
        Assert.Contains("a-server", builder.LastManifest!.NpmGlobals);
        Assert.Contains("a-server", mgr.Manifest.NpmGlobals); // persisted + reloaded from disk
    }

    [Fact]
    public void Image_has_agent_reflects_the_manifest()
    {
        var mgr = Manager(new FakeImageBuilder());
        Assert.True(mgr.ImageHasAgent("claude-code-native")); // default manifest
        Assert.True(mgr.ImageHasAgent("codex"));
        Assert.True(mgr.ImageHasAgent("opencode"));
        Assert.False(mgr.ImageHasAgent("claude-code"));       // the node-based ACP bridge isn't baked by default
    }
}
