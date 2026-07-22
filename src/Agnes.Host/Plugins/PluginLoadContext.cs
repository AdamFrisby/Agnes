using System.Reflection;
using System.Runtime.Loader;

namespace Agnes.Host.Plugins;

/// <summary>
/// A collectible, per-plugin <see cref="AssemblyLoadContext"/> — the default isolation tier (AC8/AC9):
/// enabling/disabling/updating a plugin doesn't require restarting <c>Agnes.Host</c>, and one plugin's
/// dependency versions can't collide with another's or with Agnes's own. Resolves the plugin's own
/// dependency assemblies from its extracted package directory via <see cref="AssemblyDependencyResolver"/>;
/// anything it can't resolve there falls through to the default context (so it shares Agnes's own
/// framework/BCL assemblies rather than loading a second copy).
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginId, string mainAssemblyPath)
        : base(name: $"agnes-plugin:{pluginId}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        MainAssemblyPath = mainAssemblyPath;
    }

    public string MainAssemblyPath { get; }

    public Assembly LoadMainAssembly() => LoadFromAssemblyPath(MainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
