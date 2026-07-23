using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Cli;
using Agnes.Protocol;

namespace Agnes.Cli.Tests;

public sealed class WaitLogicTests
{
    // ---- pure classification ----

    [Fact]
    public void TurnEnded_after_a_prompt_reads_as_idle()
    {
        var events = new SessionEvent[]
        {
            User("hi", 1),
            new MessageChunkEvent(MessageRole.Assistant, new TextContent("done")) { Sequence = 2 },
            new TurnEndedEvent(StopReason.EndTurn) { Sequence = 3 },
        };

        Assert.Equal(SessionState.Idle, SessionActivity.Evaluate(events));
    }

    [Fact]
    public void A_prompt_with_no_terminal_event_reads_as_running()
    {
        var events = new SessionEvent[]
        {
            User("hi", 1),
            new MessageChunkEvent(MessageRole.Assistant, new TextContent("working")) { Sequence = 2 },
        };

        Assert.Equal(SessionState.Running, SessionActivity.Evaluate(events));
    }

    [Fact]
    public void An_agent_error_reads_as_errored()
    {
        var events = new SessionEvent[]
        {
            User("hi", 1),
            new AgentErrorEvent("boom") { Sequence = 2 },
        };

        Assert.Equal(SessionState.Errored, SessionActivity.Evaluate(events));
    }

    [Fact]
    public void Re_prompting_after_an_error_clears_the_error()
    {
        var events = new SessionEvent[]
        {
            User("hi", 1),
            new AgentErrorEvent("boom") { Sequence = 2 },
            User("try again", 3),
            new TurnEndedEvent(StopReason.EndTurn) { Sequence = 4 },
        };

        Assert.Equal(SessionState.Idle, SessionActivity.Evaluate(events));
    }

    // ---- exit-code mapping (0 = idle, 1 = timeout, 2 = agent error) ----

    [Theory]
    [InlineData(WaitOutcome.Idle, 0)]
    [InlineData(WaitOutcome.Timeout, 1)]
    [InlineData(WaitOutcome.AgentError, 2)]
    public void Exit_codes_follow_the_contract(WaitOutcome outcome, int expected)
        => Assert.Equal(expected, ExitCodes.ForWait(outcome));

    // ---- IdleWaiter over a live SessionView ----

    [Fact]
    public async Task Wait_resolves_idle_when_a_turn_ends()
    {
        var view = RunningView();

        var wait = IdleWaiter.WaitAsync(view, TimeSpan.FromSeconds(5), TimeProvider.System);
        view.Apply(new TurnEndedEvent(StopReason.EndTurn) { Sequence = 3 });

        Assert.Equal(WaitOutcome.Idle, await wait);
    }

    [Fact]
    public async Task Wait_resolves_agent_error_distinctly_from_a_timeout()
    {
        var view = RunningView();

        var wait = IdleWaiter.WaitAsync(view, TimeSpan.FromSeconds(5), TimeProvider.System);
        view.Apply(new AgentErrorEvent("crashed") { Sequence = 3 });

        Assert.Equal(WaitOutcome.AgentError, await wait);
    }

    [Fact]
    public async Task Wait_times_out_while_the_turn_is_still_running()
    {
        var view = RunningView();

        var outcome = await IdleWaiter.WaitAsync(view, TimeSpan.FromMilliseconds(150), TimeProvider.System);

        Assert.Equal(WaitOutcome.Timeout, outcome);
    }

    [Fact]
    public async Task Wait_returns_immediately_when_already_idle()
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(
            Info(),
            [User("hi", 1), new TurnEndedEvent(StopReason.EndTurn) { Sequence = 2 }],
            2));

        var outcome = await IdleWaiter.WaitAsync(view, TimeSpan.FromMilliseconds(50), TimeProvider.System);

        Assert.Equal(WaitOutcome.Idle, outcome);
    }

    private static SessionView RunningView()
    {
        var view = new SessionView("s1");
        // Snapshot leaves the session mid-turn (a user prompt, no terminal event yet).
        view.ApplySnapshot(new SessionSnapshot(Info(), [User("hi", 1)], 1));
        view.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("working")) { Sequence = 2 });
        return view;
    }

    private static MessageChunkEvent User(string text, long seq)
        => new(MessageRole.User, new TextContent(text)) { Sequence = seq };

    private static SessionInfo Info() => new("s1", "claude-code", Path.GetTempPath(), 0);
}
