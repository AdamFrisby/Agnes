using Agnes.Abstractions.Events;
using Agnes.Ui.Core.Plugins;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>A client plugin bound to the client event spine can intercept a real client action — showing a
/// notification — before it happens (see .ideas/00d-event-spine-and-ui-extensibility.md, AC5).</summary>
public class ClientEventSpineTests
{
    private sealed class RecordingNotifier : INotifier
    {
        public List<AppNotification> Shown { get; } = [];
        public void Notify(AppNotification notification) => Shown.Add(notification);
    }

    private sealed class SuppressInterceptor : IEventInterceptor<BeforeNotificationEvent>
    {
        public ValueTask InterceptAsync(BeforeNotificationEvent evt, CancellationToken ct = default)
        {
            evt.Cancel("do-not-disturb");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RewriteInterceptor : IEventInterceptor<BeforeNotificationEvent>
    {
        public ValueTask InterceptAsync(BeforeNotificationEvent evt, CancellationToken ct = default)
        {
            evt.Notification = evt.Notification with { Title = "[tagged] " + evt.Notification.Title };
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BindingModule(IEventInterceptor<BeforeNotificationEvent> interceptor) : IClientPluginModule
    {
        public void Register(ClientPluginCollector collector)
            => collector.AddEventBinding(new Binding(interceptor));

        private sealed class Binding(IEventInterceptor<BeforeNotificationEvent> interceptor) : IEventBinding
        {
            public void Bind(IEventBus bus, ICollection<IDisposable> registrations)
                => registrations.Add(bus.Intercept(interceptor));
        }
    }

    private static AppNotification Note(string title = "Ready")
        => new(title, "body", NotificationKind.Completion, "s1");

    [Fact]
    public async Task A_plugin_can_suppress_a_notification()
    {
        var plugins = ClientPluginHost.FromModules([new BindingModule(new SuppressInterceptor())]);
        var notifier = new RecordingNotifier();
        var dispatcher = new NotificationDispatcher(plugins.EventBus, notifier);

        await dispatcher.NotifyAsync(Note());

        Assert.Empty(notifier.Shown); // the plugin vetoed it
    }

    [Fact]
    public async Task A_plugin_can_rewrite_a_notification()
    {
        var plugins = ClientPluginHost.FromModules([new BindingModule(new RewriteInterceptor())]);
        var notifier = new RecordingNotifier();
        var dispatcher = new NotificationDispatcher(plugins.EventBus, notifier);

        await dispatcher.NotifyAsync(Note("Ready"));

        var shown = Assert.Single(notifier.Shown);
        Assert.Equal("[tagged] Ready", shown.Title);
    }

    [Fact]
    public async Task With_no_plugins_a_notification_is_shown_unchanged()
    {
        var notifier = new RecordingNotifier();
        var dispatcher = new NotificationDispatcher(ClientPluginSet.Empty.EventBus, notifier);

        await dispatcher.NotifyAsync(Note("Ready"));

        var shown = Assert.Single(notifier.Shown);
        Assert.Equal("Ready", shown.Title);
    }
}
