using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.TestKit;

namespace Agnes.Acp.Tests;

/// <summary>
/// Resuming an ACP conversation with <c>session/load</c>. The behaviour worth pinning isn't that the call
/// happens — it's that the agent's replay of the whole prior conversation does NOT reach Agnes's event log,
/// which already holds that history and would otherwise show it twice.
/// </summary>
public class AcpResumeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Load_session_drops_the_agents_replay_of_prior_history()
    {
        var (clientStream, agentStream) = FakeAcpAgent.CreateTransport();
        await using var agent = new FakeAcpAgent(
            agentStream,
            onPrompt: async ctx =>
            {
                await ctx.SendAgentMessageAsync("fresh");
                return "end_turn";
            },
            supportsLoadSession: true,
            // A real agent replays the conversation as session/update notifications before session/load
            // returns. Several lines, not one: the leak this guards against is a race on the TAIL of the
            // replay, so a single line would pass even with the bug.
            onLoad: async ctx =>
            {
                for (var i = 0; i < 16; i++)
                {
                    await ctx.SendAgentMessageAsync($"replayed line {i}");
                }
            });

        await using var connection = new AcpConnection(clientStream, clientStream, AcpClientTests.Logger);
        await connection.InitializeAsync(CancellationToken.None);

        var session = await connection.LoadSessionAsync("sess-1", "/tmp/work", CancellationToken.None);

        Assert.Equal("sess-1", session.AgentSessionId);

        // Only what happens AFTER the resume should be in the log.
        using var cts = new CancellationTokenSource(Timeout);
        var promptTask = session.PromptAsync([new TextContent("carry on")], cts.Token);
        var texts = new List<string>();
        while (await session.Events.WaitToReadAsync(cts.Token))
        {
            while (session.Events.TryRead(out var e))
            {
                if (e is MessageChunkEvent { Content: TextContent text })
                {
                    texts.Add(text.Text);
                }

                if (e is TurnEndedEvent)
                {
                    await promptTask;
                    Assert.Equal(["fresh"], texts);
                    return;
                }
            }
        }

        Assert.Fail("turn never ended");
    }

    [Fact]
    public async Task Initialize_reports_whether_the_agent_can_load_sessions()
    {
        var (clientStream, agentStream) = FakeAcpAgent.CreateTransport();
        await using var agent = new FakeAcpAgent(agentStream, _ => Task.FromResult("end_turn"), supportsLoadSession: true);
        await using var connection = new AcpConnection(clientStream, clientStream, AcpClientTests.Logger);

        var init = await connection.InitializeAsync(CancellationToken.None);

        // The client only attempts a resume when the agent says it can; an agent that can't must never be asked.
        Assert.True(init.AgentCapabilities?.LoadSession);
    }
}
