using Agnes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agnes.Host.Plugins;

/// <summary>DI helper for wiring a plugin point in one call instead of the repeated four-line quad.</summary>
public static class PluginPointRegistration
{
    /// <summary>
    /// Registers the standard registry triad for a plugin point <typeparamref name="T"/> — the concrete
    /// <see cref="PluginRegistry{T}"/> (built from everything <c>GetServices&lt;T&gt;()</c> yields, keyed by
    /// <paramref name="keySelector"/>), its read (<see cref="IPluginRegistry{T}"/>) and mutable
    /// (<see cref="IMutablePluginRegistry{T}"/>) facets, and an <see cref="IPluginPointMerger"/> so
    /// NuGet-installed plugins of this kind merge in at runtime. The concrete built-in implementations of
    /// <typeparamref name="T"/> are registered separately (<c>AddSingleton&lt;T, …&gt;()</c>); this only
    /// wires the merge machinery over them.
    /// </summary>
    public static IServiceCollection AddPluginPoint<T>(this IServiceCollection services, Func<T, string> keySelector)
        where T : class
    {
        services.AddSingleton(sp => new PluginRegistry<T>(sp.GetServices<T>(), keySelector));
        services.AddSingleton<IPluginRegistry<T>>(sp => sp.GetRequiredService<PluginRegistry<T>>());
        services.AddSingleton<IMutablePluginRegistry<T>>(sp => sp.GetRequiredService<PluginRegistry<T>>());
        services.AddSingleton<IPluginPointMerger>(sp => new PluginPointMerger<T>(sp.GetRequiredService<IMutablePluginRegistry<T>>(), keySelector));
        return services;
    }
}
