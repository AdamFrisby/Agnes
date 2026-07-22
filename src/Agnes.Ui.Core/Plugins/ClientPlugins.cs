using Agnes.Abstractions;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Plugins;

/// <summary>
/// Client-side plugin infrastructure (see <c>.ideas/00c-client-plugins-and-negotiation.md</c>). Client
/// plugins register into the same <see cref="IPluginRegistry{TProvider}"/> abstraction the host uses, but
/// are gathered from <see cref="IClientPluginModule"/>s rather than loaded by the host installer — so the
/// *source* of modules (compile-time on every platform, or a runtime ALC loader on capable heads) varies
/// while the registry model stays identical. Intentionally free of any DI-container dependency so it works
/// unchanged on iOS/WASM heads.
/// </summary>
public interface IClientPluginModule
{
    /// <summary>Registers this plugin's client-side providers into the collector.</summary>
    void Register(ClientPluginCollector collector);
}

/// <summary>A client plugin-point: shows a notification on this device (the client half of the two-sided
/// notifications feature — the host fires the trigger, a channel here displays it).</summary>
public interface IClientNotificationChannel
{
    /// <summary>Stable id, e.g. <c>desktop-toast</c>, <c>android-push</c>.</summary>
    string ChannelId { get; }

    /// <summary>Displays the notification on this device.</summary>
    void Show(AppNotification notification);
}

/// <summary>Adapts an existing <see cref="INotifier"/> as a notification channel, so a head can expose its
/// current notifier (OS toast, in-app banner) as a built-in client plugin without rewriting it.</summary>
public sealed class DelegatingNotificationChannel(string channelId, INotifier notifier) : IClientNotificationChannel
{
    public string ChannelId => channelId;
    public void Show(AppNotification notification) => notifier.Notify(notification);
}

/// <summary>Collects client-plugin providers registered by modules, then builds the typed registries.</summary>
public sealed class ClientPluginCollector
{
    private readonly List<IClientNotificationChannel> _notificationChannels = [];

    public void AddNotificationChannel(IClientNotificationChannel channel) => _notificationChannels.Add(channel);

    public ClientPluginSet Build() => new(
        new PluginRegistry<IClientNotificationChannel>(_notificationChannels, c => c.ChannelId));
}

/// <summary>The client's populated plugin registries, one per client plugin-point.</summary>
public sealed class ClientPluginSet(IPluginRegistry<IClientNotificationChannel> notificationChannels)
{
    public IPluginRegistry<IClientNotificationChannel> NotificationChannels { get; } = notificationChannels;

    /// <summary>Empty set — no client plugins (a valid state, e.g. a headless/minimal client).</summary>
    public static ClientPluginSet Empty { get; } = new ClientPluginCollector().Build();
}

/// <summary>Builds a <see cref="ClientPluginSet"/> from a set of modules. The caller decides where the
/// modules come from — compile-time references (every platform) and/or a runtime loader (capable heads).</summary>
public static class ClientPluginHost
{
    public static ClientPluginSet FromModules(IEnumerable<IClientPluginModule> modules)
    {
        var collector = new ClientPluginCollector();
        foreach (var module in modules)
        {
            module.Register(collector);
        }

        return collector.Build();
    }
}

/// <summary>Produces the <see cref="ClientCapabilities"/> a client advertises during negotiation, derived
/// from which client plugin-points are actually populated.</summary>
public static class ClientCapabilityBuilder
{
    public static ClientCapabilities Build(string clientId, string platform, bool supportsDynamicPlugins, ClientPluginSet plugins)
    {
        var pluginPoints = new List<string>();
        var capabilityIds = new List<string>();

        if (plugins.NotificationChannels.All.Count > 0)
        {
            pluginPoints.Add("client.notification");
            capabilityIds.Add(ClientCapabilityIds.Notifications);
        }

        return new ClientCapabilities(clientId, platform, supportsDynamicPlugins, pluginPoints, capabilityIds);
    }
}
