using Agnes.Protocol;
using Agnes.Ui.Core.Plugins;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.App.Desktop.Plugins;

/// <summary>
/// Composes the desktop head's client plugins from both sources — the built-in (compile-time) modules and
/// any dynamically-loaded ones — and produces the <see cref="ClientCapabilities"/> the client advertises
/// during negotiation. The desktop head supports dynamic loading, so it reports
/// <c>SupportsDynamicPlugins = true</c>; a locked-down head (iOS/WASM) would build a set from static
/// modules only and report false.
/// </summary>
public static class DesktopClientPlugins
{
    public const string PlatformId = "desktop";

    /// <summary>Builds the desktop client plugin set: the built-in OS notifier expressed as a channel, plus
    /// every dynamic module found under <paramref name="dynamicPluginDirectory"/> (if any).</summary>
    public static ClientPluginSet Build(INotifier notifier, string? dynamicPluginDirectory = null, Action<string, Exception>? onLoadError = null)
    {
        var modules = new List<IClientPluginModule> { new DesktopBuiltInModule(notifier) };
        if (!string.IsNullOrWhiteSpace(dynamicPluginDirectory))
        {
            modules.AddRange(DesktopClientPluginLoader.LoadModules(dynamicPluginDirectory, onLoadError));
        }

        return ClientPluginHost.FromModules(modules);
    }

    /// <summary>The capabilities the desktop client advertises to the host, derived from its plugin set.</summary>
    public static ClientCapabilities Capabilities(string clientId, ClientPluginSet plugins)
        => ClientCapabilityBuilder.Build(clientId, PlatformId, supportsDynamicPlugins: true, plugins);

    /// <summary>The built-in desktop module: exposes the head's <see cref="INotifier"/> (OS toast / in-app
    /// banner) as a client notification channel.</summary>
    private sealed class DesktopBuiltInModule(INotifier notifier) : IClientPluginModule
    {
        public void Register(ClientPluginCollector collector)
            => collector.AddNotificationChannel(new DelegatingNotificationChannel("desktop-toast", notifier));
    }
}
