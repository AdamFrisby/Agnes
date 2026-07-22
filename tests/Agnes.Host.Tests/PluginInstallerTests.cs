using Agnes.Abstractions;
using Agnes.Host.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// End-to-end tests of <see cref="PluginInstaller"/> against a REAL compiled plugin assembly
/// (<c>Agnes.TestPluginFixture</c>) packed into a real .nupkg at test time (<see cref="PluginPackageFixture"/>)
/// — real extraction, real manifest parsing, real <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// loading, real capability-gated DI. Only the NuGet feed is faked (network access isn't appropriate for
/// a unit test); signature verification uses the real <see cref="NuGetSignatureVerifier"/> configured
/// permissively, since the test package is legitimately unsigned and a separate test below covers
/// rejection of unsigned packages specifically.
/// </summary>
public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _pluginsRoot = Path.Combine(Path.GetTempPath(), $"agnes-plugins-{Guid.NewGuid():n}");
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        if (Directory.Exists(_pluginsRoot))
        {
            try { Directory.Delete(_pluginsRoot, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakeFeed : INuGetPluginFeed
    {
        public Dictionary<string, byte[]> PackagesByVersion { get; } = [];
        public string LatestVersion { get; set; } = "1.0.0";

        public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PluginSearchResult>>(
                [new PluginSearchResult(PluginPackageFixture.PackageId, "Test Plugin", "A test fixture", "Agnes Test Fixtures", [LatestVersion], IsReviewed: false)]);

        public Task<IReadOnlyList<string>> ListVersionsAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(PackagesByVersion.Keys.OrderDescending().ToArray());

        public Task<NuGetPluginPackage> DownloadAsync(string packageId, string? version, CancellationToken cancellationToken = default)
        {
            var resolvedVersion = version ?? LatestVersion;
            return Task.FromResult(new NuGetPluginPackage(packageId, resolvedVersion, PackagesByVersion[resolvedVersion]));
        }
    }

    private sealed class FakeCredentialBroker : ICredentialBroker
    {
        public Task<string?> ResolveAsync(string host, CancellationToken cancellationToken = default) => Task.FromResult<string?>("fake-credential");
    }

    private static PluginCapabilityService[] CredentialCapabilityServices()
        =>
        [
            new PluginCapabilityService(PluginCapabilityIds.Credentials,
                (pluginServices, hostServices) => pluginServices.AddSingleton(hostServices.GetRequiredService<ICredentialBroker>())),
        ];

    private (PluginInstaller Installer, PluginRegistry<IAgentAdapter> Registry, FakeFeed Feed, string StateFile) CreateInstaller(bool allowUnsigned = true)
    {
        Directory.CreateDirectory(_pluginsRoot);
        var stateFile = Path.Combine(_pluginsRoot, "plugins.json");
        var feed = new FakeFeed();
        var registry = new PluginRegistry<IAgentAdapter>([], a => a.Descriptor.Id);
        var merger = new PluginPointMerger<IAgentAdapter>(registry, a => a.Descriptor.Id);

        var hostServices = new ServiceCollection().AddSingleton<ICredentialBroker, FakeCredentialBroker>().BuildServiceProvider();
        _disposables.Add(hostServices);

        var state = new PluginStateStore(stateFile);
        var verifier = new NuGetSignatureVerifier(allowUnsigned);
        var installer = new PluginInstaller(
            feed, verifier, state, _pluginsRoot, hostServices,
            [merger], CredentialCapabilityServices(), NullLogger<PluginInstaller>.Instance);

        return (installer, registry, feed, stateFile);
    }

    [Fact]
    public async Task Install_merges_the_plugins_adapter_into_the_shared_registry()
    {
        var (installer, registry, feed, _) = CreateInstaller();
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0");

        var installed = await installer.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: []);

        Assert.True(installed.Enabled);
        Assert.Equal("1.0.0", installed.Version);
        Assert.NotNull(registry.Find("test-plugin-adapter:no-credentials"));
    }

    [Fact]
    public async Task Install_with_a_granted_declared_capability_gives_the_plugin_real_access()
    {
        var (installer, registry, feed, _) = CreateInstaller();
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0", capabilities: [PluginCapabilityIds.Credentials]);

        var installed = await installer.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: [PluginCapabilityIds.Credentials]);

        Assert.Equal([PluginCapabilityIds.Credentials], installed.GrantedCapabilities);
        Assert.NotNull(registry.Find("test-plugin-adapter:has-credentials"));
    }

    [Fact]
    public async Task Install_refuses_without_consent_for_a_declared_capability()
    {
        var (installer, registry, feed, _) = CreateInstaller();
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0", capabilities: [PluginCapabilityIds.Credentials]);

        var ex = await Assert.ThrowsAsync<PluginConsentRequiredException>(
            () => installer.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: []));

        Assert.Equal(PluginPackageFixture.PackageId, ex.PluginId);
        Assert.Contains(PluginCapabilityIds.Credentials, ex.MissingCapabilities);
        Assert.Empty(registry.All); // nothing loaded on a rejected install
    }

    [Fact]
    public async Task AC11_a_capability_the_manifest_never_declared_is_never_granted_even_if_the_caller_passes_it()
    {
        // The manifest declares NO capabilities; the caller (perhaps buggy UI) grants "credentials"
        // anyway. AC11: a plugin that did not declare a capability cannot obtain it, full stop.
        var (installer, registry, feed, _) = CreateInstaller();
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0", capabilities: []);

        var installed = await installer.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: [PluginCapabilityIds.Credentials]);

        Assert.Empty(installed.GrantedCapabilities); // the effective grant is what the manifest declared — nothing
        Assert.NotNull(registry.Find("test-plugin-adapter:no-credentials"));
        Assert.Null(registry.Find("test-plugin-adapter:has-credentials"));
    }

    [Fact]
    public async Task Disable_removes_the_plugins_instances_and_enable_restores_them()
    {
        var (installer, registry, feed, _) = CreateInstaller();
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0");
        await installer.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: []);
        Assert.NotNull(registry.Find("test-plugin-adapter:no-credentials"));

        await installer.SetEnabledAsync(PluginPackageFixture.PackageId, enabled: false);
        Assert.Null(registry.Find("test-plugin-adapter:no-credentials"));
        var listedDisabled = await installer.ListInstalledAsync();
        Assert.False(Assert.Single(listedDisabled).Enabled);

        await installer.SetEnabledAsync(PluginPackageFixture.PackageId, enabled: true);
        Assert.NotNull(registry.Find("test-plugin-adapter:no-credentials"));
        var listedEnabled = await installer.ListInstalledAsync();
        Assert.True(Assert.Single(listedEnabled).Enabled);
    }

    [Fact]
    public async Task Uninstall_removes_the_plugin_and_its_files()
    {
        var (installer, registry, feed, _) = CreateInstaller();
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0");
        await installer.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: []);
        var extractedDir = Path.Combine(_pluginsRoot, PluginPackageFixture.PackageId, "1.0.0");
        Assert.True(Directory.Exists(extractedDir));

        await installer.UninstallAsync(PluginPackageFixture.PackageId);

        Assert.Null(registry.Find("test-plugin-adapter:no-credentials"));
        Assert.Empty(await installer.ListInstalledAsync());
        Assert.False(Directory.Exists(extractedDir));
    }

    [Fact]
    public async Task Configure_persists_settings_and_reloads_the_plugin_with_them()
    {
        var (installer, registry, feed, _) = CreateInstaller();
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0");
        await installer.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: []);
        Assert.NotNull(registry.Find("test-plugin-adapter:no-credentials"));

        await installer.ConfigureAsync(PluginPackageFixture.PackageId, new Dictionary<string, string> { ["id"] = "renamed" });

        Assert.Null(registry.Find("test-plugin-adapter:no-credentials"));
        Assert.NotNull(registry.Find("renamed:no-credentials"));
    }

    [Fact]
    public async Task Update_to_a_version_with_a_new_undeclared_before_capability_requires_fresh_consent()
    {
        var (installer, registry, feed, _) = CreateInstaller();
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0", capabilities: []);
        feed.PackagesByVersion["2.0.0"] = PluginPackageFixture.Build("2.0.0", capabilities: [PluginCapabilityIds.Credentials]);
        feed.LatestVersion = "2.0.0";
        await installer.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: []);

        await Assert.ThrowsAsync<PluginConsentRequiredException>(
            () => installer.UpdateAsync(PluginPackageFixture.PackageId, grantedCapabilities: []));

        // The old version keeps running — a rejected update never tears down what's already installed.
        Assert.NotNull(registry.Find("test-plugin-adapter:no-credentials"));
        var stillOld = Assert.Single(await installer.ListInstalledAsync());
        Assert.Equal("1.0.0", stillOld.Version);

        var updated = await installer.UpdateAsync(PluginPackageFixture.PackageId, grantedCapabilities: [PluginCapabilityIds.Credentials]);
        Assert.Equal("2.0.0", updated.Version);
        Assert.NotNull(registry.Find("test-plugin-adapter:has-credentials"));
    }

    [Fact]
    public async Task Restoring_from_state_reloads_a_previously_enabled_plugin_on_construction()
    {
        var (installer1, _, feed, stateFile) = CreateInstaller();
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0");
        await installer1.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: []);

        // Simulate a host restart: a brand new PluginInstaller over the same state file + plugins dir,
        // with a fresh (empty) registry — exactly what happens when Agnes.Host restarts.
        var freshRegistry = new PluginRegistry<IAgentAdapter>([], a => a.Descriptor.Id);
        var freshMerger = new PluginPointMerger<IAgentAdapter>(freshRegistry, a => a.Descriptor.Id);
        var hostServices = new ServiceCollection().AddSingleton<ICredentialBroker, FakeCredentialBroker>().BuildServiceProvider();
        _disposables.Add(hostServices);
        var state2 = new PluginStateStore(stateFile);
        _ = new PluginInstaller(feed, new NuGetSignatureVerifier(allowUnsigned: true), state2, _pluginsRoot, hostServices,
            [freshMerger], CredentialCapabilityServices(), NullLogger<PluginInstaller>.Instance);

        Assert.NotNull(freshRegistry.Find("test-plugin-adapter:no-credentials"));
    }

    [Fact]
    public async Task Unsigned_packages_are_refused_by_default()
    {
        var (installer, registry, feed, _) = CreateInstaller(allowUnsigned: false);
        feed.PackagesByVersion["1.0.0"] = PluginPackageFixture.Build("1.0.0");

        var ex = await Assert.ThrowsAsync<PluginInstallException>(
            () => installer.InstallAsync(PluginPackageFixture.PackageId, "1.0.0", grantedCapabilities: []));

        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(registry.All);
    }

    [Fact]
    public async Task Search_delegates_to_the_feed()
    {
        var (installer, _, _, _) = CreateInstaller();

        var results = await installer.SearchAsync("test");

        Assert.Contains(results, r => r.PackageId == PluginPackageFixture.PackageId);
    }
}
