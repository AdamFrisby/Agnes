using Agnes.Abstractions;
using Agnes.Client.Simulation;
using Agnes.Recording;

namespace Agnes.Desktop.Tests;

public class RecordingTests
{
    private static async Task WaitAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public void Recorder_stamps_offsets_from_the_first_event()
    {
        var t0 = DateTimeOffset.UtcNow;
        var recorder = new SessionRecorder();
        recorder.Record(new MessageChunkEvent(MessageRole.User, new TextContent("hi")) { Timestamp = t0 });
        recorder.Record(new ThoughtChunkEvent(new TextContent("thinking")) { Timestamp = t0.AddMilliseconds(50) });
        recorder.Record(new MessageChunkEvent(MessageRole.Assistant, new TextContent("hello")) { Timestamp = t0.AddMilliseconds(120) });
        recorder.Record(new TurnEndedEvent(StopReason.EndTurn) { Timestamp = t0.AddMilliseconds(140) });

        var recording = recorder.Build("Test", "opencode", "OpenCode");

        Assert.Equal(4, recording.Events.Count);
        Assert.Equal(0, recording.Events[0].OffsetMs);
        Assert.Equal(50, recording.Events[1].OffsetMs);
        Assert.Equal(140, recording.DurationMs);
    }

    [Fact]
    public void Save_and_load_preserves_polymorphic_events()
    {
        var recorder = new SessionRecorder();
        recorder.Record(new ToolCallEvent("tc1", "Write file", ToolKind.Edit, ToolCallStatus.Completed, []));
        recorder.Record(new TurnEndedEvent(StopReason.EndTurn));
        var path = Path.Combine(Path.GetTempPath(), $"agnes-rec-{Guid.NewGuid():n}.json");

        RecordingStore.Save(path, recorder.Build("Test", "opencode", "OpenCode"));
        var loaded = RecordingStore.Load(path);

        Assert.Equal(2, loaded.Events.Count);
        var tool = Assert.IsType<ToolCallEvent>(loaded.Events[0].Event);
        Assert.Equal(ToolKind.Edit, tool.Kind);
        Assert.IsType<TurnEndedEvent>(loaded.Events[1].Event);
    }

    [Fact]
    public async Task Recorded_host_replays_a_recording_as_a_session()
    {
        var recorder = new SessionRecorder();
        recorder.Record(new MessageChunkEvent(MessageRole.Assistant, new TextContent("408")));
        recorder.Record(new TurnEndedEvent(StopReason.EndTurn));
        var recording = recorder.Build("Quick answer", "opencode", "OpenCode");

        var host = new RecordedHost("rec://test", [recording], speed: 1000);
        await host.ConnectAsync();

        var agents = await host.ListAgentsAsync();
        Assert.Contains(agents, a => a.DisplayName == "Quick answer");

        var info = await host.OpenSessionAsync(agents[0].AdapterId, ".");
        var view = await host.SubscribeAsync(info.SessionId);

        await WaitAsync(() => view.Events.OfType<TurnEndedEvent>().Any());
        Assert.Contains(view.Events, e => e is MessageChunkEvent { Role: MessageRole.Assistant });
    }

    [Fact]
    public async Task Real_captured_opencode_fixture_replays()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "recordings", "opencode-qa.json");
        Assert.True(File.Exists(path), $"expected captured fixture at {path}");

        var recording = RecordingStore.Load(path);
        Assert.Equal("opencode", recording.AdapterId);
        Assert.NotEmpty(recording.Events);

        var host = new RecordedHost("rec://test", [recording], speed: 1000);
        await host.ConnectAsync();
        var info = await host.OpenSessionAsync((await host.ListAgentsAsync())[0].AdapterId, ".");
        var view = await host.SubscribeAsync(info.SessionId);

        await WaitAsync(() => view.Events.OfType<TurnEndedEvent>().Any());
        // The real OpenCode Q&A ends with an assistant answer.
        Assert.Contains(view.Events, e => e is MessageChunkEvent { Role: MessageRole.Assistant });
    }
}
