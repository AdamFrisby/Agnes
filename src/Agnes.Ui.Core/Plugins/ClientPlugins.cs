using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Plugins;

/// <summary>Raised before a notification is shown on this device (client-side event spine). An interceptor
/// may rewrite <see cref="Notification"/> or <see cref="CancelableEvent.Cancel"/> it (e.g. a do-not-disturb
/// plugin, or one that reroutes certain notifications elsewhere).</summary>
public sealed class BeforeNotificationEvent(AppNotification notification) : CancelableEvent
{
    public AppNotification Notification { get; set; } = notification;
}

/// <summary>Dispatches a notification through the client event bus before showing it, so client plugins can
/// intercept it. A canceled notification is simply not shown.</summary>
public sealed class NotificationDispatcher(IEventBus bus, INotifier notifier)
{
    public async Task NotifyAsync(AppNotification notification)
    {
        var evt = await bus.DispatchAsync(new BeforeNotificationEvent(notification)).ConfigureAwait(false);
        if (!evt.IsCanceled)
        {
            notifier.Notify(evt.Notification);
        }
    }
}

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

/// <summary>Well-known UI slot ids a plugin can contribute content into. A head that doesn't render a
/// given slot simply ignores contributions to it.</summary>
public static class UiSlots
{
    public const string ComposerActions = "composer.actions";
    public const string ConversationBanner = "conversation.banner";
    public const string SessionSidebar = "session.sidebar";
}

/// <summary>A contribution rendered into a named UI slot. <see cref="CreateContent"/> returns a view-model
/// the head resolves to a view; content is platform-agnostic here (the head owns rendering).</summary>
public interface IUiContribution
{
    string SlotId { get; }
    int Order => 0;
    object CreateContent();
}

/// <summary>Context handed to a conversation-item renderer for the item it may render.</summary>
public sealed record ConversationItemContext(string ItemKind, object Item);

/// <summary>Overrides or decorates how a transcript item of a given kind renders. The registry picks the
/// lowest-<see cref="Order"/> renderer for a kind; returning null from <see cref="CreateView"/> falls back
/// to the built-in renderer.</summary>
public interface IConversationItemRenderer
{
    string ItemKind { get; }
    int Order => 0;
    object? CreateView(ConversationItemContext context);
}

/// <summary>A custom top-level screen a plugin contributes, opened as a tab/document like the built-in
/// Settings screen. The head hosts <see cref="CreateViewModel"/> as a document and resolves its view.</summary>
public interface ICustomScreenProvider
{
    string ScreenId { get; }
    string Title { get; }
    string? Icon { get; }
    object CreateViewModel();
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

/// <summary>Collects client-plugin providers and event bindings registered by modules, then builds the
/// typed registries and the client event bus (with the plugins' bindings applied).</summary>
public sealed class ClientPluginCollector
{
    private readonly List<IClientNotificationChannel> _notificationChannels = [];
    private readonly List<IEventBinding> _eventBindings = [];
    private readonly List<IUiContribution> _contributions = [];
    private readonly List<IConversationItemRenderer> _renderers = [];
    private readonly List<ICustomScreenProvider> _screens = [];

    public void AddNotificationChannel(IClientNotificationChannel channel) => _notificationChannels.Add(channel);

    /// <summary>Registers a plugin's event bindings (interceptors/observers) onto the client bus.</summary>
    public void AddEventBinding(IEventBinding binding) => _eventBindings.Add(binding);

    /// <summary>Registers a contribution into a named UI slot.</summary>
    public void AddUiContribution(IUiContribution contribution) => _contributions.Add(contribution);

    /// <summary>Registers a renderer that overrides/decorates a conversation item kind.</summary>
    public void AddConversationRenderer(IConversationItemRenderer renderer) => _renderers.Add(renderer);

    /// <summary>Registers a custom screen a head can open as a tab/document.</summary>
    public void AddCustomScreen(ICustomScreenProvider screen) => _screens.Add(screen);

    public ClientPluginSet Build()
    {
        var bus = new EventBus();
        var registrations = new List<IDisposable>(); // the client bus lives for the app's lifetime
        foreach (var binding in _eventBindings)
        {
            binding.Bind(bus, registrations);
        }

        return new ClientPluginSet(
            new PluginRegistry<IClientNotificationChannel>(_notificationChannels, c => c.ChannelId),
            bus,
            [.. _contributions],
            [.. _renderers],
            [.. _screens]);
    }
}

/// <summary>The client's populated plugin registries, event bus, and UI extension contributions.</summary>
public sealed class ClientPluginSet(
    IPluginRegistry<IClientNotificationChannel> notificationChannels,
    IEventBus eventBus,
    IReadOnlyList<IUiContribution> contributions,
    IReadOnlyList<IConversationItemRenderer> conversationRenderers,
    IReadOnlyList<ICustomScreenProvider> customScreens)
{
    public IPluginRegistry<IClientNotificationChannel> NotificationChannels { get; } = notificationChannels;

    /// <summary>The client event spine, with every plugin's bindings applied.</summary>
    public IEventBus EventBus { get; } = eventBus;

    /// <summary>Every UI-slot contribution, across all plugins.</summary>
    public IReadOnlyList<IUiContribution> Contributions { get; } = contributions;

    /// <summary>Custom screens a head can open as tabs.</summary>
    public IReadOnlyList<ICustomScreenProvider> CustomScreens { get; } = customScreens;

    private readonly IReadOnlyList<IConversationItemRenderer> _conversationRenderers = conversationRenderers;

    /// <summary>Contributions for one slot, in Order.</summary>
    public IReadOnlyList<IUiContribution> SlotContributions(string slotId)
        => Contributions.Where(c => c.SlotId == slotId).OrderBy(c => c.Order).ToArray();

    /// <summary>The winning (lowest-Order) plugin renderer for an item kind, or null to use the built-in.</summary>
    public IConversationItemRenderer? RendererFor(string itemKind)
        => _conversationRenderers.Where(r => r.ItemKind == itemKind).OrderBy(r => r.Order).FirstOrDefault();

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
