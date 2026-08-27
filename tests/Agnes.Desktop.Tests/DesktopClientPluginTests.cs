using System.Runtime.Loader;
using System.Reflection;
using Agnes.App.Desktop.Plugins;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core.Plugins;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The desktop head loads client plugins dynamically (its own AssemblyLoadContext) in addition to its
/// built-in ones, then advertises the combined capabilities during negotiation
/// (see .ideas/00c-client-plugins-and-negotiation.md). Exercises the real compiled
/// Agnes.TestClientPluginFixture assembly, loaded from disk — not a fabricated module.
/// </summary>
public class DesktopClientPluginTests
{
    private sealed class NullNoteNotifier : INotifier
    {
        public void Notify(AppNotification notification) { }
    }

    /// <summary>Copies just the fixture DLL into a fresh temp directory, so the loader scans only the
    /// plugin (not Agnes.Ui.Core et al. sitting beside it in the fixture's own output).</summary>
    private static string FixtureDirectory()
    {
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // .../bin/<Config>/net10.0/
        var testsDir = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.Parent!; // -> tests/
        var fixtureDll = Path.Combine(testsDir.FullName, "Agnes.TestClientPluginFixture", "bin", config, "net10.0", "Agnes.TestClientPluginFixture.dll");
        Assert.True(File.Exists(fixtureDll), $"fixture not built at {fixtureDll}");

        var dir = Path.Combine(Path.GetTempPath(), "agnes-clientplug-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.Copy(fixtureDll, Path.Combine(dir, Path.GetFileName(fixtureDll)));
        return dir;
    }

    [Fact]
    public void Loads_a_dynamic_client_plugin_alongside_the_built_in_one()
    {
        var dir = FixtureDirectory();
        try
        {
            var plugins = DesktopClientPlugins.Build(new NullNoteNotifier(), dir);

            // Built-in desktop channel + the dynamically-loaded one from the fixture assembly.
            Assert.NotNull(plugins.NotificationChannels.Find("desktop-toast"));
            var dynamic = plugins.NotificationChannels.Find("test-dynamic-channel");
            Assert.NotNull(dynamic);

            // The dynamically-loaded channel actually runs across the ALC boundary (shared Agnes.Ui.Core types).
            dynamic!.Show(new AppNotification("Hello from a plugin", "body", NotificationKind.Completion, "s1"));
            var sinkTitles = ReadFixtureSink(dynamic); // read the Sink from the channel's OWN loaded assembly
            Assert.Contains("Hello from a plugin", sinkTitles);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Advertised_capabilities_negotiate_against_a_host()
    {
        var dir = FixtureDirectory();
        try
        {
            var plugins = DesktopClientPlugins.Build(new NullNoteNotifier(), dir);
            var caps = DesktopClientPlugins.Capabilities("desktop-1", plugins);

            Assert.True(caps.SupportsDynamicPlugins);
            Assert.Equal("desktop", caps.Platform);
            Assert.Contains(ClientCapabilityIds.Notifications, caps.CapabilityIds);

            // Negotiate against the simulated host (which advertises agent-adapter + plugin-management,
            // but no notification trigger) → notifications is client-only, not usable end to end yet.
            var host = new SimulatedHost();
            var negotiated = await host.NegotiateAsync(caps);
            Assert.Equal(CapabilitySupport.ClientOnly,
                negotiated.Capabilities.Single(c => c.Id == ClientCapabilityIds.Notifications).Support);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Reads the fixture's static Sink from the SAME assembly instance the channel was loaded into (its own
    // AssemblyLoadContext), not a second copy — otherwise the statics wouldn't match.
    private static IReadOnlyList<string> ReadFixtureSink(IClientNotificationChannel channel)
    {
        var sink = channel.GetType().Assembly.GetType("Agnes.TestClientPluginFixture.Sink")!;
        return (IReadOnlyList<string>)sink.GetMethod("Titles")!.Invoke(null, null)!;
    }

    [Fact]
    public void Only_assemblies_that_reference_the_contract_get_a_load_context()
    {
        // A plugin folder is mostly the plugin's dependencies — the CodeyBox one ships twenty files, of
        // which exactly one is a plugin. Giving each its own context meant loading nineteen framework
        // assemblies for nothing, and those contexts then became garbage while the real plugin still
        // needed the files they held: a dependency loaded later hit one mid-unload and failed with
        // "AssemblyLoadContext is unloading or was already unloaded", reported as a FileLoadException for
        // an assembly plainly present on disk.
        var dir = FixtureDirectory();
        try
        {
            // A dependency sitting beside the plugin, as one really would.
            File.Copy(
                typeof(Agnes.Protocol.SessionInfo).Assembly.Location,
                Path.Combine(dir, Path.GetFileName(typeof(Agnes.Protocol.SessionInfo).Assembly.Location)));

            var before = AssemblyLoadContext.All.Count();
            var modules = DesktopClientPluginLoader.LoadModules(dir);
            var created = AssemblyLoadContext.All.Count() - before;

            Assert.Single(modules);
            Assert.Equal(1, created); // the plugin only — not one per file in the folder
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_plugins_load_context_outlives_collection_so_late_dependencies_still_load()
    {
        // The context is collectible, so it lives exactly as long as something references it. What made
        // this hard to see is that everything already loaded kept working; only the FIRST assembly the
        // plugin had not needed yet failed. For CodeyBox that was SignalR.Common, pulled in when a hub
        // connection is first opened — minutes after startup, on a click.
        var dir = FixtureDirectory();
        try
        {
            var modules = DesktopClientPluginLoader.LoadModules(dir);
            var context = AssemblyLoadContext.GetLoadContext(Assert.Single(modules).GetType().Assembly);
            Assert.NotNull(context);

            var weak = new WeakReference(context);
            context = null;
            for (var i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // The loader roots it for the session; without that the only thing keeping it alive would be
            // whatever the caller happened to hold, which is not a lifetime anyone declared.
            Assert.True(weak.IsAlive, "the loader must keep a plugin's load context alive for the session");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
