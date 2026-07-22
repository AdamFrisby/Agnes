using System.Reflection;
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
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            return null; // fall through to the default context — never isolate the client-plugin contract
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
            try
            {
                var assembly = new ClientPluginLoadContext(dll).LoadMainAssembly();
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
