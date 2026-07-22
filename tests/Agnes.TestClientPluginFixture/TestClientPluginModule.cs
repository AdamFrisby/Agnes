using Agnes.Ui.Core.Plugins;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.TestClientPluginFixture;

/// <summary>A real client plugin module compiled into its own assembly, loaded dynamically by the desktop
/// loader test. Registers a notification channel that records what it was asked to show.</summary>
public sealed class TestClientPluginModule : IClientPluginModule
{
    public void Register(ClientPluginCollector collector)
        => collector.AddNotificationChannel(new RecordingChannel());

    /// <summary>A channel whose shown notifications are observable via a static sink, so the test can see
    /// that the dynamically-loaded channel actually ran (crossing the ALC boundary via the shared
    /// Agnes.Ui.Core types).</summary>
    private sealed class RecordingChannel : IClientNotificationChannel
    {
        public string ChannelId => "test-dynamic-channel";

        public void Show(AppNotification notification) => Sink.Add(notification.Title);
    }
}

/// <summary>Shared sink the dynamically-loaded channel writes to (lives in the fixture assembly, but the
/// test reads it after loading the assembly, so it's the same static across the ALC boundary).</summary>
public static class Sink
{
    private static readonly List<string> Shown = [];

    public static void Add(string title)
    {
        lock (Shown) { Shown.Add(title); }
    }

    public static IReadOnlyList<string> Titles()
    {
        lock (Shown) { return Shown.ToArray(); }
    }
}
