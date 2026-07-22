using Agnes.Protocol;
using Agnes.Ui.Core.Plugins;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>Client-side plugins register into the same registry model as the host, gathered from modules
/// (compile-time or dynamic), and drive what the client advertises during negotiation
/// (see .ideas/00c-client-plugins-and-negotiation.md).</summary>
public class ClientPluginTests
{
    private sealed class RecordingNotifier : INotifier
    {
        public List<AppNotification> Shown { get; } = [];
        public void Notify(AppNotification notification) => Shown.Add(notification);
    }

    private sealed class NotificationModule(INotifier notifier) : IClientPluginModule
    {
        public void Register(ClientPluginCollector collector)
            => collector.AddNotificationChannel(new DelegatingNotificationChannel("desktop-toast", notifier));
    }

    [Fact]
    public void Static_modules_populate_the_client_registry()
    {
        var notifier = new RecordingNotifier();
        var plugins = ClientPluginHost.FromModules([new NotificationModule(notifier)]);

        var channel = plugins.NotificationChannels.Find("desktop-toast");
        Assert.NotNull(channel);

        channel!.Show(new AppNotification("Ready", "Agent finished", NotificationKind.Completion, "s1"));
        Assert.Single(notifier.Shown);
        Assert.Equal("Ready", notifier.Shown[0].Title);
    }

    [Fact]
    public void An_empty_client_advertises_no_notification_capability()
    {
        var caps = ClientCapabilityBuilder.Build("c1", "wasm", supportsDynamicPlugins: false, ClientPluginSet.Empty);

        Assert.False(caps.SupportsDynamicPlugins);
        Assert.Equal("wasm", caps.Platform);
        Assert.DoesNotContain(ClientCapabilityIds.Notifications, caps.CapabilityIds);
    }

    [Fact]
    public void A_client_with_a_notification_channel_advertises_the_notifications_capability()
    {
        var plugins = ClientPluginHost.FromModules([new NotificationModule(new RecordingNotifier())]);
        var caps = ClientCapabilityBuilder.Build("c1", "desktop", supportsDynamicPlugins: true, plugins);

        Assert.Contains(ClientCapabilityIds.Notifications, caps.CapabilityIds);
        Assert.Contains("client.notification", caps.PluginPointIds);
    }

    [Fact]
    public void Negotiation_reports_notifications_as_Both_when_the_host_also_triggers_them()
    {
        var plugins = ClientPluginHost.FromModules([new NotificationModule(new RecordingNotifier())]);
        var caps = ClientCapabilityBuilder.Build("c1", "desktop", supportsDynamicPlugins: true, plugins);

        IReadOnlyList<HostCapability> hostWithTrigger = [new(ClientCapabilityIds.Notifications, Available: true, FailClosed: false)];
        var negotiated = CapabilityNegotiator.Reconcile(hostWithTrigger, caps);
        Assert.Equal(CapabilitySupport.Both, negotiated.Capabilities.Single(c => c.Id == ClientCapabilityIds.Notifications).Support);

        // With a host that can't trigger notifications, the same client capability is client-only.
        var negotiatedNoHost = CapabilityNegotiator.Reconcile([], caps);
        Assert.Equal(CapabilitySupport.ClientOnly, negotiatedNoHost.Capabilities.Single(c => c.Id == ClientCapabilityIds.Notifications).Support);
    }
}
