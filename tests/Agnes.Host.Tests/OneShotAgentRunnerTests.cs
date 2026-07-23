using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Host.Sessions;

namespace Agnes.Host.Tests;

/// <summary>
/// The shared one-shot-agent primitive (git-and-files/01): opens a throwaway session, sends one prompt,
/// returns the final assistant text, and always tears the session down — including on a timeout.
/// </summary>
public class OneShotAgentRunnerTests
{
    private sealed class FakeSession(string? reply, bool emitTurnEnd) : IAgentSession
    {
        private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>();

        public bool Disposed { get; private set; }
        public string AgentSessionId => "one-shot";
        public ChannelReader<SessionEvent> Events => _events.Reader;

        public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
        {
            if (reply is not null)
            {
                // Two chunks to prove the runner concatenates streamed assistant text in order.
                _events.Writer.TryWrite(new MessageChunkEvent(MessageRole.Assistant, new TextContent(reply)));
                _events.Writer.TryWrite(new MessageChunkEvent(MessageRole.Assistant, new TextContent("!")));
            }

            if (emitTurnEnd)
            {
                _events.Writer.TryWrite(new TurnEndedEvent(StopReason.EndTurn));
            }

            return Task.FromResult(StopReason.EndTurn);
        }

        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAdapter(Func<FakeSession> factory) : IAgentAdapter
    {
        public FakeSession? Last { get; private set; }
        public AgentDescriptor Descriptor { get; } = new() { Id = "one-shot", DisplayName = "One Shot" };

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        {
            Last = factory();
            return Task.FromResult<IAgentSession>(Last);
        }
    }

    [Fact]
    public async Task Returns_the_final_assistant_text_and_tears_the_session_down()
    {
        var adapter = new FakeAdapter(() => new FakeSession("hello", emitTurnEnd: true));
        var runner = new OneShotAgentRunner();

        var result = await runner.RunAsync(adapter, Path.GetTempPath(), [new TextContent("summarize")]);

        Assert.Equal("hello!", result.Text);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.NotNull(adapter.Last);
        Assert.True(adapter.Last!.Disposed);
    }

    [Fact]
    public async Task A_run_that_never_ends_times_out_cleanly_and_still_disposes_the_session()
    {
        // No TurnEndedEvent is ever emitted and the stream stays open, so the run can only end via timeout.
        var adapter = new FakeAdapter(() => new FakeSession(reply: null, emitTurnEnd: false));
        var runner = new OneShotAgentRunner(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(adapter, Path.GetTempPath(), [new TextContent("summarize")]));

        Assert.NotNull(adapter.Last);
        Assert.True(adapter.Last!.Disposed);
    }
}
