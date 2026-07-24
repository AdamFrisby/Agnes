using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>Mid-session model switching (models/switch): the model is a launch-time CLI argument, so
/// <see cref="SessionManager.SwitchModelAsync"/> persists the new model on the session record and relaunches
/// the agent on it. The choice must also survive across a later restart.</summary>
public class ModelSwitchTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private static SessionManager NewManager(ScriptedAgentAdapter adapter, IEventStore store)
        => new(TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance);

    [Fact]
    public async Task Switching_the_model_relaunches_the_agent_on_it_and_persists_it()
    {
        var store = new InMemoryEventStore();
        var adapter = new ScriptedAgentAdapter();
        await using var manager = NewManager(adapter, store);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", modelId: "sonnet");
        Assert.Equal("sonnet", adapter.LastOptions?.ModelId);
        Assert.Equal("sonnet", info.CurrentModelId);

        await manager.SwitchModelAsync(info.SessionId, "opus");

        // The relaunch carried the new model to the CLI…
        Assert.Equal("opus", adapter.LastOptions?.ModelId);
        // …and it's persisted on the catalogue record (so it survives a restart).
        var record = Assert.Single(await store.ListSessionsAsync());
        Assert.Equal("opus", record.ModelId);
        // …and a joining client sees the new model on the snapshot.
        Assert.Equal("opus", (await manager.GetSnapshotAsync(info.SessionId, 0)).Session.CurrentModelId);
    }

    [Fact]
    public async Task Switching_to_the_same_model_is_a_no_op()
    {
        var store = new InMemoryEventStore();
        var adapter = new ScriptedAgentAdapter();
        await using var manager = NewManager(adapter, store);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", modelId: "sonnet");
        var headBefore = (await manager.GetSnapshotAsync(info.SessionId, 0)).HeadSequence;

        await manager.SwitchModelAsync(info.SessionId, "sonnet");

        // No relaunch notice appended — nothing changed.
        Assert.Equal(headBefore, (await manager.GetSnapshotAsync(info.SessionId, 0)).HeadSequence);
    }

    [Fact]
    public async Task A_restart_resumes_on_the_switched_model()
    {
        var store = new InMemoryEventStore();
        string sessionId;
        await using (var manager = NewManager(new ScriptedAgentAdapter(), store))
        {
            var info = await manager.OpenSessionAsync("scripted", "/tmp/work", modelId: "sonnet");
            sessionId = info.SessionId;
            await manager.SwitchModelAsync(sessionId, "opus");
        }

        // A brand-new manager over the same store restores the session and relaunches on the persisted model.
        var adapter2 = new ScriptedAgentAdapter();
        await using var resumed = NewManager(adapter2, store);
        await resumed.RestoreAsync();
        await resumed.PromptAsync(sessionId, [new TextContent("continue")]);

        Assert.Equal("opus", adapter2.LastOptions?.ModelId);
    }
}
