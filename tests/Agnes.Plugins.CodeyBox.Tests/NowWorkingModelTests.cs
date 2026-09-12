using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The wall, without a wall: everything it decides is a pure function, so everything it decides is
/// asserted here rather than looked at in a screenshot.
/// </summary>
public sealed class NowWorkingModelTests
{
    private static readonly DateTimeOffset Now = NowWorkingSamples.Now;

    // ---- slots -----------------------------------------------------------------------------------

    [Fact]
    public void Busy_slots_come_first_longest_running_first_and_the_rest_are_drawn_free()
    {
        var cards = NowWorkingModel.Slots(NowWorkingSamples.Board(), NowWorkingSamples.Overview(), null, Now);

        Assert.Equal(5, cards.Count);
        Assert.Equal(4, cards.Count(c => c.IsBusy));
        Assert.Single(cards, c => c.IsFree);

        // Longest first, and the free one last — capacity you cannot see is capacity you forget.
        var busy = cards.Where(c => c.IsBusy).ToList();
        Assert.Equal("Reworking", busy[0].Phase);
        Assert.Equal("Merging", busy[^1].Phase);
        Assert.True(cards[^1].IsFree);
        Assert.Equal([0, 1, 2, 3, 4], cards.Select(c => c.Index));
    }

    [Fact]
    public void A_card_carries_its_items_audit_trace_and_its_agents_last_lines()
    {
        var cards = NowWorkingModel.Slots(
            NowWorkingSamples.Board(), NowWorkingSamples.Overview(), NowWorkingSamples.Tails(), Now);

        var reworking = cards.Single(c => c.Phase == "Reworking");
        Assert.NotNull(reworking.Trace);
        Assert.True(reworking.HasTrace);
        Assert.Equal(Convergence.Oscillating, reworking.Trace!.Shape);
        Assert.Equal("resolving with the upstream hunk", reworking.Latest);
        Assert.Contains("CONFLICT", reworking.Recent, StringComparison.Ordinal);

        // The item that has not reached audit has no trace, and says so rather than drawing an empty one.
        var working = cards.Single(c => c.Phase == "Working");
        Assert.False(working.HasTrace);
    }

    [Fact]
    public void A_board_with_more_running_items_than_slots_still_draws_every_one()
    {
        // The orchestrator can hold a slot open across a phase boundary, so Now can legitimately exceed
        // the slot count. Dropping a running item because the arithmetic says so would be the one
        // failure this screen cannot afford.
        var board = NowWorkingSamples.Board() with { Slots = (1, 1) };
        var cards = NowWorkingModel.Slots(board, null, null, Now);

        Assert.Equal(4, cards.Count);
        Assert.All(cards, c => Assert.True(c.IsBusy));
    }

    [Fact]
    public void An_item_stamped_in_the_future_does_not_count_up_from_a_negative_number()
    {
        var board = NowWorkingSamples.Board();
        var cards = NowWorkingModel.Slots(board, null, null, Now.AddHours(-3));

        Assert.All(cards.Where(c => c.IsBusy), c => Assert.True(c.Since <= Now.AddHours(-3)));
    }

    // ---- an agent's output -----------------------------------------------------------------------

    [Fact]
    public void Output_is_the_last_lines_with_the_terminal_control_codes_taken_out()
    {
        var lines = NowWorkingModel.Lines(
            "first\n\n\u001b[32mgreen\u001b[0m line\n  0%\r 61%\rdone 100%\ntrailing", 3);

        Assert.Equal(3, lines.Count);
        Assert.Equal("green line", lines[0]);
        // A progress bar redraws its own line; the last redraw is the one worth keeping.
        Assert.Equal("done 100%", lines[1]);
        Assert.Equal("trailing", lines[2]);
        Assert.DoesNotContain(lines, l => l.Contains('\u001b'));
    }

    [Fact]
    public void Nothing_printed_yet_is_no_lines_rather_than_one_empty_one()
    {
        Assert.Empty(NowWorkingModel.Lines(string.Empty, 3));
        Assert.Empty(NowWorkingModel.Lines("   \n\n  ", 3));
    }

    // ---- the ticker ------------------------------------------------------------------------------

    private static CodeyBoxEvent Event(
        long id, string type, string? item = "a1", string? title = "An item", string? state = null,
        int second = 0) => new(id, type, item, "codeybox-self", Now.AddSeconds(second), title, state);

    [Fact]
    public void A_state_change_reads_as_a_transition_once_the_wall_has_seen_the_item_before()
    {
        var cold = NowWorkingModel.Describe(Event(1, "work_item.auditing", state: "Auditing"));
        Assert.Equal("→ Auditing", cold!.Verb);

        var warm = NowWorkingModel.Describe(Event(2, "work_item.auditing", state: "Auditing"), "Working");
        Assert.Equal("Working → Auditing", warm!.Verb);
        Assert.Equal(WallTone.Working, warm.Tone);
        Assert.Equal("An item", warm.Subject);
    }

    [Theory]
    [InlineData("work_item.done", "landed", WallTone.Landed)]
    [InlineData("work_item.failed", "failed", WallTone.Failed)]
    [InlineData("work_item.audit_passed", "audit passed", WallTone.Landed)]
    [InlineData("work_item.auto_retry", "retrying", WallTone.Attention)]
    [InlineData("queue.paused", "queue paused", WallTone.Attention)]
    [InlineData("queue.resumed", "queue resumed", WallTone.Working)]
    public void Each_kind_of_event_gets_its_own_words_and_its_own_hue(string type, string verb, WallTone tone)
    {
        var line = NowWorkingModel.Describe(Event(1, type, state: "Done"));

        Assert.NotNull(line);
        Assert.Equal(verb, line!.Verb);
        Assert.Equal(tone, line.Tone);
    }

    [Fact]
    public void The_phase_level_events_stay_out_of_the_log_and_only_feed_the_heartbeat()
    {
        // One item entering Auditing emits all four of these within the same second. Printing them all
        // says one thing four times in the space of four.
        Assert.Null(NowWorkingModel.Describe(Event(1, "iteration.started", state: null)));
        Assert.Null(NowWorkingModel.Describe(Event(2, "audit.started", state: null)));
        Assert.Null(NowWorkingModel.Describe(Event(3, "audit.findings.emitted", state: null)));
        Assert.Null(NowWorkingModel.Describe(Event(4, "merge.completed", state: null)));
    }

    [Fact]
    public void A_long_title_is_cut_so_the_verb_still_fits_on_the_line()
    {
        var line = NowWorkingModel.Describe(
            Event(1, "work_item.working", title: new string('x', 200), state: "Working"));

        Assert.True(line!.Subject.Length <= 69);
        Assert.EndsWith("…", line.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_keeps_a_dozen_lines_newest_first()
    {
        IReadOnlyList<TickerLine> log = [];
        for (var i = 0; i < 20; i++)
        {
            log = NowWorkingModel.Push(log, new TickerLine(i, Now.AddSeconds(i * 10), $"item {i}", "landed", WallTone.Landed));
        }

        Assert.Equal(NowWorkingModel.TickerDepth, log.Count);
        Assert.Equal("item 19", log[0].Subject);
        Assert.Equal("item 8", log[^1].Subject);
    }

    [Fact]
    public void A_replayed_frame_is_not_printed_twice()
    {
        var line = new TickerLine(7, Now, "an item", "landed", WallTone.Landed);
        var log = NowWorkingModel.Push([], line);

        Assert.Same(log, NowWorkingModel.Push(log, line));
    }

    [Fact]
    public void A_burst_repeating_itself_within_a_second_is_one_line()
    {
        var first = new TickerLine(1, Now, "an item", "→ Auditing", WallTone.Working);
        var again = new TickerLine(2, Now.AddMilliseconds(400), "an item", "→ Auditing", WallTone.Working);
        var later = new TickerLine(3, Now.AddSeconds(30), "an item", "→ Auditing", WallTone.Working);

        var log = NowWorkingModel.Push([], first);
        Assert.Same(log, NowWorkingModel.Push(log, again));

        // Half a minute later it is a real second transition and is printed.
        Assert.Equal(2, NowWorkingModel.Push(log, later).Count);
    }

    // ---- the heartbeat ---------------------------------------------------------------------------

    [Fact]
    public void The_heartbeat_is_always_an_hour_long_even_when_nothing_happened()
    {
        var quiet = NowWorkingModel.Heartbeat([], Now);

        Assert.Equal(60, quiet.Count);
        Assert.All(quiet, b => Assert.Equal(0, b));
    }

    [Fact]
    public void An_event_lands_in_the_minute_it_happened_in()
    {
        var beats = NowWorkingModel.Heartbeat(
        [
            Now,                            // the minute in progress: the last bucket
            Now.AddSeconds(-30),            // still this minute
            Now.AddMinutes(-1),             // the one before
            Now.AddMinutes(-59.5),          // just inside the window
            Now.AddMinutes(-61),            // outside it, and dropped
            Now.AddSeconds(5),              // clock skew forward: still counted, as this minute
        ], Now);

        Assert.Equal(3, beats[^1]);
        Assert.Equal(1, beats[^2]);
        Assert.Equal(1, beats[0]);
        Assert.Equal(5, beats.Sum());
    }

    // ---- the numbers -----------------------------------------------------------------------------

    [Fact]
    public void The_headline_figures_come_from_the_board_the_overview_and_the_item_list()
    {
        var numbers = NowWorkingModel.Numbers(
            NowWorkingSamples.Board(), NowWorkingSamples.Overview(), NowWorkingSamples.Items(), Now);

        var by = numbers.ToDictionary(n => n.Key, StringComparer.Ordinal);

        Assert.Equal(6, by["landed-today"].Value);
        Assert.Equal(4, by["in-flight"].Value);
        Assert.Equal(9, by["waiting"].Value);
        Assert.Equal("4 / 5", by["slots"].Text);
        Assert.Equal("6h 40m", by["drain"].Text);
        Assert.Equal(2, by["needs-you"].Value);

        // Amber is spent only when something is genuinely waiting on a person.
        Assert.Equal(TileTone.Attention, by["needs-you"].Tone);
    }

    [Fact]
    public void Nothing_waiting_on_you_is_not_painted_amber()
    {
        var overview = NowWorkingSamples.Overview();
        var quiet = overview with { Sample = overview.Sample with { BlockedOnYou = 0 } };

        var needsYou = NowWorkingModel
            .Numbers(NowWorkingSamples.Board(), quiet, NowWorkingSamples.Items(), Now)
            .Single(n => n.Key == "needs-you");

        Assert.Equal(TileTone.Neutral, needsYou.Tone);
        Assert.Equal("nothing is waiting on you", needsYou.Caption);
    }

    [Fact]
    public void A_figure_the_host_cannot_price_is_left_out_rather_than_shown_as_zero()
    {
        var overview = NowWorkingSamples.Overview() with { Burn = null };
        var numbers = NowWorkingModel.Numbers(NowWorkingSamples.Board(), overview, [], Now);

        Assert.DoesNotContain(numbers, n => n.Key == "drain");
        Assert.DoesNotContain(numbers, n => n.Key == "cost");
    }

    [Fact]
    public void A_sparkline_is_drawn_only_where_there_is_a_real_series_behind_it()
    {
        var numbers = NowWorkingModel.Numbers(
            NowWorkingSamples.Board(), NowWorkingSamples.Overview(), NowWorkingSamples.Items(), Now);

        Assert.True(numbers.Single(n => n.Key == "landed-today").HasSpark);
        Assert.False(numbers.Single(n => n.Key == "waiting").HasSpark);
    }

    [Theory]
    [InlineData(34, NumberFormat.Count, null, "34")]
    [InlineData(12.4, NumberFormat.Money, null, "$12.40")]
    [InlineData(412.9, NumberFormat.Money, null, "$413")]
    [InlineData(400, NumberFormat.Duration, null, "6h 40m")]
    [InlineData(48, NumberFormat.Duration, null, "48m")]
    [InlineData(3000, NumberFormat.Duration, null, "2d 02h")]
    // A drained queue: short enough to fit the tile, which "under a minute" was not.
    [InlineData(0.4, NumberFormat.Duration, null, "< 1m")]
    [InlineData(2, NumberFormat.Ratio, 3d, "2 / 3")]
    public void Each_figure_is_written_the_way_a_person_says_it(
        double value, NumberFormat format, double? of, string expected)
        => Assert.Equal(expected, NowWorkingModel.Format(value, format, of));

    [Theory]
    [InlineData(42, "42s")]
    [InlineData(252, "4m 12s")]
    [InlineData(3840, "1h 04m")]
    public void The_elapsed_timer_reads_at_the_scale_it_has_reached(int seconds, string expected)
        => Assert.Equal(expected, NowWorkingModel.Elapsed(TimeSpan.FromSeconds(seconds)));

    // ---- motion ----------------------------------------------------------------------------------

    [Fact]
    public void A_count_up_starts_where_it_was_and_arrives_exactly()
    {
        var life = TimeSpan.FromMilliseconds(600);

        Assert.Equal(12, NowWorkingModel.CountUp(12, 34, TimeSpan.Zero, life));
        Assert.Equal(34, NowWorkingModel.CountUp(12, 34, life, life));
        Assert.Equal(34, NowWorkingModel.CountUp(12, 34, life + life, life));

        // Eased out, so it is most of the way there at the halfway point and settles rather than stops.
        var half = NowWorkingModel.CountUp(12, 34, TimeSpan.FromMilliseconds(300), life);
        Assert.InRange(half, 25, 33);

        // And monotone, which is what stops a number visibly going backwards on its way up.
        var previous = 12.0;
        for (var ms = 0; ms <= 600; ms += 30)
        {
            var at = NowWorkingModel.CountUp(12, 34, TimeSpan.FromMilliseconds(ms), life);
            Assert.True(at >= previous);
            previous = at;
        }
    }

    [Fact]
    public void A_number_going_down_counts_down()
        => Assert.InRange(
            NowWorkingModel.CountUp(34, 12, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600)),
            13, 21);

    [Fact]
    public void A_flash_is_full_when_it_fires_and_gone_when_it_has_decayed()
    {
        var life = TimeSpan.FromMilliseconds(1500);

        Assert.Equal(1, NowWorkingModel.Decay(TimeSpan.Zero, life));
        Assert.Equal(0.5, NowWorkingModel.Decay(TimeSpan.FromMilliseconds(750), life), 3);
        Assert.Equal(0, NowWorkingModel.Decay(life, life));
        Assert.Equal(0, NowWorkingModel.Decay(TimeSpan.FromHours(1), life));
    }
}
