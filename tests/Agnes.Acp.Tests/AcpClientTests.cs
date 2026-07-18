using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Acp.Tests;

public class AcpClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static readonly CapturingLogger Logger = new();

    private static async Task<(AcpConnection Connection, IAgentSession Session, FakeAcpAgent Agent)> ConnectAsync(
        Func<FakePromptContext, Task<string>> onPrompt)
    {
        var (clientStream, agentStream) = FakeAcpAgent.CreateTransport();
        var agent = new FakeAcpAgent(agentStream, onPrompt);
        var connection = new AcpConnection(clientStream, clientStream, Logger);
        await connection.InitializeAsync(CancellationToken.None);
        var session = await connection.NewSessionAsync("/tmp/work", CancellationToken.None);
        return (connection, session, agent);
    }

    private static async Task<List<SessionEvent>> DrainUntilAsync(
        ChannelReader<SessionEvent> reader,
        Func<SessionEvent, bool> stop)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var events = new List<SessionEvent>();
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

    [Fact]
    public async Task Streams_assistant_message_chunks_and_ends_turn()
    {
        var (connection, session, agent) = await ConnectAsync(async ctx =>
        {
            await ctx.SendAgentMessageAsync("Hello, ");
            await ctx.SendAgentMessageAsync("world");
            return "end_turn";
        });

        await using var _ = connection;
        await using var __ = agent;

        var promptTask = session.PromptAsync([new TextContent("hi")]);
        var events = await DrainUntilAsync(session.Events, e => e is TurnEndedEvent);
        var stop = await promptTask;

        Assert.Equal(StopReason.EndTurn, stop);
        var text = string.Concat(events.OfType<MessageChunkEvent>()
            .Where(m => m.Role == MessageRole.Assistant)
            .Select(m => ((TextContent)m.Content).Text));
        Assert.Equal("Hello, world", text);
        Assert.Contains(events, e => e is TurnEndedEvent { Reason: StopReason.EndTurn });
    }

    [Fact]
    public async Task Maps_tool_call_and_plan_updates()
    {
        var (connection, session, agent) = await ConnectAsync(async ctx =>
        {
            await ctx.SendPlanAsync(("Investigate", "in_progress"), ("Fix", "pending"));
            await ctx.SendToolCallAsync("tc-1", "Read file.cs", "read", "in_progress");
            return "end_turn";
        });

        await using var _ = connection;
        await using var __ = agent;

        var promptTask = session.PromptAsync([new TextContent("go")]);
        var events = await DrainUntilAsync(session.Events, e => e is TurnEndedEvent);
        await promptTask;

        var plan = Assert.Single(events.OfType<PlanEvent>());
        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal("Investigate", plan.Entries[0].Content);

        var toolCall = Assert.Single(events.OfType<ToolCallEvent>());
        Assert.Equal("tc-1", toolCall.ToolCallId);
        Assert.Equal(ToolKind.Read, toolCall.Kind);
        Assert.Equal(ToolCallStatus.InProgress, toolCall.Status);
    }

    [Fact]
    public async Task Permission_request_round_trips_to_client_and_back()
    {
        FakePermissionResult? agentSaw = null;
        var (connection, session, agent) = await ConnectAsync(async ctx =>
        {
            agentSaw = await ctx.RequestPermissionAsync(
                "tc-1",
                "Run rm -rf build/",
                ("allow", "Allow", "allow_once"),
                ("reject", "Reject", "reject_once"));
            return "end_turn";
        });

        await using var _ = connection;
        await using var __ = agent;

        var promptTask = session.PromptAsync([new TextContent("delete build")]);

        // Wait for the permission request to surface, then answer it.
        var untilRequest = await DrainUntilAsync(session.Events, e => e is PermissionRequestedEvent);
        var request = Assert.Single(untilRequest.OfType<PermissionRequestedEvent>());
        Assert.Equal(2, request.Options.Count);
        await session.RespondToPermissionAsync(request.RequestId, "allow");

        var afterResolve = await DrainUntilAsync(session.Events, e => e is TurnEndedEvent);
        await promptTask;

        var resolved = Assert.Single(afterResolve.OfType<PermissionResolvedEvent>());
        Assert.Equal("allow", resolved.OptionId);
        Assert.Equal(PermissionOutcome.Allowed, resolved.Outcome);

        Assert.NotNull(agentSaw);
        Assert.True(agentSaw!.Selected);
        Assert.Equal("allow", agentSaw.OptionId);
    }
}
