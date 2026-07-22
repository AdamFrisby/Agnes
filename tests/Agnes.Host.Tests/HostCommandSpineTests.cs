using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Plugins;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>The host command tail runs through the spine: session/sandbox runtime commands, plugin
/// lifecycle, and scheduled-task management all dispatch vetoable Before* and observe-only *edEvents
/// (see .ideas/00d-event-spine-and-ui-extensibility.md, Pass 2).</summary>
public class HostCommandSpineTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    // A one-off interceptor that cancels the first event of the given type it sees.
    private sealed class Veto<T> : IEventInterceptor<T> where T : CancelableEvent
    {
        public ValueTask InterceptAsync(T evt, CancellationToken ct = default) { evt.Cancel("test"); return ValueTask.CompletedTask; }
    }

    private sealed class Record<T>(Action<T> onSeen) : IEventObserver<T> where T : IAgnesEvent
    {
        public ValueTask ObserveAsync(T evt, CancellationToken ct = default) { onSeen(evt); return ValueTask.CompletedTask; }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) { cts.Token.ThrowIfCancellationRequested(); await Task.Delay(10, cts.Token); }
    }

    // ---- session runtime commands ----

    [Fact]
    public async Task An_interceptor_can_veto_a_cancel_so_the_agent_keeps_running()
    {
        var adapter = new ScriptedAgentAdapter();
        var bus = new EventBus();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance, eventBus: bus);
        var cancelled = false;
        adapter.Session.OnCancel = () => { cancelled = true; return Task.CompletedTask; };
        adapter.Session.OnPrompt = (_, s) => { s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };
        bus.Intercept(new Veto<BeforeSessionCancelEvent>());

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        await manager.CancelAsync(info.SessionId);

        Assert.False(cancelled); // the veto kept the turn running
    }

    // ---- scheduled tasks ----

    [Fact]
    public async Task Scheduling_a_task_dispatches_before_and_created_events()
    {
        var bus = new EventBus();
        string? created = null;
        bus.Observe(new Record<ScheduledTaskCreatedEvent>(e => created = e.TaskId));
        var manager = new ScheduledTaskManager(bus: bus);

        var task = await manager.AddAsync(new ScheduleTaskRequest("scripted", "/tmp", "hello", IntervalSeconds: 5));

        Assert.Equal(task.Id, created);
    }

    [Fact]
    public async Task An_interceptor_can_veto_scheduling_a_task()
    {
        var bus = new EventBus();
        bus.Intercept(new Veto<BeforeScheduledTaskCreateEvent>());
        var manager = new ScheduledTaskManager(bus: bus);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddAsync(new ScheduleTaskRequest("scripted", "/tmp", "hello", IntervalSeconds: 5)));
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task An_interceptor_can_veto_removing_a_task()
    {
        var bus = new EventBus();
        var manager = new ScheduledTaskManager(bus: bus);
        var task = await manager.AddAsync(new ScheduleTaskRequest("scripted", "/tmp", "hello", IntervalSeconds: 5));
        bus.Intercept(new Veto<BeforeScheduledTaskRemoveEvent>());

        await manager.RemoveAsync(task.Id);

        Assert.Single(manager.List()); // veto kept it
    }

    // ---- plugin lifecycle ----

    // Minimal installer that just records calls and returns a fixed plugin.
    private sealed class FakeInstaller : IPluginInstaller
    {
        public List<string> Uninstalled { get; } = [];
        public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PluginSearchResult>>([]);
        public Task<InstalledPlugin> InstallAsync(string packageId, string? version, IReadOnlyCollection<string> caps, CancellationToken ct = default)
            => Task.FromResult(new InstalledPlugin(packageId, version ?? "1.0.0", true, [], false));
        public Task<InstalledPlugin> UpdateAsync(string pluginId, IReadOnlyCollection<string> caps, CancellationToken ct = default)
            => Task.FromResult(new InstalledPlugin(pluginId, "2.0.0", true, [], false));
        public Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task UninstallAsync(string pluginId, CancellationToken ct = default) { Uninstalled.Add(pluginId); return Task.CompletedTask; }
        public Task ConfigureAsync(string pluginId, IReadOnlyDictionary<string, string> settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<InstalledPlugin>> ListInstalledAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InstalledPlugin>>([]);
    }

    [Fact]
    public async Task Installing_a_plugin_dispatches_before_and_installed_events()
    {
        var bus = new EventBus();
        string? installed = null;
        bus.Observe(new Record<PluginInstalledEvent>(e => installed = e.PluginId));
        var service = new PluginManagementService(new FakeInstaller(), bus);

        var outcome = await service.InstallAsync(new InstallPluginRequest("com.acme.tool", "1.2.3", []));

        Assert.True(outcome.Success);
        Assert.Equal("com.acme.tool", installed);
    }

    [Fact]
    public async Task A_governance_plugin_can_veto_an_install()
    {
        var bus = new EventBus();
        bus.Intercept(new Veto<BeforePluginInstallEvent>());
        var service = new PluginManagementService(new FakeInstaller(), bus);

        var outcome = await service.InstallAsync(new InstallPluginRequest("com.acme.tool", "1.2.3", []));

        Assert.False(outcome.Success);
        Assert.Contains("blocked", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_governance_plugin_can_veto_an_uninstall()
    {
        var bus = new EventBus();
        bus.Intercept(new Veto<BeforePluginUninstallEvent>());
        var installer = new FakeInstaller();
        var service = new PluginManagementService(installer, bus);

        await service.UninstallAsync("com.acme.tool");

        Assert.Empty(installer.Uninstalled); // veto kept it installed
    }
}
