using Agnes.Abstractions;
using Agnes.Agents.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Agents.Codex.Tests;

public class CodexSessionTests
{
    private static async Task<List<SessionEvent>> DrainAsync(IAgentSession session, TimeSpan timeout)
    {
        var events = new List<SessionEvent>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (await session.Events.WaitToReadAsync(cts.Token))
            {
                while (session.Events.TryRead(out var e))
                {
                    events.Add(e);
                    if (e is TurnEndedEvent)
                    {
                        return events;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return events;
    }

    private static async Task<T> ReadUntilAsync<T>(IAgentSession session, TimeSpan timeout) where T : SessionEvent
    {
        using var cts = new CancellationTokenSource(timeout);
        while (await session.Events.WaitToReadAsync(cts.Token))
        {
            while (session.Events.TryRead(out var e))
            {
                if (e is T match)
                {
                    return match;
                }
            }
        }

        throw new TimeoutException($"No {typeof(T).Name} within {timeout}");
    }

    [Fact]
    public async Task Full_turn_handshakes_starts_a_thread_streams_a_message_and_completes()
    {
        var (client, server) = FakeCodexAppServer.Create();
        await using var _ = server;
        await using var connection = new CodexConnection(client, client, NullLogger.Instance);

        await connection.InitializeAsync(default);
        var session = await connection.StartThreadAsync("/tmp", "on-request", "workspace-write", default);
        Assert.Equal("th-1", session.AgentSessionId);

        server.OnTurn = async rpc =>
        {
            await rpc.NotifyWithParameterObjectAsync("item/completed", new
            {
                item = new { type = "agentMessage", id = "m1", text = "Hello from Codex" },
                threadId = "th-1",
                turnId = "tn-1",
            });
            await rpc.NotifyWithParameterObjectAsync("turn/completed", new
            {
                threadId = "th-1",
                turn = new { id = "tn-1", status = "completed" },
            });
        };

        var reason = await session.PromptAsync([new TextContent("hi")]);
        Assert.Equal(StopReason.EndTurn, reason);

        var events = await DrainAsync(session, TimeSpan.FromSeconds(5));
        Assert.Contains(events, e => e is MessageChunkEvent m && ((TextContent)m.Content).Text == "Hello from Codex");
        Assert.Contains(events, e => e is TurnEndedEvent);
    }

    [Fact]
    public async Task Approval_request_surfaces_as_a_permission_and_the_decision_returns_to_codex()
    {
        var (client, server) = FakeCodexAppServer.Create();
        await using var _ = server;
        await using var connection = new CodexConnection(client, client, NullLogger.Instance);

        await connection.InitializeAsync(default);
        var session = await connection.StartThreadAsync("/tmp", "on-request", "workspace-write", default);

        server.OnTurn = async rpc =>
        {
            // The server asks the client to approve a command; the client's user answers.
            await server.RequestApprovalAsync("item/commandExecution/requestApproval", new
            {
                callId = "call-1",
                command = new[] { "rm", "-rf", "build" },
                cwd = "/tmp",
                reason = "Delete the build folder?",
            });
            await rpc.NotifyWithParameterObjectAsync("turn/completed", new
            {
                threadId = "th-1",
                turn = new { id = "tn-1", status = "completed" },
            });
        };

        var promptTask = session.PromptAsync([new TextContent("clean up")]);

        var request = await ReadUntilAsync<PermissionRequestedEvent>(session, TimeSpan.FromSeconds(5));
        Assert.Equal("Delete the build folder?", request.Title);
        Assert.Equal("call-1", request.ToolCallId);

        await session.RespondToPermissionAsync(request.RequestId, "approve");

        var reason = await promptTask;
        Assert.Equal(StopReason.EndTurn, reason);
        Assert.Equal("approved", server.LastApprovalDecision);
    }

    [Fact]
    public async Task User_input_request_surfaces_as_a_question_and_the_answers_return_to_codex()
    {
        var (client, server) = FakeCodexAppServer.Create();
        await using var _ = server;
        await using var connection = new CodexConnection(client, client, NullLogger.Instance);

        await connection.InitializeAsync(default);
        var session = await connection.StartThreadAsync("/tmp", "on-request", "workspace-write", default);

        server.OnTurn = async rpc =>
        {
            await server.RequestUserInputAsync(new
            {
                threadId = "th-1",
                turnId = "tn-1",
                itemId = "item-9",
                questions = new object[]
                {
                    new
                    {
                        id = "db",
                        header = "Database",
                        question = "Which database should we use?",
                        isOther = true,
                        options = new object[]
                        {
                            new { label = "SQLite", description = "Embedded, zero-config" },
                            new { label = "Postgres", description = "Client/server" },
                        },
                    },
                },
            });
            await rpc.NotifyWithParameterObjectAsync("turn/completed", new
            {
                threadId = "th-1",
                turn = new { id = "tn-1", status = "completed" },
            });
        };

        var promptTask = session.PromptAsync([new TextContent("set up the db")]);

        var question = await ReadUntilAsync<QuestionAskedEvent>(session, TimeSpan.FromSeconds(5));
        Assert.Equal("item-9", question.ToolCallId);
        var q = Assert.Single(question.Questions);
        Assert.Equal("db", q.Id);
        Assert.Equal("Database", q.Header);
        Assert.True(q.AllowFreeText);
        Assert.False(q.MultiSelect);
        Assert.Equal(2, q.Options.Count);
        Assert.Equal("SQLite", q.Options[0].Label);

        await session.AnswerQuestionAsync(question.RequestId,
            [new QuestionAnswer("db", ["Postgres"], "and add pgbouncer")]);

        var reason = await promptTask;
        Assert.Equal(StopReason.EndTurn, reason);

        // The answers echoed back to Codex: {"db":{"answers":["Postgres","and add pgbouncer"]}}.
        var answers = server.LastUserInputAnswers!.Value;
        var picks = answers.GetProperty("db").GetProperty("answers").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["Postgres", "and add pgbouncer"], picks);

        var resolved = await ReadUntilAsync<QuestionAnsweredEvent>(session, TimeSpan.FromSeconds(2));
        Assert.Equal(question.RequestId, resolved.RequestId);
    }
}
