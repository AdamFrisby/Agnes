using System.Reflection;
using System.Runtime.Loader;
using Agnes.Abstractions;

namespace Agnes.Host.Plugins;

/// <summary>
/// A collectible, per-plugin <see cref="AssemblyLoadContext"/> — the default isolation tier (AC8/AC9):
/// enabling/disabling/updating a plugin doesn't require restarting <c>Agnes.Host</c>, and one plugin's
/// dependency versions can't collide with another's or with Agnes's own. Resolves the plugin's own
/// dependency assemblies from its extracted package directory via <see cref="AssemblyDependencyResolver"/>;
/// anything it can't resolve there falls through to the default context (so it shares Agnes's own
/// framework/BCL assemblies rather than loading a second copy).
///
/// Critically, the plugin-contract assemblies — <c>Agnes.Abstractions</c> (so an <c>IAgentAdapter</c>
/// the plugin registers is type-identical to the one <c>SessionManager</c> checks for) and
/// <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> (so the <c>IServiceCollection</c> the
/// host hands the plugin's <c>ConfigureServices</c> is the same type the plugin's own compiled code
/// resolves against) — are ALWAYS forced to resolve from the default context instead of a
/// plugin-private copy. Loading a second copy of either would silently break type identity between
/// the host and the plugin (service registrations that appear to "vanish" because they're keyed by a
/// different runtime type than the one the host is looking for).
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IAgentAdapter).Assembly.GetName().Name!,
        typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.GetName().Name!,
    };

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
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            return null; // fall through to the default context — never isolate the plugin-contract assemblies
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
