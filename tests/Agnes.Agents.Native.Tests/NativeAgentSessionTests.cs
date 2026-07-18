using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Agents.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Agents.Native.Tests;

public class NativeAgentSessionTests
{
    /// <summary>A TextReader whose lines the test feeds asynchronously (the fake agent's stdout).</summary>
    private sealed class ScriptedReader : TextReader
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();

        public void Line(string line) => _lines.Writer.TryWrite(line);
        public void End() => _lines.Writer.TryWrite(null);

        public override async Task<string?> ReadLineAsync()
        {
            try
            {
                return await _lines.Reader.ReadAsync();
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    [Fact]
    public async Task Prompt_streams_events_and_completes_on_result()
    {
        var reader = new ScriptedReader();
        var stdin = new StringWriter();
        await using var session = new NativeAgentSession(reader, stdin, new ClaudeCodeStreamMapper(), NullLogger.Instance);

        reader.Line("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\"}");

        var promptTask = session.PromptAsync([new TextContent("hi")]);

        // The fake agent answers, then ends the turn.
        reader.Line("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}}");
        reader.Line("{\"type\":\"result\",\"is_error\":false}");

        var reason = await promptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StopReason.EndTurn, reason);
        Assert.Equal("s1", session.AgentSessionId);

        // The user turn was written to stdin as stream-json.
        Assert.Contains("\"type\":\"user\"", stdin.ToString());

        // The mapped events streamed on the channel.
        var events = await DrainUntilAsync(session.Events, e => e is TurnEndedEvent);
        Assert.Contains(events, e => e is MessageChunkEvent { Role: MessageRole.Assistant });
        Assert.Contains(events, e => e is TurnEndedEvent);
    }

    private static async Task<List<SessionEvent>> DrainUntilAsync(ChannelReader<SessionEvent> reader, Func<SessionEvent, bool> stop)
    {
        var events = new List<SessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await reader.WaitToReadAsync(cts.Token))
        {
            while (reader.TryRead(out var e))
            {
                events.Add(e);
                if (stop(e))
                {
                    return events;
                }
            }
        }

        return events;
    }
}
