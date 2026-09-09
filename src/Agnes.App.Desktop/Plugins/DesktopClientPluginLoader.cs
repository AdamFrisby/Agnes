using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Agnes.Ui.Core.Plugins;

namespace Agnes.App.Desktop.Plugins;

/// <summary>
/// A collectible <see cref="AssemblyLoadContext"/> for a dynamically-loaded client plugin — the client-side
/// analogue of the host's <c>PluginLoadContext</c>. The client-plugin contract assembly
/// (<c>Agnes.Ui.Core</c>, which defines <see cref="IClientPluginModule"/>/<see cref="IClientNotificationChannel"/>
/// and <c>AppNotification</c>) is always forced to resolve from the default context, so a module the plugin
/// registers is type-identical to what the app looks for; everything else resolves from the plugin's own
/// directory.
/// </summary>
public sealed class ClientPluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IClientPluginModule).Assembly.GetName().Name!,
    };

    /// <summary>
    /// Assembly-name prefixes also forced to the default context, because their types cross the plugin
    /// boundary and so must be the <i>same</i> types on both sides.
    /// </summary>
    /// <remarks>
    /// <para><c>Avalonia</c>: a plugin that supplies a view (<see cref="Agnes.Ui.Core.Plugins.IViewFactory"/>)
    /// hands the head a real control. Resolved from the plugin's own directory that control would be a
    /// different type than the one the head renders, with the framework's static state duplicated behind
    /// it. Sharing used to happen only when a plugin's build did not copy Avalonia beside its DLL, which
    /// made whether a plugin rendered a property of its csproj rather than of the contract.</para>
    ///
    /// <para><c>Agnes.</c>: naming the contract assembly alone was not enough, because the contract does not
    /// live in one assembly. <c>ClientPlugins.cs</c> spans three — <c>IPluginRegistry</c>, <c>IEventBus</c>,
    /// <c>IEventBinding</c> and <c>IVoiceProvider</c> come from <c>Agnes.Abstractions</c>, and
    /// <c>ClientCapabilities</c> from <c>Agnes.Protocol</c> — so a plugin registering an event binding or a
    /// voice provider is handling types from assemblies that were <i>not</i> shared. Carrying its own copy
    /// would satisfy the compiler and then fail to match at runtime. Agnes's own assemblies are never a
    /// plugin's to version, so a copy in the plugin folder is simply ignored.</para>
    /// </remarks>
    private static readonly string[] SharedAssemblyPrefixes = ["Avalonia", "Agnes."];

    private readonly AssemblyDependencyResolver _resolver;

    public ClientPluginLoadContext(string mainAssemblyPath)
        : base(name: $"agnes-client-plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        MainAssemblyPath = mainAssemblyPath;
    }

    public string MainAssemblyPath { get; }

    public Assembly LoadMainAssembly() => LoadFromAssemblyPath(MainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name &&
            (SharedAssemblyNames.Contains(name) ||
             SharedAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
        {
            return null; // fall through to the default context — never isolate the contract or the UI framework
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }
}

/// <summary>
/// Loads dynamic client plugins from a directory of <c>*.dll</c>s, each into its own
/// <see cref="ClientPluginLoadContext"/> — the runtime-loading source of client plugins on the desktop
/// head (iOS/WASM heads never reference this type; they use compile-time modules only). Returns the
/// <see cref="IClientPluginModule"/>s discovered, to be combined with the app's built-in modules and
/// handed to <see cref="ClientPluginHost.FromModules"/>.
/// </summary>
public static class DesktopClientPluginLoader
{
    /// <summary>
    /// Every load context handed out, kept for the life of the process.
    /// </summary>
    /// <remarks>
    /// A <see cref="ClientPluginLoadContext"/> is collectible, so it lives exactly as long as something
    /// references it. Dropping the context after loading left it eligible for collection immediately.
    /// Everything already loaded kept working — the module, its view models, its views — and then the
    /// <b>first</b> assembly the plugin had not yet needed failed to load. For the CodeyBox plugin that was
    /// SignalR.Common, pulled in only when a hub connection is first opened, so it surfaced minutes after
    /// startup on a click and read as a networking fault rather than a loader one.
    ///
    /// <para>Client plugins live for the whole session, so rooting them here is the correct lifetime.
    /// Collectible remains right for the contexts themselves: it costs nothing while referenced, and keeps
    /// unloading possible if plugins ever become reloadable.</para>
    /// </remarks>
    private static readonly List<ClientPluginLoadContext> Contexts = [];

    /// <summary>
    /// Whether an assembly could define a plugin module at all, decided from its metadata without loading
    /// it: it must reference the contract assembly.
    /// </summary>
    /// <remarks>
    /// A plugin folder is mostly the plugin's dependencies — the CodeyBox one ships twenty files, exactly
    /// one of which is a plugin. Giving each its own context loaded nineteen framework assemblies for
    /// nothing, and those contexts then became garbage while the real plugin still needed the files they
    /// held: a dependency resolved later hit one mid-unload and failed with "AssemblyLoadContext is
    /// unloading or was already unloaded".
    ///
    /// <para>Reading the reference table is exact rather than heuristic and loads nothing: an assembly that
    /// cannot name the contract cannot implement it.</para>
    /// </remarks>
    private static bool CanContainModule(string dll)
    {
        try
        {
            using var stream = File.OpenRead(dll);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return false;
            }

            var reader = pe.GetMetadataReader();
            var contract = typeof(IClientPluginModule).Assembly.GetName().Name;
            foreach (var handle in reader.AssemblyReferences)
            {
                if (reader.GetString(reader.GetAssemblyReference(handle).Name) == contract)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            // Not a managed assembly, or unreadable. Either way it is not a plugin.
            return false;
        }
    }

    /// <summary>Discovers and instantiates every <see cref="IClientPluginModule"/> in the assemblies under
    /// <paramref name="pluginDirectory"/>. A directory that doesn't exist yields no modules. An assembly
    /// that can't be loaded or scanned is skipped (never aborts loading the rest).</summary>
    public static IReadOnlyList<IClientPluginModule> LoadModules(string pluginDirectory, Action<string, Exception>? onError = null)
    {
        if (!Directory.Exists(pluginDirectory))
        {
            return [];
        }

        var modules = new List<IClientPluginModule>();
        foreach (var dll in Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (!CanContainModule(dll))
            {
                continue;
            }

            try
            {
                var context = new ClientPluginLoadContext(dll);
                lock (Contexts)
                {
                    // Rooted before the assembly is touched, so a plugin's lazily-loaded dependencies
                    // resolve through a live context for as long as the plugin itself lives.
                    Contexts.Add(context);
                }

                var assembly = context.LoadMainAssembly();
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(IClientPluginModule).IsAssignableFrom(type) && type is { IsAbstract: false, IsClass: true })
                    {
                        if (Activator.CreateInstance(type) is IClientPluginModule module)
                        {
                            modules.Add(module);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(dll, ex);
            }
        }

        return modules;
    }
}
