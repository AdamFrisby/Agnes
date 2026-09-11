using System.Globalization;

using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The overview's derivations, against the shapes a real fleet of autonomous agents produces: long audit
/// loops that are working, short ones that are not, items stopped for a reason and items stopped for
/// none. The thing under test throughout is that <b>iteration count is never the signal</b> — direction
/// is — and that a stopped item is only a defect when nobody can say why it stopped.
/// </summary>
public sealed class OverviewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------------------------
    // Builders. Every test names only the fields it is about.
    // -------------------------------------------------------------------------------------------

    private static WorkItemRow Item(
        string state = "Working",
        string id = "item-1",
        bool deps = true,
        int priority = 0,
        DateTimeOffset? updated = null,
        DateTimeOffset? created = null,
        string? agent = "claude",
        string? project = "codeybox-self",
        IReadOnlyList<string>? dependsOn = null,
        string? failureKind = null,
        string? lastError = null,
        DateTimeOffset? nextQuotaRetry = null,
        DateTimeOffset? nextTransientRetry = null,
        DateTimeOffset? quotaReset = null)
        => new(
            Id: id,
            Title: id,
            State: state,
            Agent: agent,
            ProjectId: project,
            QueuePosition: 0,
            UpdatedAt: updated ?? Now,
            LastError: lastError,
            DependsOn: dependsOn,
            Priority: priority,
            CreatedAt: created ?? default,
            DependsOnSatisfied: deps,
            FailureKind: failureKind,
            NextQuotaRetryAt: nextQuotaRetry,
            NextTransientRetryAt: nextTransientRetry,
            QuotaResetAt: quotaReset);

    private static AuditProgressRow Audit(
        int iteration,
        int blocking,
        DateTimeOffset at,
        string status = "complete",
        int max = 0,
        string[]? gate = null,
        string attempt = "attempt-1")
        => new(
            Id: FormattableString.Invariant($"{attempt}-{iteration}"),
            WorkAttemptKey: attempt,
            Iteration: iteration,
            MaxIterations: max,
            Status: status,
            BlockingFindings: blocking,
            NonBlockingFindings: 0,
            RecordedAt: at,
            ScheduledAuditors: gate,
            CompletedAuditors: gate,
            BlockingFindingsDetails: gate is null
                ? null
                : [.. gate.Select(g => new AuditProgressFinding(g, "Error", "t", "d", 1, false, null))],
            Findings: null,
            Truncated: false);

    /// <summary>An audit loop as a list of blocking-finding counts, one per iteration.</summary>
    private static ItemAuditProgress Series(
        string itemId,
        DateTimeOffset baseAt,
        int max = 0,
        int firstIteration = 1,
        string? gate = null,
        params int[] blocking)
        => new(itemId,
        [
            .. blocking.Select((b, i) => Audit(
                firstIteration + i,
                b,
                baseAt.AddMinutes(i),
                max: max,
                gate: gate is null ? null : b > 0 ? [gate] : null))
        ]);

    private static QuotaProbe Probe(
        string agent,
        bool? wouldAllow,
        bool paused = false,
        int? availablePct = null,
        DateTimeOffset? resetAt = null)
        => new(
            Agent: agent,
            ModelId: null,
            Billing: null,
            Paused: paused,
            PausedReason: null,
            WouldAllow: wouldAllow,
            ObservedFailures: null,
            LatestSnapshot: availablePct is null && resetAt is null
                ? null
                : new QuotaSnapshot(availablePct, availablePct is not null, resetAt));

    private static OverviewInputs Inputs(
        IReadOnlyList<WorkItemRow>? items = null,
        IReadOnlyList<ItemAuditProgress>? progress = null,
        IReadOnlyDictionary<string, int>? questions = null,
        QueueStatus? queue = null,
        Concurrency? concurrency = null,
        IReadOnlyList<QuotaProbe>? probes = null,
        IReadOnlyList<QuotaBurn>? quotaHistory = null,
        TransitionHealth? health = null,
        IReadOnlyList<OverviewSample>? history = null,
        IReadOnlyDictionary<string, int>? ceilings = null,
        DateTimeOffset? now = null)
        => new(
            now ?? Now,
            items ?? [],
            progress ?? [],
            questions ?? new Dictionary<string, int>(),
            queue,
            concurrency,
            probes ?? [],
            quotaHistory ?? [],
            health,
            history ?? [],
            ceilings ?? new Dictionary<string, int>());

    private static ItemTrace Only(Overview overview)
        => overview.Attention.Concat(overview.Healthy).Concat(overview.Folded.SelectMany(g => g.Items)).Single();

    private static string At(DateTimeOffset when)
        => when.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    // -------------------------------------------------------------------------------------------
    // 1. The five trace shapes
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_descending_loop_is_converging_however_many_iterations_it_has_taken()
    {
        // Twelve iterations is not a problem on this fleet — it is the median. What matters is that the
        // findings are coming down.
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddMinutes(-5))],
            progress: [Series("item-1", Now.AddMinutes(-60), blocking: [9, 8, 6, 6, 4, 3, 3, 2])]));

        var trace = Only(overview);

        Assert.Equal(Convergence.Converging, trace.Shape);
        Assert.Equal(Motion.Moving, trace.Motion);
        Assert.False(trace.NeedsAttention);
        Assert.Equal(string.Empty, trace.Why);
        Assert.Equal(8, trace.LastIteration);
    }

    [Fact]
    public void A_sawtooth_on_the_same_gate_is_oscillating_and_names_the_gate()
    {
        // The one kind of repetition that is waste: the rework lands, the same auditor objects again.
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Reworking", updated: Now.AddMinutes(-5))],
            progress: [Series("item-1", Now.AddMinutes(-50), gate: "security-auditor", blocking: [3, 1, 3, 1, 3])]));

        var trace = Only(overview);

        Assert.Equal(Convergence.Oscillating, trace.Shape);
        Assert.True(trace.NeedsAttention);
        Assert.Equal("same gate repeating: security-auditor", trace.Why);
        Assert.Equal(OverviewModel.RankOscillating, trace.Rank);
    }

    [Fact]
    public void A_flat_non_zero_loop_is_stuck()
    {
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddMinutes(-5))],
            progress: [new ItemAuditProgress("item-1",
            [
                Audit(1, 4, Now.AddMinutes(-40), gate: ["a"]),
                Audit(2, 4, Now.AddMinutes(-30), gate: ["b"]),
                Audit(3, 4, Now.AddMinutes(-20), gate: ["c"]),
            ])]));

        var trace = Only(overview);

        Assert.Equal(Convergence.Stuck, trace.Shape);
        Assert.Equal("same 4 findings for 3 iterations", trace.Why);
        Assert.Equal(OverviewModel.RankStuck, trace.Rank);
    }

    [Fact]
    public void A_short_loop_has_no_shape_yet()
    {
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddMinutes(-5))],
            progress: [Series("item-1", Now.AddMinutes(-20), blocking: [5, 4])]));

        var trace = Only(overview);

        Assert.Equal(Convergence.New, trace.Shape);
        Assert.False(trace.NeedsAttention);
    }

    [Fact]
    public void Descent_is_read_two_ways_because_a_real_loop_is_noisy()
    {
        static IReadOnlyList<TracePoint> Points(params int[] blocking)
            => [.. blocking.Select((b, i) => new TracePoint(i + 1, b, true, false))];

        // The latest iteration beat the worst of the three before it.
        Assert.True(OverviewModel.IsConverging(Points(9, 7, 8, 5)));

        // Or the last three did not go up, and something in the last five genuinely came down.
        Assert.True(OverviewModel.IsConverging(Points(9, 6, 4, 4, 4)));

        // A rise at the end is not a descent, and neither is a flat run with nothing behind it.
        Assert.False(OverviewModel.IsConverging(Points(3, 2, 5)));
        Assert.False(OverviewModel.IsConverging(Points(4, 4, 4)));
        Assert.False(OverviewModel.IsConverging(Points(5, 4)));
    }

    [Fact]
    public void A_passing_last_iteration_is_passed_not_converging()
    {
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "AuditPassed", updated: Now.AddMinutes(-5))],
            progress: [Series("item-1", Now.AddMinutes(-30), blocking: [4, 2, 0])]));

        Assert.Equal(Convergence.Passed, Only(overview).Shape);
    }

    [Fact]
    public void A_live_phase_that_has_gone_quiet_is_wedged()
    {
        // The operator's own rule: judge by timestamps advancing, not by the Done counter.
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddMinutes(-72))],
            progress: [Series("item-1", Now.AddMinutes(-200), blocking: [6, 5, 4])]));

        var trace = Only(overview);

        Assert.Equal(Motion.Wedged, trace.Motion);
        Assert.Equal("quiet for 1h 12m", trace.Why);
        Assert.Equal(OverviewModel.RankWedged, trace.Rank);
    }

    [Fact]
    public void A_fresh_audit_row_counts_as_movement_even_when_the_state_has_not_changed()
    {
        // An audit that takes an hour is patience, not a wedge, and the audit rows are the proof.
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddHours(-3))],
            progress: [Series("item-1", Now.AddMinutes(-10), blocking: [6, 5, 4])]));

        Assert.Equal(Motion.Moving, Only(overview).Motion);
    }

    [Fact]
    public void The_latest_recording_of_an_iteration_wins_over_an_earlier_attempt()
    {
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddMinutes(-5))],
            progress: [new ItemAuditProgress("item-1",
            [
                Audit(1, 9, Now.AddMinutes(-40), attempt: "old"),
                Audit(1, 2, Now.AddMinutes(-10), attempt: "new"),
                Audit(2, 1, Now.AddMinutes(-5), attempt: "new"),
            ])]));

        var trace = Only(overview);

        Assert.Equal(2, trace.Points.Count);
        Assert.Equal(2, trace.Points[0].BlockingFindings);
    }

    // -------------------------------------------------------------------------------------------
    // 2. Motion, and the line each class shows
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_quota_wait_with_a_known_resume_is_parked_and_says_when()
    {
        var resume = Now.AddMinutes(37);
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "WaitingForQuotaReset", updated: Now.AddHours(-4), nextQuotaRetry: resume)]));

        var trace = Only(overview);

        Assert.Equal(Motion.Parked, trace.Motion);
        Assert.Equal($"resumes {At(resume)}", trace.Why);
        Assert.False(trace.NeedsPerson);
        Assert.Equal(OverviewModel.RankParked, trace.Rank);
    }

    [Fact]
    public void The_soonest_resume_is_the_one_that_governs()
    {
        var soon = Now.AddMinutes(5);
        var overview = OverviewModel.Build(Inputs(
            items:
            [
                Item(state: "Working", updated: Now.AddHours(-4),
                    nextTransientRetry: soon, quotaReset: Now.AddHours(3)),
            ]));

        // A retry pending in the future parks the item whatever state it claims to be in — and a live
        // phase with a published resume must never be reported as a wedge.
        Assert.Equal(Motion.Parked, Only(overview).Motion);
        Assert.Equal($"resumes {At(soon)}", Only(overview).Why);
    }

    [Fact]
    public void A_park_with_no_published_time_still_says_what_it_is_waiting_for()
    {
        var paused = OverviewModel.Build(Inputs(
            items: [Item(state: "WaitingForAgentResume", updated: Now.AddHours(-4))]));
        Assert.Equal("agent paused", Only(paused).Why);

        var quota = OverviewModel.Build(Inputs(
            items: [Item(state: "WaitingForQuotaReset", updated: Now.AddHours(-4))]));
        Assert.Equal("waiting for quota", Only(quota).Why);

        var transient = OverviewModel.Build(Inputs(
            items: [Item(state: "WaitingForTransientRetry", updated: Now.AddHours(-4))]));
        Assert.Equal("waiting to retry", Only(transient).Why);
    }

    [Fact]
    public void A_dependency_block_counts_the_dependencies()
    {
        var two = OverviewModel.Build(Inputs(
            items: [Item(state: "Queued", deps: false, dependsOn: ["a", "b"])]));
        var trace = Only(two);

        Assert.Equal(Motion.Blocked, trace.Motion);
        Assert.False(trace.NeedsPerson);
        Assert.Equal("waiting on 2 dependencies", trace.Why);
        Assert.Equal(OverviewModel.RankBlocked, trace.Rank);

        var one = OverviewModel.Build(Inputs(
            items: [Item(state: "Queued", deps: false, dependsOn: ["a"])]));
        Assert.Equal("waiting on 1 dependency", Only(one).Why);
    }

    [Fact]
    public void An_operator_question_is_blocked_on_a_person()
    {
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "NeedsOperatorInput", updated: Now.AddHours(-6))],
            questions: new Dictionary<string, int> { ["item-1"] = 3 }));

        var trace = Only(overview);

        Assert.Equal(Motion.Blocked, trace.Motion);
        Assert.True(trace.NeedsPerson);
        Assert.Equal("3 open questions", trace.Why);
        Assert.Equal(OverviewModel.RankNeedsPerson, trace.Rank);
    }

    [Fact]
    public void A_failed_item_is_kept_because_it_is_waiting_on_a_decision()
    {
        // Terminal to the orchestrator, unfinished to a person: it is the one terminal state that still
        // belongs on an overview.
        var kinded = OverviewModel.Build(Inputs(
            items: [Item(state: "Failed", failureKind: "InfraTimeout")]));
        Assert.Equal("failed: InfraTimeout", Only(kinded).Why);
        Assert.True(Only(kinded).NeedsPerson);

        var errored = OverviewModel.Build(Inputs(
            items: [Item(state: "AuditFailed", lastError: "the auditor process exited with 137\nstack…")]));
        Assert.Equal("failed: the auditor process exited with 137", Only(errored).Why);

        var silent = OverviewModel.Build(Inputs(items: [Item(state: "Failed")]));
        Assert.Equal("needs a decision", Only(silent).Why);
    }

    [Fact]
    public void Finished_history_is_not_on_the_overview_at_all()
    {
        // 372 of 404 items on the instance this was designed against are finished. None of them is news.
        var overview = OverviewModel.Build(Inputs(
            items:
            [
                Item(state: "Done", id: "done"),
                Item(state: "Cancelled", id: "cancelled"),
                Item(state: "Working", id: "live"),
            ]));

        Assert.Equal("live", Only(overview).Item.Id);
    }

    [Fact]
    public void A_runnable_queued_item_is_moving_until_it_has_waited_long_enough_to_be_worth_saying()
    {
        var fresh = OverviewModel.Build(Inputs(
            items: [Item(state: "Queued", updated: Now.AddMinutes(-4))]));
        Assert.Equal(Motion.Moving, Only(fresh).Motion);
        Assert.Equal(string.Empty, Only(fresh).Why);
        Assert.False(Only(fresh).NeedsAttention);

        var stale = OverviewModel.Build(Inputs(
            items: [Item(state: "Queued", updated: Now.AddMinutes(-90))]));
        Assert.Equal(Motion.Moving, Only(stale).Motion);
        Assert.Equal("waiting for a slot", Only(stale).Why);
    }

    // -------------------------------------------------------------------------------------------
    // 3. The ceiling
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Converging_work_close_to_the_cap_is_flagged_before_it_is_thrown_away()
    {
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddMinutes(-5))],
            progress: [Series("item-1", Now.AddMinutes(-30), max: 25, firstIteration: 21, blocking: [5, 4, 3])]));

        var trace = Only(overview);

        Assert.Equal(25, trace.Ceiling);
        Assert.True(trace.NearCeiling);
        Assert.Equal("iteration 23 of 25, converging", trace.Why);
        Assert.Equal(OverviewModel.RankNearCeiling, trace.Rank);
    }

    [Fact]
    public void The_ceiling_falls_back_to_the_project_cap_when_the_rows_do_not_carry_one()
    {
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddMinutes(-5))],
            progress: [Series("item-1", Now.AddMinutes(-30), firstIteration: 8, blocking: [5, 4, 3])],
            ceilings: new Dictionary<string, int> { ["codeybox-self"] = 10 }));

        Assert.Equal(10, Only(overview).Ceiling);
        Assert.True(Only(overview).NearCeiling);
    }

    [Fact]
    public void Work_far_from_the_cap_is_not_flagged_and_neither_is_work_that_is_not_going_anywhere()
    {
        var early = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddMinutes(-5))],
            progress: [Series("item-1", Now.AddMinutes(-30), max: 25, blocking: [5, 4, 3])]));
        Assert.False(Only(early).NearCeiling);

        // Near-ceiling is an argument for extending the cap, which only makes sense while the loop is
        // still heading somewhere. A stuck loop at the cap should be stopped, not extended.
        var stuck = OverviewModel.Build(Inputs(
            items: [Item(state: "Auditing", updated: Now.AddMinutes(-5))],
            progress: [Series("item-1", Now.AddMinutes(-30), max: 25, firstIteration: 21, blocking: [4, 4, 4])]));
        Assert.False(Only(stuck).NearCeiling);
        Assert.Equal(Convergence.Stuck, Only(stuck).Shape);
    }

    // -------------------------------------------------------------------------------------------
    // 4. Ranking
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_attention_band_is_worst_first_and_healthy_work_is_not_in_it()
    {
        var overview = OverviewModel.Build(Inputs(
            items:
            [
                Item(id: "moving", state: "Auditing", updated: Now.AddMinutes(-5)),
                Item(id: "dependency", state: "Queued", deps: false, dependsOn: ["x"]),
                Item(id: "parked", state: "WaitingForQuotaReset", updated: Now.AddHours(-4)),
                Item(id: "person", state: "Failed", failureKind: "Infra"),
                Item(id: "stuck", state: "Auditing", updated: Now.AddMinutes(-5)),
                Item(id: "ceiling", state: "Auditing", updated: Now.AddMinutes(-5)),
                Item(id: "oscillating", state: "Reworking", updated: Now.AddMinutes(-5)),
                Item(id: "wedged", state: "Merging", updated: Now.AddHours(-5)),
            ],
            progress:
            [
                Series("moving", Now.AddMinutes(-30), blocking: [9, 6, 3]),
                Series("stuck", Now.AddMinutes(-30), blocking: [4, 4, 4]),
                Series("ceiling", Now.AddMinutes(-30), max: 25, firstIteration: 21, blocking: [5, 4, 3]),
                Series("oscillating", Now.AddMinutes(-30), gate: "style", blocking: [3, 1, 3, 1, 3]),
            ]));

        Assert.Equal(
            ["wedged", "oscillating", "ceiling", "stuck", "person"],
            overview.Attention.Select(t => t.Item.Id));
        // Parked and dependency-blocked items are the pipeline's own waits: folded, one line each.
        Assert.Equal(
            ["1 item parked until quota or a retry", "1 item waiting on a dependency"],
            overview.Folded.Select(g => g.Title));
        Assert.Equal(["moving"], overview.Healthy.Select(t => t.Item.Id));
        Assert.Equal("1 item converging normally", overview.HealthyLabel);
    }

    [Fact]
    public void Rows_of_equal_rank_are_ordered_by_how_long_they_have_been_still()
    {
        var overview = OverviewModel.Build(Inputs(
            items:
            [
                Item(id: "recent", state: "Merging", updated: Now.AddMinutes(-50)),
                Item(id: "ancient", state: "Merging", updated: Now.AddHours(-9)),
                Item(id: "middling", state: "Merging", updated: Now.AddHours(-2)),
            ]));

        Assert.Equal(["ancient", "middling", "recent"], overview.Attention.Select(t => t.Item.Id));
    }

    // -------------------------------------------------------------------------------------------
    // 5. Vitals
    // -------------------------------------------------------------------------------------------

    private static IReadOnlyList<OverviewSample> Samples(int count, Func<int, int> landed)
        =>
        [
            .. Enumerable.Range(0, count).Select(i => new OverviewSample(
                At: Now.AddHours(-(count - i) - 1),
                Landed7d: landed(i),
                InMotion: 3,
                Parked: 0,
                Blocked: 0,
                Wedged: 0,
                EligibleAgents: 2,
                SlotsBusy: 1,
                SlotsTotal: 2,
                InfraFailureRate: 0.05,
                BlockedOnYou: 0))
        ];

    [Fact]
    public void With_no_history_a_vital_has_no_band_and_no_trend()
    {
        var landed = OverviewModel.Build(Inputs()).Vitals[0];

        Assert.Equal("Landed this week", landed.Label);
        Assert.False(landed.HasBand);
        Assert.Equal(Trend.Unknown, landed.Trend);
        Assert.Equal("no history yet", landed.Caption);
        Assert.True(landed.IsNeutral);
    }

    [Fact]
    public void A_band_is_the_fleets_own_trailing_norm_and_a_quiet_week_is_never_reported_as_bad()
    {
        var history = Samples(30, i => 16 + (i % 5));

        var quiet = OverviewModel.Build(Inputs(history: history)).Vitals[0];

        Assert.Equal(18, quiet.Median);
        Assert.Equal(16, quiet.BandLow);
        Assert.Equal(19, quiet.BandHigh);
        Assert.Equal("vs a usual 18 a week", quiet.Caption);
        Assert.Equal("0", quiet.Value);
        Assert.Equal(Trend.Down, quiet.Trend);

        // Below the band is worth noticing. It is not a failure, and colouring it as one would teach the
        // operator to ignore the colour that does mean failure.
        Assert.True(quiet.IsAttention);
        Assert.False(quiet.IsBad);

        // 31 readings: the trailing 30 plus the one being taken now.
        Assert.Equal(31, quiet.Spark.Count);
        Assert.Equal(0, quiet.Spark[^1]);
    }

    [Fact]
    public void A_reading_inside_the_band_is_ordinary()
    {
        var items = Enumerable.Range(0, 18)
            .Select(i => Item(id: FormattableString.Invariant($"done-{i}"), state: "Done", updated: Now.AddDays(-1)))
            .ToArray();

        var landed = OverviewModel.Build(Inputs(items: items, history: Samples(30, i => 16 + (i % 5)))).Vitals[0];

        Assert.Equal("18", landed.Value);
        Assert.Equal(Trend.Flat, landed.Trend);
        Assert.True(landed.IsNeutral);
    }

    [Fact]
    public void Only_settled_samples_build_the_band_so_the_current_reading_cannot_move_it()
    {
        // Thirty readings taken in the last few minutes are not a norm, they are today.
        var churn = Enumerable.Range(0, 30)
            .Select(i => new OverviewSample(Now.AddMinutes(-i), 3, 3, 0, 0, 0, 2, 1, 2, 0.05, 0))
            .ToArray();

        var landed = OverviewModel.Build(Inputs(history: churn)).Vitals[0];

        Assert.False(landed.HasBand);
        Assert.Equal(Trend.Unknown, landed.Trend);
    }

    [Fact]
    public void Landed_this_week_counts_only_the_last_seven_days()
    {
        var overview = OverviewModel.Build(Inputs(items:
        [
            Item(id: "a", state: "Done", updated: Now.AddDays(-2)),
            Item(id: "b", state: "Done", updated: Now.AddDays(-6)),
            Item(id: "c", state: "Done", updated: Now.AddDays(-9)),
        ]));

        Assert.Equal("2", overview.Vitals[0].Value);
    }

    [Fact]
    public void In_motion_breaks_the_stopped_items_down_and_leads_with_the_worst_of_them()
    {
        var overview = OverviewModel.Build(Inputs(items:
        [
            Item(id: "m1", state: "Working"),
            Item(id: "m2", state: "Working"),
            Item(id: "wedged", state: "Merging", updated: Now.AddHours(-5)),
            Item(id: "parked", state: "WaitingForQuotaReset", updated: Now.AddHours(-5)),
            Item(id: "blocked", state: "Queued", deps: false, dependsOn: ["x"]),
        ]));

        var motion = overview.Vitals[1];

        Assert.Equal("In motion", motion.Label);
        Assert.Equal("2 moving · 3 stopped", motion.Value);
        Assert.Equal("1 wedged · 1 parked · 1 blocked", motion.Caption);
        Assert.True(motion.IsBad);
    }

    [Fact]
    public void Nothing_stopped_says_so_rather_than_listing_three_zeroes()
    {
        var overview = OverviewModel.Build(Inputs(items: [Item(state: "Working")]));
        var motion = overview.Vitals[1];

        Assert.Equal("1 moving · 0 stopped", motion.Value);
        Assert.Equal("nothing stopped", motion.Caption);
        Assert.True(motion.IsActive);
    }

    [Fact]
    public void Eligible_capacity_names_when_the_gated_agent_comes_back()
    {
        var reset = Now.AddHours(6);
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Queued")],
            concurrency: new Concurrency(2, 1, null),
            probes:
            [
                Probe("claude", wouldAllow: true, availablePct: 80),
                Probe("codex", wouldAllow: false, availablePct: 4, resetAt: reset),
            ]));

        var capacity = overview.Vitals[2];

        Assert.Equal("Eligible capacity", capacity.Label);
        Assert.Equal("1 of 2 agents · 1/2 slots", capacity.Value);
        Assert.Equal($"next reset {At(reset)} (codex)", capacity.Caption);
    }

    [Fact]
    public void No_eligible_agent_with_runnable_work_is_the_worst_capacity_can_read()
    {
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Queued")],
            probes: [Probe("claude", wouldAllow: false, availablePct: 0, resetAt: Now.AddHours(2))]));

        Assert.True(overview.Vitals[2].IsBad);
        Assert.Equal("0 of 1 agents", overview.Vitals[2].Value);
    }

    [Fact]
    public void Capacity_with_nothing_probed_says_so_and_with_everything_eligible_says_that()
    {
        Assert.Equal("no probes", OverviewModel.Build(Inputs()).Vitals[2].Caption);

        var all = OverviewModel.Build(Inputs(probes: [Probe("claude", wouldAllow: true, availablePct: 90)]));
        Assert.Equal("all agents eligible", all.Vitals[2].Caption);
    }

    [Fact]
    public void An_unmeasured_window_is_not_reported_as_healthy()
    {
        // The orchestrator scores an empty window a perfect 1.0; "0%" would read as a clean bill of
        // health for a pipeline that has simply not run.
        var overview = OverviewModel.Build(Inputs(health: new TransitionHealth(1.0, 0, 0, null)));
        var infra = overview.Vitals[3];

        Assert.Equal("Infra failures", infra.Label);
        Assert.Equal("—", infra.Value);
        Assert.Equal("nothing to measure", infra.Caption);
        Assert.True(infra.IsNeutral);
    }

    [Fact]
    public void Infra_failures_are_a_rate_over_a_stated_number_of_transitions()
    {
        var overview = OverviewModel.Build(Inputs(health: new TransitionHealth(0.92, 0.08, 155, "Work")));
        var infra = overview.Vitals[3];

        Assert.Equal("8%", infra.Value);
        Assert.Equal("over 155 transitions, worst: Work", infra.Caption);
        Assert.True(infra.IsNeutral);

        var attention = OverviewModel.Build(Inputs(health: new TransitionHealth(0.8, 0.18, 155, "Audit")));
        Assert.True(attention.Vitals[3].IsAttention);

        var bad = OverviewModel.Build(Inputs(health: new TransitionHealth(0.6, 0.4, 155, "Audit")));
        Assert.True(bad.Vitals[3].IsBad);
    }

    [Fact]
    public void Leaving_the_bands_upper_edge_is_bad_even_at_a_rate_that_would_otherwise_be_fine()
    {
        var settled = Enumerable.Range(0, 30)
            .Select(i => new OverviewSample(Now.AddHours(-i - 2), 18, 3, 0, 0, 0, 2, 1, 2, 0.01, 0))
            .ToArray();

        var overview = OverviewModel.Build(Inputs(
            health: new TransitionHealth(0.94, 0.06, 155, "Work"),
            history: settled));

        // 6% is under every absolute threshold here and six times this fleet's own norm.
        Assert.True(overview.Vitals[3].IsBad);
    }

    [Fact]
    public void Blocked_on_you_counts_the_rows_that_actually_want_a_person()
    {
        var overview = OverviewModel.Build(Inputs(
            items:
            [
                Item(id: "q", state: "NeedsOperatorInput"),
                Item(id: "f", state: "Failed", failureKind: "Infra"),
                Item(id: "d", state: "Queued", deps: false, dependsOn: ["x"]),
            ],
            questions: new Dictionary<string, int> { ["q"] = 2 }));

        var blocked = overview.Vitals[4];

        Assert.Equal("Blocked on you", blocked.Label);
        Assert.Equal("2", blocked.Value);
        Assert.Equal("questions and failed items awaiting a decision", blocked.Caption);
        Assert.True(blocked.IsAttention);

        var quiet = OverviewModel.Build(Inputs()).Vitals[4];
        Assert.Equal("nothing needs you", quiet.Caption);
        Assert.True(quiet.IsNeutral);
    }

    [Fact]
    public void There_are_exactly_five_vitals_in_reading_order()
    {
        Assert.Equal(
            ["Landed this week", "In motion", "Eligible capacity", "Infra failures", "Blocked on you"],
            OverviewModel.Build(Inputs()).Vitals.Select(v => v.Label));
    }

    // -------------------------------------------------------------------------------------------
    // 6. The sentence
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_paused_queue_leads_and_says_since_when_and_why()
    {
        var pausedAt = Now.AddHours(-3);
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Queued")],
            queue: new QueueStatus("Paused", pausedAt, "budget review")));

        Assert.StartsWith($"Paused since {At(pausedAt)}: budget review.", overview.Sentence, StringComparison.Ordinal);
        Assert.Equal(TileTone.Bad, overview.Verdict);

        // And the line still ends on what is actually happening.
        Assert.EndsWith("landed this week.", overview.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wedge_leads_over_everything_except_a_paused_queue()
    {
        var overview = OverviewModel.Build(Inputs(items:
        [
            Item(id: "w", state: "Merging", updated: Now.AddHours(-4)),
            Item(id: "r", state: "Working"),
        ]));

        Assert.StartsWith("1 item wedged.", overview.Sentence, StringComparison.Ordinal);
        Assert.Equal(TileTone.Bad, overview.Verdict);
    }

    [Fact]
    public void No_eligible_agent_with_work_to_do_is_named_as_quota_bound()
    {
        var reset = Now.AddHours(6);
        var overview = OverviewModel.Build(Inputs(
            items: [Item(state: "Queued")],
            probes: [Probe("codex", wouldAllow: false, availablePct: 2, resetAt: reset)]));

        Assert.Equal(
            $"Quota-bound. No agent is eligible; next reset {At(reset)} (codex). 1 queued, nothing in flight, 0 landed this week.",
            overview.Sentence);
        Assert.Equal(TileTone.Bad, overview.Verdict);
    }

    [Fact]
    public void Full_slots_with_work_waiting_is_capacity_bound_which_is_not_a_fault()
    {
        var overview = OverviewModel.Build(Inputs(
            items:
            [
                Item(id: "a", state: "Working", agent: "claude"),
                Item(id: "b", state: "Working", agent: "codex"),
                Item(id: "c", state: "Queued"),
                Item(id: "d", state: "Queued"),
                Item(id: "e", state: "Queued"),
                Item(id: "f", state: "Queued"),
                Item(id: "g", state: "Queued"),
            ],
            concurrency: new Concurrency(2, 2, null),
            probes: [Probe("claude", wouldAllow: true, availablePct: 60)]));

        Assert.Equal(
            "Capacity-bound. 2 of 2 slots busy, 5 runnable waiting. 2 in flight, 0 landed this week.",
            overview.Sentence);
        Assert.Equal(TileTone.Active, overview.Verdict);
    }

    [Fact]
    public void A_queue_that_is_entirely_dependency_blocked_says_that_resuming_it_would_start_nothing()
    {
        // The state the old dashboard was built for: ten queued, every one of them waiting.
        var items = Enumerable.Range(0, 10)
            .Select(i => Item(id: FormattableString.Invariant($"q{i}"), state: "Queued", deps: false, dependsOn: ["x"]))
            .ToArray();

        var overview = OverviewModel.Build(Inputs(
            items: items,
            probes: [Probe("claude", wouldAllow: true, availablePct: 90)]));

        Assert.Equal(
            "Dependency-bound. All 10 queued items wait on something that has not finished. 10 queued, nothing in flight, 0 landed this week.",
            overview.Sentence);
        Assert.Equal(TileTone.Bad, overview.Verdict);
    }

    [Fact]
    public void Work_that_needs_a_person_is_addressed_to_them()
    {
        var overview = OverviewModel.Build(Inputs(
            items:
            [
                Item(id: "q1", state: "NeedsOperatorInput"),
                Item(id: "f1", state: "Failed", failureKind: "Infra"),
            ],
            questions: new Dictionary<string, int> { ["q1"] = 2 }));

        Assert.StartsWith("Waiting on you: 2 questions, 1 failed item.", overview.Sentence, StringComparison.Ordinal);
        Assert.Equal(TileTone.Attention, overview.Verdict);
    }

    [Fact]
    public void An_empty_fleet_says_it_is_idle_rather_than_saying_nothing()
    {
        var overview = OverviewModel.Build(Inputs());

        Assert.Equal("Idle. Nothing queued.", overview.Sentence);
        Assert.Equal(TileTone.Neutral, overview.Verdict);
    }

    [Fact]
    public void A_healthy_fleet_says_what_it_is_doing_and_on_what()
    {
        var done = Enumerable.Range(0, 12)
            .Select(i => Item(id: FormattableString.Invariant($"d{i}"), state: "Done", updated: Now.AddDays(-2)));

        var running = new[]
        {
            Item(id: "r1", state: "Working", agent: "claude"),
            Item(id: "r2", state: "Working", agent: "antigravity"),
            Item(id: "r3", state: "Auditing", agent: "claude"),
        };

        var overview = OverviewModel.Build(Inputs(
            items: [.. done, .. running],
            probes: [Probe("claude", wouldAllow: true, availablePct: 70)]));

        Assert.Equal("Moving. 3 in flight on antigravity and claude, 12 landed this week.", overview.Sentence);
        Assert.Equal(TileTone.Active, overview.Verdict);
    }

    [Fact]
    public void The_sentence_never_runs_past_three_clauses()
    {
        var overview = OverviewModel.Build(Inputs(
            items:
            [
                Item(id: "w", state: "Merging", updated: Now.AddHours(-4)),
                Item(id: "q", state: "Queued"),
            ],
            queue: new QueueStatus("Paused", Now.AddHours(-1), "manual"),
            probes: [Probe("codex", wouldAllow: false, availablePct: 1, resetAt: Now.AddHours(4))]));

        Assert.Equal(3, overview.Sentence.Split(". ").Length);
        Assert.StartsWith("Paused since", overview.Sentence, StringComparison.Ordinal);
        Assert.Contains("1 item wedged.", overview.Sentence, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // 7. Flow
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_flow_series_is_cumulative_and_starts_from_what_was_already_there()
    {
        var overview = OverviewModel.Build(Inputs(items:
        [
            // Before the window: folded into the first day's baseline, not dropped, so the series
            // starts at the height the fleet was actually at.
            Item(id: "old1", state: "Done", created: Now.AddDays(-40), updated: Now.AddDays(-40)),
            Item(id: "old2", state: "Done", created: Now.AddDays(-40), updated: Now.AddDays(-40)),

            Item(id: "wip", state: "Working", created: Now.AddDays(-10), updated: Now),
            Item(id: "landed", state: "Done", created: Now.AddDays(-5), updated: Now.AddDays(-2)),
            Item(id: "dropped", state: "Cancelled", created: Now.AddDays(-3), updated: Now.AddDays(-1)),
        ]));

        var flow = overview.Flow;

        Assert.Equal(OverviewModel.FlowDays, flow.Days.Count);
        Assert.True(flow.HasData);

        Assert.Equal(2, flow.Days[0].Created);
        Assert.Equal(2, flow.Days[0].Landed);
        Assert.Equal(0, flow.Days[0].InFlight);

        // Day 19 of a 30-day window ending today is ten days ago.
        Assert.Equal(2, flow.Days[18].Created);
        Assert.Equal(3, flow.Days[19].Created);

        Assert.Equal(5, flow.Days[^1].Created);
        Assert.Equal(3, flow.Days[^1].Landed);
        Assert.Equal(1, flow.Days[^1].Cancelled);
        Assert.Equal(1, flow.Days[^1].InFlight);
    }

    [Fact]
    public void An_item_with_no_created_date_is_not_dated_to_the_year_one()
    {
        var overview = OverviewModel.Build(Inputs(items: [Item(state: "Working")]));

        Assert.All(overview.Flow.Days, d => Assert.Equal(0, d.Created));
        Assert.False(overview.Flow.HasData);
    }

    // -------------------------------------------------------------------------------------------
    // 8. Quota burn-down
    // -------------------------------------------------------------------------------------------

    private static QuotaBurn Burn(DateTimeOffset? resetAt, params double[] pct)
        => new(
            Agent: "claude",
            Window: "five_hour",
            Samples: [.. pct.Select((p, i) => new BurnSample(Now.AddHours(i - (pct.Length - 1)), p))],
            ResetAt: resetAt,
            NowPct: null,
            ProjectedUnspentPct: null,
            Eligible: false);

    [Fact]
    public void A_descending_window_projects_what_will_be_left_unspent_at_the_reset()
    {
        // Under a subscription the marginal token is already paid for, so the waste is what is still
        // sitting there when the window rolls over.
        var overview = OverviewModel.Build(Inputs(
            quotaHistory: [Burn(Now.AddHours(2), 100, 90, 80, 70, 60)],
            probes: [Probe("claude", wouldAllow: true, availablePct: 60)]));

        var burn = Assert.Single(overview.Quota);

        Assert.NotNull(burn.ProjectedUnspentPct);
        Assert.Equal(40, burn.ProjectedUnspentPct!.Value, 1);
        Assert.Equal(60, burn.NowPct);
        Assert.True(burn.Eligible);
        Assert.Equal("claude · five hour", burn.Label);
    }

    [Fact]
    public void A_refill_starts_the_fit_again_rather_than_being_averaged_through()
    {
        // A window rolling over is a step change; a line fitted across one describes nothing.
        var overview = OverviewModel.Build(Inputs(
            quotaHistory: [Burn(Now.AddHours(1), 30, 20, 10, 95, 85, 75)]));

        var burn = Assert.Single(overview.Quota);

        Assert.Equal(65, burn.ProjectedUnspentPct!.Value, 1);
    }

    [Fact]
    public void Too_little_history_a_flat_window_or_no_reset_projects_nothing_rather_than_guessing()
    {
        Assert.Null(OverviewModel.ProjectUnspent(Burn(Now.AddHours(1), 90, 80)));
        Assert.Null(OverviewModel.ProjectUnspent(Burn(Now.AddHours(1), 80, 80, 80, 80)));
        Assert.Null(OverviewModel.ProjectUnspent(Burn(Now.AddHours(1), 60, 70, 74)));
        Assert.Null(OverviewModel.ProjectUnspent(Burn(null, 90, 80, 70, 60)));
    }

    [Fact]
    public void A_projection_cannot_run_past_empty()
    {
        var overview = OverviewModel.Build(Inputs(
            quotaHistory: [Burn(Now.AddHours(20), 40, 30, 20, 10)]));

        Assert.Equal(0, Assert.Single(overview.Quota).ProjectedUnspentPct);
    }

    [Fact]
    public void Without_a_statistics_plugin_the_band_still_lists_the_agents_it_can_see()
    {
        var reset = Now.AddHours(3);
        var overview = OverviewModel.Build(Inputs(probes:
        [
            Probe("claude", wouldAllow: true, availablePct: 62, resetAt: reset),
            Probe("codex", wouldAllow: null),   // never probed: a 0% bar would read as exhausted
        ]));

        var burn = Assert.Single(overview.Quota);

        Assert.Equal("claude", burn.Agent);
        Assert.Equal(62, burn.NowPct);
        Assert.Equal(reset, burn.ResetAt);
        Assert.Null(burn.ProjectedUnspentPct);
        Assert.True(burn.Eligible);
        Assert.False(burn.HasSamples);
    }

    // -------------------------------------------------------------------------------------------
    // 9. The empty case
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Build_survives_a_host_that_offers_nothing_at_all()
    {
        var overview = OverviewModel.Build(Inputs());

        Assert.Equal("Idle. Nothing queued.", overview.Sentence);
        Assert.Equal(5, overview.Vitals.Count);
        Assert.Empty(overview.Attention);
        Assert.Empty(overview.Healthy);
        Assert.Equal(OverviewModel.FlowDays, overview.Flow.Days.Count);
        Assert.Empty(overview.Quota);
        Assert.Equal(Now, overview.Sample.At);
        Assert.False(overview.HasAttention);
    }

    [Fact]
    public void The_sample_records_what_this_build_saw_so_the_next_one_has_a_band()
    {
        var overview = OverviewModel.Build(Inputs(
            items:
            [
                Item(id: "m", state: "Working"),
                Item(id: "w", state: "Merging", updated: Now.AddHours(-4)),
                Item(id: "p", state: "WaitingForQuotaReset", updated: Now.AddHours(-4)),
                Item(id: "f", state: "Failed", failureKind: "Infra"),
                Item(id: "done", state: "Done", updated: Now.AddDays(-1)),
            ],
            concurrency: new Concurrency(4, 2, null),
            probes: [Probe("claude", wouldAllow: true, availablePct: 50)],
            health: new TransitionHealth(0.9, 0.1, 40, "Work")));

        var sample = overview.Sample;

        Assert.Equal(1, sample.Landed7d);
        Assert.Equal(1, sample.InMotion);
        Assert.Equal(1, sample.Wedged);
        Assert.Equal(1, sample.Parked);
        Assert.Equal(1, sample.Blocked);
        Assert.Equal(1, sample.BlockedOnYou);
        Assert.Equal(1, sample.EligibleAgents);
        Assert.Equal(2, sample.SlotsBusy);
        Assert.Equal(4, sample.SlotsTotal);
        Assert.Equal(0.1, sample.InfraFailureRate);
    }
}
