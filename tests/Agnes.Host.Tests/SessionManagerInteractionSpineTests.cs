using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>A plugin bound to the spine can override or veto the host's replies to the agent — permission
/// responses, question answers, and mode changes (see .ideas/00d-event-spine-and-ui-extensibility.md).</summary>
public class SessionManagerInteractionSpineTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private sealed class SetPermissionOption(string optionId) : IEventInterceptor<BeforePermissionResponseEvent>
    {
        public ValueTask InterceptAsync(BeforePermissionResponseEvent evt, CancellationToken ct = default) { evt.OptionId = optionId; return ValueTask.CompletedTask; }
    }

    private sealed class SetMode(string modeId) : IEventInterceptor<BeforeModeChangeEvent>
    {
        public ValueTask InterceptAsync(BeforeModeChangeEvent evt, CancellationToken ct = default) { evt.ModeId = modeId; return ValueTask.CompletedTask; }
    }

    private sealed class Veto<TEvent>(string reason) : IEventInterceptor<TEvent> where TEvent : CancelableEvent
    {
        public ValueTask InterceptAsync(TEvent evt, CancellationToken ct = default) { evt.Cancel(reason); return ValueTask.CompletedTask; }
    }

    private static async Task<(SessionManager Manager, ScriptedAgentAdapter Adapter, EventBus Bus, string SessionId)> OpenAsync()
    {
        var adapter = new ScriptedAgentAdapter();
        var bus = new EventBus();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance, eventBus: bus);
        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        return (manager, adapter, bus, info.SessionId);
    }

    [Fact]
    public async Task A_plugin_can_override_the_permission_option()
    {
        var (manager, adapter, bus, sessionId) = await OpenAsync();
        await using var _ = manager;
        bus.Intercept(new SetPermissionOption("allow")); // policy plugin auto-allows

        await manager.RespondPermissionAsync(sessionId, "req1", "reject");

        Assert.Equal("allow", adapter.Session.LastPermissionOptionId);
    }

    [Fact]
    public async Task A_plugin_can_veto_a_permission_response()
    {
        var (manager, adapter, bus, sessionId) = await OpenAsync();
        await using var _ = manager;
        bus.Intercept(new Veto<BeforePermissionResponseEvent>("hold"));

        await manager.RespondPermissionAsync(sessionId, "req1", "allow");

        Assert.Null(adapter.Session.LastPermissionOptionId); // never forwarded to the agent
    }

    [Fact]
    public async Task A_plugin_can_override_a_mode_change()
    {
        var (manager, adapter, bus, sessionId) = await OpenAsync();
        await using var _ = manager;
        bus.Intercept(new SetMode("plan"));

        await manager.SetModeAsync(sessionId, "build");

        Assert.Equal("plan", adapter.Session.LastMode);
    }

    [Fact]
    public async Task A_plugin_can_veto_a_mode_change()
    {
        var (manager, adapter, bus, sessionId) = await OpenAsync();
        await using var _ = manager;
        bus.Intercept(new Veto<BeforeModeChangeEvent>("no"));

        await manager.SetModeAsync(sessionId, "build");

        Assert.Null(adapter.Session.LastMode);
    }

    [Fact]
    public async Task A_plugin_can_veto_a_question_answer()
    {
        var (manager, adapter, bus, sessionId) = await OpenAsync();
        await using var _ = manager;
        bus.Intercept(new Veto<BeforeQuestionAnswerEvent>("no"));

        await manager.AnswerQuestionAsync(sessionId, "q1", [new QuestionAnswerDto("q1", ["yes"], null)]);

        Assert.Null(adapter.Session.LastAnswers);
    }

    [Fact]
    public async Task With_no_interceptors_interactions_reach_the_agent_unchanged()
    {
        var (manager, adapter, _, sessionId) = await OpenAsync();
        await using var _d = manager;

        await manager.RespondPermissionAsync(sessionId, "req1", "allow");
        await manager.SetModeAsync(sessionId, "build");

        Assert.Equal("allow", adapter.Session.LastPermissionOptionId);
        Assert.Equal("build", adapter.Session.LastMode);
    }
}
