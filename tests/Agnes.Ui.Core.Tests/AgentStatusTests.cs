using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The agent's own one-line status: the sentence it writes about what it found and what it is doing, which
/// every surface shows instead of making a person read a transcript. Two rules carry the whole feature and
/// both are easy to get wrong — it is <em>not</em> a transcript item (and arrives mid-turn, so it must not
/// split a streaming reply), and everything about it that depends on the clock has to be derivable rather
/// than pushed, or a tab left open all afternoon quietly lies about how old its status is.
/// </summary>
public class AgentStatusTests
{
    // A clock the test moves by hand, so "eleven minutes later" costs nothing.
    private sealed class Clock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; set; } = start;

        public void Advance(TimeSpan by) => Now += by;

        public Func<DateTimeOffset> Read => () => Now;
    }

    private static SessionEvent Status(string text, long seq = 1) => new AgentStatusEvent(text) { Sequence = seq };

    // ---- the transcript ----

    [Fact]
    public void A_status_report_adds_nothing_to_the_transcript()
    {
        var t = new TranscriptBuilder();
        t.Apply(Status("Found the config default is wrong; fixing it."));

        Assert.Empty(t.Items);
    }

    [Fact]
    public void A_status_arriving_mid_reply_does_not_break_the_bubble_it_lands_in()
    {
        // The host rate-limits reports, so one lands whenever it lands — routinely in the middle of a
        // streamed answer. Closing the open bubble (as a file card or a tool call correctly does) would
        // split one reply into two at whatever word the agent happened to report on.
        var t = new TranscriptBuilder();
        t.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("one ")) { Sequence = 1 });
        t.Apply(Status("still going", 2));
        t.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("two")) { Sequence = 3 });

        var bubble = Assert.IsType<MessageBubbleItem>(Assert.Single(t.Items));
        Assert.Equal("one two", bubble.Text);
    }

    // ---- applying it to the session ----

    [Fact]
    public void Replaying_a_log_leaves_the_last_status_standing()
    {
        var vm = Session(out _, history:
        [
            new AgentStatusEvent("Reading the request and planning a response."),
            new AgentStatusEvent("Found the config default is wrong; fixing it."),
        ]);

        Assert.True(vm.HasStatus);
        Assert.Equal("Found the config default is wrong; fixing it.", vm.LatestStatus);
    }

    [Fact]
    public void A_live_report_replaces_whatever_was_there()
    {
        var vm = Session(out var view, history: [new AgentStatusEvent("Reading the request.")]);

        view.Apply(Status("Now adding the regression test.", 9));

        Assert.Equal("Now adding the regression test.", vm.LatestStatus);
    }

    [Fact]
    public void A_session_whose_agent_has_never_reported_shows_nothing_rather_than_a_placeholder()
    {
        var vm = Session(out _, history: []);

        Assert.Null(vm.LatestStatus);
        Assert.False(vm.HasStatus);
        Assert.Equal(string.Empty, vm.StatusAge);
        Assert.False(vm.ShowStatusLine);
    }

    [Fact]
    public void A_blank_report_is_ignored_so_a_good_status_is_never_replaced_by_nothing()
    {
        var vm = Session(out var view, history: [new AgentStatusEvent("Fixing the config default.")]);

        view.Apply(Status("   ", 9));

        Assert.Equal("Fixing the config default.", vm.LatestStatus);
    }

    [Fact]
    public void The_age_is_said_in_prose_and_follows_the_clock()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);

        view.Apply(Status("Fixing the config default.", 5));
        Assert.Equal("just now", vm.StatusAge);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("2 min ago", vm.StatusAge);

        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal("3 h ago", vm.StatusAge);
    }

    // ---- gone quiet ----

    [Fact]
    public void A_working_agent_that_has_said_nothing_for_ten_minutes_is_stale()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);

        view.Apply(Status("Fixing the config default.", 1));
        Work(view, 2); // a streamed chunk is what makes the session "working"
        Assert.True(vm.IsWorking);
        Assert.False(vm.StatusIsStale);

        clock.Advance(TimeSpan.FromMinutes(12));
        Assert.True(vm.StatusIsStale);
        Assert.Equal("no update for 12 min", vm.StaleText);
    }

    [Fact]
    public void Silence_from_an_agent_that_is_not_working_is_not_news()
    {
        // An idle session that said nothing for an hour is finished, not silent. Calling that stale would
        // put a warning on every completed session on the dashboard.
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);

        view.Apply(Status("Done — the test passes.", 1));
        clock.Advance(TimeSpan.FromHours(1));

        Assert.False(vm.IsWorking);
        Assert.False(vm.StatusIsStale);
        Assert.Equal(string.Empty, vm.StaleText);
    }

    [Fact]
    public void A_working_agent_that_has_never_reported_at_all_goes_stale_too()
    {
        // The worse case of the two: not "it stopped talking" but "it never started". Measured from when
        // this view opened, since that is the earliest moment this client could have heard anything.
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);

        Work(view, 1);
        Assert.False(vm.StatusIsStale);

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(vm.StatusIsStale);
        Assert.Equal("no update for 11 min", vm.StaleText);
        // ...and the line is worth showing even though there is no status to put on it.
        Assert.False(vm.HasStatus);
        Assert.True(vm.ShowStatusLine);
    }

    // ---- while you were away ----

    [Fact]
    public void A_session_is_unattended_once_the_agent_has_worked_and_nobody_has_looked_for_three_minutes()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);

        // Opening the session counts as looking at it, so nothing is unattended at the start.
        Assert.False(vm.IsUnattended);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(vm.IsUnattended); // five quiet minutes with no agent activity is just a quiet tab

        view.Apply(Status("Found the config default is wrong; fixing it.", 1));
        Assert.True(vm.IsUnattended);
        Assert.Equal("Found the config default is wrong; fixing it.", vm.AwayStatus);
        Assert.True(vm.HasAwayStatus);
    }

    [Fact]
    public void Any_sign_of_life_retires_the_away_band()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        view.Apply(Status("Fixing the config default.", 1));
        Assert.True(vm.IsUnattended);

        vm.NoteUserInteraction();

        Assert.False(vm.IsUnattended);
        Assert.Null(vm.AwayStatus);
        Assert.False(vm.HasAwayStatus);
        // ...but the status itself is untouched: the header keeps saying what the agent is doing.
        Assert.Equal("Fixing the config default.", vm.LatestStatus);
    }

    [Fact]
    public void Coming_back_and_leaving_again_re_arms_it()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        view.Apply(Status("Fixing the config default.", 1));
        vm.NoteUserInteraction();

        // Away for four minutes, but the agent has done nothing since — nothing was missed.
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(vm.IsUnattended);

        view.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("done")) { Sequence = 2 });
        Assert.True(vm.IsUnattended);
    }

    [Fact]
    public void The_users_own_prompt_echoing_back_is_not_the_agent_acting()
    {
        // The host replays a sent prompt to every client as a user message chunk. Counting that as agent
        // activity would raise the band three minutes after someone typed, which is the opposite of the
        // situation it exists for.
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);
        clock.Advance(TimeSpan.FromMinutes(5));

        view.Apply(new MessageChunkEvent(MessageRole.User, new TextContent("fix the config")) { Sequence = 1 });

        Assert.False(vm.IsUnattended);
    }

    [Fact]
    public void Replaying_a_log_is_not_the_agent_acting_either()
    {
        // The whole history arrives at once, before anyone could have interacted with it — a reconnect must
        // not open onto an away band about work the person has already seen.
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out _, clock: clock, history:
        [
            new AgentStatusEvent("Fixing the config default."),
            new MessageChunkEvent(MessageRole.Assistant, new TextContent("done")),
        ]);

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.False(vm.IsUnattended);
        Assert.Null(vm.AwayStatus);
    }

    [Fact]
    public void Back_dating_a_visit_leaves_the_work_already_done_on_the_far_side_of_it()
    {
        // Both sides of "unattended" are timestamps rather than a flag, so stating when the person was last
        // here is enough to describe a session they walked away from — no replaying of the agent's work.
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);
        view.Apply(Status("Fixing the config default.", 1));
        Assert.False(vm.IsUnattended);

        vm.NoteUserInteraction(clock.Now - TimeSpan.FromMinutes(8));

        Assert.True(vm.IsUnattended);
        Assert.Equal("Fixing the config default.", vm.AwayStatus);
    }

    [Fact]
    public void Dismissing_the_band_is_the_same_act_as_being_there()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        view.Apply(Status("Fixing the config default.", 1));

        vm.DismissAwayCommand.Execute(null);

        Assert.False(vm.IsUnattended);
        Assert.Equal(clock.Now, vm.LastUserInteractionAt);
    }

    [Fact]
    public void The_clock_derived_properties_are_re_raised_on_demand_since_nothing_pushes_them()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var vm = Session(out var view, history: [], clock: clock);
        view.Apply(Status("Fixing the config default.", 1));

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        clock.Advance(TimeSpan.FromMinutes(4));
        vm.RaiseStatusAge();

        Assert.Contains(nameof(SessionViewModel.StatusAge), raised);
        Assert.Contains(nameof(SessionViewModel.StatusIsStale), raised);
        Assert.Contains(nameof(SessionViewModel.IsUnattended), raised);
        Assert.Contains(nameof(SessionViewModel.AwayStatus), raised);
    }

    // ---- the catalogue row (a list, before anything is opened) ----

    [Fact]
    public void A_catalogue_row_carries_the_status_the_host_put_on_the_summary()
    {
        var summary = new SessionSummary("s1", "claude", "/home/a/agnes", "Agnes", SessionRunState.Working, 40,
            LatestStatus: "Found the config default is wrong; fixing it.",
            LatestStatusAt: DateTimeOffset.Now - TimeSpan.FromMinutes(2));
        var row = new CatalogSessionRow(new Host(), summary);

        Assert.True(row.HasStatus);
        Assert.Equal("Found the config default is wrong; fixing it.", row.LatestStatus);
        Assert.Equal("2 min ago", row.StatusAge);
    }

    [Fact]
    public void A_catalogue_row_for_a_silent_agent_offers_nothing_to_show()
    {
        var row = new CatalogSessionRow(new Host(),
            new SessionSummary("s1", "claude", "/home/a/agnes", "Agnes", SessionRunState.Idle, 40));

        Assert.False(row.HasStatus);
        Assert.Null(row.LatestStatus);
        Assert.Equal(string.Empty, row.StatusAge);
    }

    // ---- helpers ----

    // A streamed assistant chunk: the only thing that makes a session read as "working", since the working
    // state is derived from the stream rather than from who sent the prompt.
    private static void Work(SessionView view, long seq)
        => view.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("…")) { Sequence = seq });

    // StubAgnesHost is abstract (it is a base for per-test fakes); nothing here talks to a host, so the
    // plainest possible concrete one will do.
    private sealed class Host : StubAgnesHost;

    private static SessionViewModel Session(out SessionView view, IReadOnlyList<SessionEvent> history, Clock? clock = null)
    {
        view = new SessionView("s1");
        var events = history.Select((e, i) => e with { Sequence = i + 1 }).ToArray();
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, 0), events, events.Length));
        return new SessionViewModel(new Host(), view, ImmediateDispatcher.Instance, "OpenCode",
            now: clock?.Read);
    }
}
