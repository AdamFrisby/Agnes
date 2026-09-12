using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The wall as a live thing: what it does with a clock, an event feed and the two switches.
/// </summary>
/// <remarks>
/// No dispatcher and no Avalonia here — the view model takes its clock as a dependency
/// (<see cref="IWallClock"/>) precisely so that "the flash has faded after a second and a half" and "the
/// timers stop when you leave the section" are assertions rather than sleeps.
/// </remarks>
public sealed class NowWorkingViewModelTests
{
    private static readonly DateTimeOffset Start = NowWorkingSamples.Now;

    private sealed class Fixture
    {
        public FakeWallClock Clock { get; } = new();

        public DateTimeOffset Now { get; set; } = Start;

        public Board? Board { get; set; } = NowWorkingSamples.Board();

        public Overview? Overview { get; set; } = NowWorkingSamples.Overview();

        public NowWorkingViewModel Wall { get; }

        public Fixture()
        {
            Wall = new NowWorkingViewModel(
                () => Board,
                () => Overview,
                NowWorkingSamples.Items,
                action => { action(); return Task.CompletedTask; },
                tail: null,
                now: () => Now,
                clock: Clock);
        }

        /// <summary>Moves the clock on and fires whatever timers are running.</summary>
        public void Advance(TimeSpan by, int frames = 1)
        {
            Now += by;
            Clock.Fire(frames);
        }
    }

    // ---- the timers ------------------------------------------------------------------------------

    [Fact]
    public void Leaving_the_section_stops_every_timer()
    {
        var f = new Fixture();
        f.Wall.Start();

        Assert.Equal(2, f.Clock.Running);   // one second hand, one frame loop
        Assert.True(f.Wall.IsRunning);
        Assert.True(f.Wall.IsAnimating);

        f.Wall.Stop();

        Assert.Equal(0, f.Clock.Running);
        Assert.False(f.Wall.IsRunning);
        Assert.False(f.Wall.IsAnimating);
    }

    [Fact]
    public void Starting_twice_does_not_start_two_sets_of_timers()
    {
        var f = new Fixture();
        f.Wall.Start();
        f.Wall.Start();

        Assert.Equal(2, f.Clock.Running);
    }

    [Fact]
    public void Disposing_it_stops_it()
    {
        var f = new Fixture();
        f.Wall.Start();
        f.Wall.Dispose();

        Assert.Equal(0, f.Clock.Running);
    }

    [Fact]
    public void An_event_that_arrives_while_the_section_is_elsewhere_is_dropped()
    {
        var f = new Fixture();
        f.Wall.Note(Landed(1));

        Assert.Empty(f.Wall.Ticker);
        Assert.Equal(0, f.Wall.Minutes.Sum());
    }

    // ---- calm ------------------------------------------------------------------------------------

    [Fact]
    public void Calm_stops_the_frame_loop_and_leaves_the_second_hand_running()
    {
        var f = new Fixture();
        f.Wall.Start();

        f.Wall.ToggleCalmCommand.Execute(null);

        Assert.True(f.Wall.IsCalm);
        Assert.False(f.Wall.IsAnimating);
        Assert.Equal(1, f.Clock.Running);
        Assert.True(f.Wall.IsRunning);

        f.Wall.ToggleCalmCommand.Execute(null);

        Assert.False(f.Wall.IsCalm);
        Assert.True(f.Wall.IsAnimating);
    }

    [Fact]
    public void Calm_settles_everything_that_was_mid_animation()
    {
        var f = new Fixture();
        f.Wall.Start();
        f.Wall.Accept(Landed(1), f.Now);

        Assert.Equal(1, f.Wall.Ticker[0].Glow);
        Assert.NotEqual(default, f.Wall.Ticker[0].Enter);

        f.Wall.IsCalm = true;

        Assert.Equal(0, f.Wall.Ticker[0].Glow);
        Assert.Equal(default, f.Wall.Ticker[0].Enter);
        Assert.All(f.Wall.Slots, s => Assert.Equal(0, s.Glow));
        Assert.All(f.Wall.Numbers, n => Assert.Equal(n.Number.Value, n.Display));
        Assert.All(f.Wall.Gauges, g => Assert.Equal(1, g.Pulse));
    }

    [Fact]
    public void Calm_keeps_the_live_updates_coming()
    {
        var f = new Fixture();
        f.Wall.Start();
        f.Wall.IsCalm = true;

        f.Wall.Accept(Landed(1), f.Now);

        // The line is in the log and the heartbeat counted it; only the decoration is gone.
        Assert.Single(f.Wall.Ticker);
        Assert.Equal(0, f.Wall.Ticker[0].Glow);
        Assert.Equal(1, f.Wall.Minutes.Sum());
    }

    [Fact]
    public void Calm_puts_a_changed_number_straight_to_its_new_value()
    {
        var f = new Fixture();
        f.Wall.Start();
        f.Wall.IsCalm = true;

        var landed = f.Wall.Numbers.Single(n => n.Key == "landed-today");
        Assert.Equal(6, landed.Display);

        landed.Apply(landed.Number with { Value = 9 }, f.Now, calm: true);

        Assert.Equal(9, landed.Display);
        Assert.Equal("9", landed.Text);
    }

    // ---- the wall switch -------------------------------------------------------------------------

    [Fact]
    public void The_wall_switch_is_a_toggle_and_says_which_way_it_is_facing()
    {
        var f = new Fixture();

        Assert.False(f.Wall.IsWall);
        Assert.Equal("Wall", f.Wall.WallButtonText);
        Assert.Equal("Calm", f.Wall.CalmButtonText);

        f.Wall.ToggleWallCommand.Execute(null);

        Assert.True(f.Wall.IsWall);
        Assert.Equal("Exit wall", f.Wall.WallButtonText);

        f.Wall.ToggleCalmCommand.Execute(null);
        Assert.Equal("Calm on", f.Wall.CalmButtonText);
    }

    // ---- what the panels do ----------------------------------------------------------------------

    [Fact]
    public void Starting_builds_the_whole_wall_from_what_the_tab_already_holds()
    {
        var f = new Fixture();
        f.Wall.Start();

        Assert.True(f.Wall.HasData);
        Assert.Equal(5, f.Wall.Slots.Count);
        Assert.Equal("4 of 5 slots busy", f.Wall.Headline);
        Assert.NotEmpty(f.Wall.Numbers);
        Assert.NotEmpty(f.Wall.Gauges);
        Assert.True(f.Wall.HasFlow);
    }

    [Fact]
    public void A_card_keeps_its_timer_across_a_rebuild_and_ticks_every_second()
    {
        var f = new Fixture();
        f.Wall.Start();

        // The newest card, whose timer is still counting seconds: the oldest is an hour in and reads in
        // hours and minutes, so five seconds would not move it.
        var card = f.Wall.Slots.Last(s => s.IsBusy);
        var before = card.Elapsed;

        f.Advance(TimeSpan.FromSeconds(5));

        Assert.Same(card, f.Wall.Slots.Last(s => s.IsBusy));
        Assert.NotEqual(before, card.Elapsed);
    }

    [Fact]
    public void A_slot_whose_item_changed_state_flashes_and_the_flash_decays()
    {
        var f = new Fixture();
        f.Wall.Start();
        f.Wall.Frames(f.Now.AddHours(1));   // let the arrival flash finish

        var card = f.Wall.Slots.Single(s => s.Card.Phase == "Working");
        Assert.Equal(0, card.Glow);

        // The orchestrator moves that item on, and the tab rebuilds its board.
        f.Board = Moved(f.Board!, card.Card.ItemId, "Auditing");
        f.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Auditing", f.Wall.Slots.Single(s => s.Card.ItemId == card.Card.ItemId).Phase);
        Assert.Equal(1.0, card.Glow, 2);

        // …and a second and a half later it is gone, which is what makes two events read as two.
        f.Advance(NowWorkingViewModel.FlashLife);
        Assert.Equal(0, card.Glow);
    }

    [Fact]
    public void A_slot_that_frees_up_becomes_an_outline_rather_than_disappearing()
    {
        var f = new Fixture();
        f.Wall.Start();
        var running = f.Board!.Now.Count;

        f.Board = f.Board with { Now = [.. f.Board.Now.Skip(1)], Slots = (running - 1, 5) };
        f.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(5, f.Wall.Slots.Count);
        Assert.Equal(2, f.Wall.Slots.Count(s => s.IsFree));
    }

    [Fact]
    public void A_number_that_moved_counts_to_its_new_value_rather_than_jumping()
    {
        var f = new Fixture();
        f.Wall.Start();
        var landed = f.Wall.Numbers.Single(n => n.Key == "landed-today");

        landed.Apply(landed.Number with { Value = 20 }, f.Now, calm: false);
        Assert.Equal(6, landed.Display);   // not yet: the count-up has not been stepped

        landed.Frame(f.Now.AddMilliseconds(120));
        Assert.InRange(landed.Display, 6.1, 19.9);

        landed.Frame(f.Now + NowWorkingViewModel.CountLife);
        Assert.Equal(20, landed.Display);
        Assert.Equal("20", landed.Text);
    }

    [Fact]
    public void The_log_names_what_moved_and_the_heartbeat_counts_everything()
    {
        var f = new Fixture();
        f.Wall.Start();

        f.Wall.Accept(Event(1, "work_item.working", state: "Working"), f.Now);
        f.Wall.Accept(Event(2, "iteration.started", state: null), f.Now);
        f.Wall.Accept(Event(3, "audit.started", state: null), f.Now);
        f.Wall.Accept(Event(4, "work_item.auditing", state: "Auditing"), f.Now);

        // Two lines out of four events: the phase-level pair says nothing the state lines do not.
        Assert.Equal(2, f.Wall.Ticker.Count);
        Assert.Equal("Working → Auditing", f.Wall.Ticker[0].Verb);
        Assert.Equal("→ Working", f.Wall.Ticker[1].Verb);

        // But all four are in the pulse, which is the number that says how busy it is.
        Assert.Equal(4, f.Wall.Minutes.Sum());
        Assert.Contains("4 events in the last hour", f.Wall.Pulse, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_never_grows_past_a_dozen_lines()
    {
        var f = new Fixture();
        f.Wall.Start();

        for (var i = 0; i < 30; i++)
        {
            f.Now += TimeSpan.FromSeconds(5);
            f.Wall.Accept(Event(i + 1, "work_item.done", title: $"item {i}", state: "Done"), f.Now);
        }

        Assert.Equal(NowWorkingModel.TickerDepth, f.Wall.Ticker.Count);
        Assert.Equal("item 29", f.Wall.Ticker[0].Subject);
    }

    [Fact]
    public void An_hour_of_silence_empties_the_heartbeat_again()
    {
        var f = new Fixture();
        f.Wall.Start();
        f.Wall.Accept(Landed(1), f.Now);
        Assert.Equal(1, f.Wall.Minutes.Sum());

        f.Advance(TimeSpan.FromMinutes(61));

        Assert.Equal(0, f.Wall.Minutes.Sum());
        Assert.Equal(60, f.Wall.Minutes.Count);
    }

    [Fact]
    public void A_gauge_breathes_only_while_its_window_is_actually_going_down()
    {
        var f = new Fixture();
        f.Wall.Start();

        var burning = f.Wall.Gauges.Single(g => g.Agent == "claude");
        var flat = f.Wall.Gauges.Single(g => g.Agent == "copilot");

        burning.Frame(f.Now);
        flat.Frame(f.Now);

        Assert.True(burning.Burn.IsBurning);
        Assert.NotEqual(1, burning.Pulse);
        Assert.Equal(1, flat.Pulse);
    }

    // ---- what the section around it does ----------------------------------------------------------

    /// <summary>Answers every request with an empty body, so a sections view model can be built without
    /// a host anywhere near it.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static CodeyBoxSectionsViewModel Sections(FakeWallClock clock) => new(
        new CodeyBoxClient(new CodeyBoxOptions("http://127.0.0.1:1", "k"), new Offline()),
        action => { action(); return Task.CompletedTask; },
        board: NowWorkingSamples.Board,
        clock: clock);

    [Fact]
    public void The_section_starts_the_wall_when_it_is_shown_and_stops_it_when_it_is_left()
    {
        var clock = new FakeWallClock();
        var sections = Sections(clock);

        Assert.False(sections.NowWorking.IsRunning);
        Assert.Equal(0, clock.Running);

        sections.Section = CodeyBoxSection.NowWorking;
        Assert.True(sections.IsNowWorking);
        Assert.True(sections.NowWorking.IsRunning);
        Assert.Equal(2, clock.Running);

        sections.Section = CodeyBoxSection.Queue;
        Assert.False(sections.NowWorking.IsRunning);
        Assert.Equal(0, clock.Running);
    }

    [Fact]
    public void The_wall_switch_hides_the_tabs_own_chrome_and_only_while_the_wall_is_the_section()
    {
        var sections = Sections(new FakeWallClock());
        Assert.True(sections.IsChromeVisible);

        sections.Section = CodeyBoxSection.NowWorking;
        Assert.True(sections.IsChromeVisible);

        sections.NowWorking.ToggleWallCommand.Execute(null);
        Assert.False(sections.IsChromeVisible);

        // Leaving the section brings the rail and the header back even with the switch still thrown —
        // otherwise a wall left in that state would eat the chrome of every other section.
        sections.Section = CodeyBoxSection.Queue;
        Assert.True(sections.IsChromeVisible);
    }

    [Fact]
    public void The_section_titles_it_the_way_the_rail_names_it()
    {
        var sections = Sections(new FakeWallClock()) ;
        sections.Section = CodeyBoxSection.NowWorking;

        Assert.Equal("Now working", sections.SectionTitle);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static CodeyBoxEvent Event(
        long id, string type, string? title = "An item", string? state = null)
        => new(id, type, "a1".PadRight(32, '0'), "codeybox-self", Start, title, state);

    private static CodeyBoxEvent Landed(long id) => Event(id, "work_item.done", state: "Done");

    /// <summary>The same board with one item in a new state — what the tab hands over after a feed
    /// event has been read back.</summary>
    private static Board Moved(Board board, string itemId, string state)
    {
        var now = board.Now.Select(chain =>
        {
            if (chain.Head.Id != itemId)
            {
                return chain;
            }

            var head = chain.Head with { State = state };
            return chain with { Head = head, Steps = [new Step(head, 0, StepState.Running, string.Empty)] };
        }).ToList();

        return board with { Now = now };
    }
}
