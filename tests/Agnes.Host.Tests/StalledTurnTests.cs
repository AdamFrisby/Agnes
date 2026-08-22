using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// The stall rule and the honest-reporting of stop reasons. Covers the OpenCode failure observed in the
/// wild: the agent streams reasoning for minutes, then ends the turn reporting a normal completion.
/// </summary>
public sealed class StalledTurnTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private static TurnProductivity Fold(params SessionEvent[] events)
    {
        var turn = TurnProductivity.Empty;
        foreach (var e in events)
        {
            turn = turn.WithEvent(e);
        }

        return turn;
    }

    [Fact]
    public void Reasoning_only_turn_that_ends_normally_is_a_stall()
    {
        // Exactly the dawn2 shape: thought chunks, then turn_ended(end_turn).
        var turn = Fold(
            new ThoughtChunkEvent(new TextContent("thinking about the structure")),
            new ThoughtChunkEvent(new TextContent("still thinking")));

        Assert.True(turn.IsStall(StopReason.EndTurn));
    }

    [Fact]
    public void A_turn_with_an_assistant_message_is_not_a_stall()
    {
        // Even a terse answer is a real result — never re-prompt over the top of one.
        var turn = Fold(new MessageChunkEvent(MessageRole.Assistant, new TextContent("Done.")));

        Assert.False(turn.IsStall(StopReason.EndTurn));
    }

    [Fact]
    public void A_turn_with_only_a_tool_call_is_not_a_stall()
    {
        var turn = Fold(new ToolCallEvent("call-1", "bash", ToolKind.Execute, ToolCallStatus.Pending, []));

        Assert.False(turn.IsStall(StopReason.EndTurn));
    }

    [Fact]
    public void A_turn_that_ends_asking_the_user_a_question_is_not_a_stall()
    {
        // The agent is blocked on a person. Auto-continuing here would talk straight over the question.
        var turn = Fold(new QuestionAskedEvent("q-1", "call-1", []));

        Assert.False(turn.IsStall(StopReason.EndTurn));
    }

    [Fact]
    public void A_turn_that_ends_awaiting_a_permission_is_not_a_stall()
    {
        var turn = Fold(new PermissionRequestedEvent("p-1", "call-1", "write /etc/hosts", []));

        Assert.False(turn.IsStall(StopReason.EndTurn));
    }

    [Fact]
    public void A_turn_that_reported_an_error_is_not_a_stall()
    {
        // A specific failure, not a silent stop — re-prompting would just repeat it.
        var turn = Fold(new AgentErrorEvent("provider rejected the request"));

        Assert.False(turn.IsStall(StopReason.EndTurn));
    }

    [Fact]
    public void A_user_message_alone_does_not_count_as_production()
    {
        // The user's own prompt is logged into the turn; it must not mask a stall.
        var turn = Fold(new MessageChunkEvent(MessageRole.User, new TextContent("do the thing")));

        Assert.True(turn.IsStall(StopReason.EndTurn));
    }

    [Theory]
    [InlineData(StopReason.Cancelled)]
    [InlineData(StopReason.MaxTokens)]
    [InlineData(StopReason.Refusal)]
    [InlineData(StopReason.MaxTurnRequests)]
    public void Only_a_normal_completion_can_stall(StopReason reason)
    {
        // These reasons are the agent saying something specific — a person cancelled, the budget ran out,
        // the model refused. Re-prompting would fight the user or repeat a failure that will repeat again.
        Assert.False(Fold().IsStall(reason));
    }

    [Fact]
    public void An_empty_turn_that_ends_normally_is_a_stall()
    {
        Assert.True(Fold().IsStall(StopReason.EndTurn));
    }

    // ---- the retry cap ----

    [Fact]
    public void Auto_continue_defaults_are_bounded_and_on()
    {
        var options = new AutoContinueOptions();

        Assert.True(options.Enabled);
        Assert.InRange(options.MaxAttempts, 1, 5); // bounded: a model that always stalls must not loop forever
        Assert.NotEmpty(options.Prompt);
    }

    // ---- end-to-end: the host actually re-prompts, and the cap actually stops it ----

    /// <summary>An agent that always stalls: it emits a normal turn end having produced nothing, which is
    /// precisely what OpenCode did against a weak model.</summary>
    private static ScriptedAgentAdapter AlwaysStalls(List<IReadOnlyList<ContentBlock>> prompts)
    {
        var adapter = new ScriptedAgentAdapter();
        adapter.Session.OnPrompt = (content, session) =>
        {
            prompts.Add(content);
            session.Emit(new ThoughtChunkEvent(new TextContent("thinking…")));
            session.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };
        return adapter;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task A_stalled_turn_is_continued_up_to_the_cap_then_reported()
    {
        var prompts = new List<IReadOnlyList<ContentBlock>>();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(AlwaysStalls(prompts)), new InMemoryEventStore(), new NullBroadcaster(),
            NullLoggerFactory.Instance, autoContinue: new AutoContinueOptions { Enabled = true, MaxAttempts = 2 });

        var info = await manager.OpenSessionAsync("scripted", Path.GetTempPath(), useSandbox: false);
        await manager.PromptAsync(info.SessionId, [new TextContent("do the thing")]);

        // 1 user prompt + 2 auto-continues, then it gives up rather than looping forever.
        await WaitForAsync(async () =>
            (await manager.GetSnapshotAsync(info.SessionId, 0)).Events
                .OfType<NoticeEvent>().Any(n => n.IsError));

        Assert.Equal(3, prompts.Count);

        var notices = (await manager.GetSnapshotAsync(info.SessionId, 0)).Events.OfType<NoticeEvent>().ToList();
        Assert.Equal(2, notices.Count(n => !n.IsError));          // one per retry
        Assert.Single(notices, n => n.IsError);                   // the give-up

        // The continuation must never masquerade as something the person typed.
        var userMessages = (await manager.GetSnapshotAsync(info.SessionId, 0)).Events
            .OfType<MessageChunkEvent>().Where(m => m.Role == MessageRole.User).ToList();
        Assert.Single(userMessages);
        Assert.Contains("do the thing", ((TextContent)userMessages[0].Content).Text);
    }

    [Fact]
    public async Task A_stall_is_reported_but_not_continued_when_auto_continue_is_off()
    {
        var prompts = new List<IReadOnlyList<ContentBlock>>();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(AlwaysStalls(prompts)), new InMemoryEventStore(), new NullBroadcaster(),
            NullLoggerFactory.Instance, autoContinue: new AutoContinueOptions { Enabled = false });

        var info = await manager.OpenSessionAsync("scripted", Path.GetTempPath(), useSandbox: false);
        await manager.PromptAsync(info.SessionId, [new TextContent("do the thing")]);

        await WaitForAsync(async () =>
            (await manager.GetSnapshotAsync(info.SessionId, 0)).Events.OfType<NoticeEvent>().Any());

        // Surfacing the stall is not optional — only the retry is.
        Assert.Single(prompts);
        Assert.Single((await manager.GetSnapshotAsync(info.SessionId, 0)).Events.OfType<NoticeEvent>(), n => n.IsError);
    }

    [Fact]
    public async Task A_productive_turn_is_never_continued()
    {
        var prompts = new List<IReadOnlyList<ContentBlock>>();
        var adapter = new ScriptedAgentAdapter();
        adapter.Session.OnPrompt = (content, session) =>
        {
            prompts.Add(content);
            session.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("Done.")));
            session.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };

        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(),
            NullLoggerFactory.Instance, autoContinue: new AutoContinueOptions { Enabled = true, MaxAttempts = 2 });

        var info = await manager.OpenSessionAsync("scripted", Path.GetTempPath(), useSandbox: false);
        await manager.PromptAsync(info.SessionId, [new TextContent("do the thing")]);

        await WaitForAsync(async () =>
            (await manager.GetSnapshotAsync(info.SessionId, 0)).Events.OfType<TurnEndedEvent>().Any());
        await Task.Delay(150); // give any (incorrect) auto-continue a chance to fire

        Assert.Single(prompts);
        Assert.Empty((await manager.GetSnapshotAsync(info.SessionId, 0)).Events.OfType<NoticeEvent>());
    }
}
