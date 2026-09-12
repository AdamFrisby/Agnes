using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// A whole wall, hand-built: four busy slots in four different phases, a free one, a month of flow, two
/// quota windows and a queue behind it.
/// </summary>
/// <remarks>
/// It lives in the test project for the same reason <see cref="BoardSamples"/> and
/// <see cref="OverviewSamples"/> do — the wall is a function of a board, an overview and a work-item
/// list, so anyone can point at this and see the screen fully populated with nothing running. The cases
/// that are awkward to catch on a live host are here on purpose: an item with no audit trace yet, one
/// deep into a sawtooth, a card whose output is ANSI-coloured, a slot nobody is using.
/// </remarks>
public static class NowWorkingSamples
{
    /// <summary>
    /// Local midday on a fixed date.
    /// </summary>
    /// <remarks>
    /// Local, not UTC, and midday rather than any hour. "Landed today" is a question about the reader's
    /// own calendar day, so a sample pinned to a UTC instant answers it differently in Sydney than in
    /// Los Angeles — which is a property of the sample and not of the code, and exactly the sort of test
    /// that passes in CI and fails on the machine it was written for.
    /// </remarks>
    public static readonly DateTimeOffset Now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Local);

    /// <summary>The four running items and the queue behind them.</summary>
    public static IReadOnlyList<WorkItemRow> Items()
    {
        var rows = new List<WorkItemRow>
        {
            Row("a1", "Rewrite the credential broker so a sandbox never sees a long-lived token",
                "Working", "claude", Now.AddMinutes(-9), cost: 4.12m),
            Row("b2", "tests:coverage — deterministic diff-scoped code-coverage audit gate",
                "Auditing", "copilot", Now.AddMinutes(-21), cost: 11.90m),
            Row("c3", "Harden the Incus volume lifecycle against a mid-bake restart",
                "Reworking", "codex", Now.AddHours(-1).AddMinutes(-14), cost: 26.40m),
            Row("d4", "Merge state machine: land the upstream push retry", "Merging", "claude",
                Now.AddMinutes(-2), cost: 0.84m),
        };

        // Landed today, for the headline figure and the day's cost.
        for (var i = 0; i < 6; i++)
        {
            rows.Add(Row($"e{i}", $"Landed work {i + 1}", "Done", "claude", Now.AddMinutes(-10 * (i + 1)), cost: 1.5m + i));
        }

        // And a queue that is not moving, so "waiting" is not zero.
        for (var i = 0; i < 9; i++)
        {
            rows.Add(Row($"q{i}", $"Queued work {i + 1}", "Queued", null, Now.AddHours(-4)));
        }

        return rows;
    }

    /// <summary>Four busy slots of five. The fifth draws as an outline, which is the state the wall is
    /// most often in and the one a sample of only-busy slots never shows.</summary>
    public static Board Board()
    {
        var running = Items().Where(i => i.IsActive).Select(Solo).ToList();
        return new Board(
            Now: running,
            Next: [],
            Waiting: [new WaitGroup(WaitReason.Slot, "Waiting for a slot",
                [.. Items().Where(i => i.State == "Queued").Select(Solo)])],
            Landed: [],
            HistoryCount: 372,
            Slots: (4, 5));
    }

    /// <summary>The overview the wall reads its traces, flow, quota and drain estimate out of.</summary>
    public static Overview Overview()
    {
        var items = Items();
        return new Overview(
            Sentence: "Four slots busy, one free. Nothing is waiting on you.",
            Verdict: TileTone.Active,
            Vitals: [],
            Attention: [Trace(items[2], Sawtooth(), Motion.Moving, Convergence.Oscillating,
                              "completeness:llm-review has blocked 4 of the last 5 iterations.", 1)],
            Healthy:
            [
                Trace(items[0], [], Motion.Moving, Convergence.New, string.Empty, 90),
                Trace(items[1], Descending(9), Motion.Moving, Convergence.Converging, string.Empty, 91),
                Trace(items[3], Passed(), Motion.Moving, Convergence.Passed, string.Empty, 92),
            ],
            Flow: Flow(),
            Quota: Quota(),
            Sample: new OverviewSample(
                Now, Landed7d: 34, InMotion: 4, Parked: 2, Blocked: 1, Wedged: 0, EligibleAgents: 2,
                SlotsBusy: 4, SlotsTotal: 5, InfraFailureRate: 0.06, BlockedOnYou: 2))
        {
            Burn = new BurnEstimate(
                Remaining: 9, Sampled: 12, MedianPerItem: TimeSpan.FromMinutes(47),
                LowPerItem: TimeSpan.FromMinutes(21), HighPerItem: TimeSpan.FromMinutes(96),
                SpentOnLive: TimeSpan.FromMinutes(90), WorkRemaining: TimeSpan.FromMinutes(333),
                Slots: 5, Wall: TimeSpan.FromMinutes(400),
                SparkHours: [9.1, 8.4, 7.8, 8.9, 7.2, 6.9, 6.7]),
        };
    }

    /// <summary>
    /// What four agents are printing, keyed by the same ids the items carry. One of them in colour,
    /// because a real tail is.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Tails() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Id("a1")] = "reading src/Agnes.Sandbox/CredentialBroker.cs\nwriting src/Agnes.Sandbox/CredentialBroker.cs\n\u001b[32mPASS\u001b[0m 412 tests, 0 failed",
        [Id("b2")] = "dotnet test tests/CodeyBox.Tests --filter Coverage\r  0%\r 61%\n  coverage 84.2% of changed lines\nauditor csharp:coverage reporting…",
        [Id("c3")] = "git rebase --onto main HEAD~3\nCONFLICT (content): Merge conflict in src/Incus/VolumeLifecycle.cs\nresolving with the upstream hunk",
        [Id("d4")] = "pushing codeybox/d4 → origin\nremote: Resolving deltas: 100%\nopening pull request",
    };

    // ---- the pieces ------------------------------------------------------------------------------

    /// <summary>An id in the shape the orchestrator hands out, from a short name a sample can read.</summary>
    public static string Id(string name) => name.PadRight(32, '0');

    public static WorkItemRow Row(
        string id, string title, string state, string? agent, DateTimeOffset updated, decimal cost = 0) => new(
        Id: Id(id),
        Title: title,
        State: state,
        Agent: agent,
        ProjectId: "codeybox-self",
        QueuePosition: 0,
        UpdatedAt: updated,
        LastError: null,
        CreatedAt: updated.AddDays(-2),
        UsageTotal: cost > 0 ? new UsageTotal(cost, 120_000, 14_000) : null);

    private static Chain Solo(WorkItemRow item)
    {
        var state = item.State switch
        {
            "Done" => StepState.Done,
            "Queued" => StepState.Ready,
            _ => StepState.Running,
        };
        return new Chain(
            Id: item.Id,
            Title: item.Title,
            ProjectId: item.ProjectId,
            Steps: [new Step(item, 0, state, string.Empty)],
            Head: item,
            Horizon: item.IsActive ? Horizon.Now : Horizon.Waiting,
            Reason: item.IsActive ? WaitReason.None : WaitReason.Slot,
            Why: item.IsActive ? $"running on {item.Agent}" : "waiting for a slot",
            Blocker: null,
            DispatchRank: -1,
            LastActivity: item.UpdatedAt,
            Landed: null);
    }

    private static ItemTrace Trace(
        WorkItemRow item, IReadOnlyList<TracePoint> points, Motion motion, Convergence shape,
        string why, int rank) => new(
        item, points, Ceiling: 25, motion, shape, why, NearCeiling: false, NeedsPerson: false,
        SinceMoved: Now - item.UpdatedAt, rank);

    private static IReadOnlyList<TracePoint> Descending(int count)
        => [.. Enumerable.Range(1, count).Select(i => new TracePoint(i, Math.Max(1, 12 - i), true, false))];

    private static IReadOnlyList<TracePoint> Sawtooth()
    {
        int[] findings = [7, 4, 6, 3, 7, 2, 8, 3, 6];
        return [.. findings.Select((f, i) => new TracePoint(i + 1, f, true, i > 0 && f > findings[i - 1]))];
    }

    private static IReadOnlyList<TracePoint> Passed()
        => [.. Enumerable.Range(1, 5).Select(i => new TracePoint(i, i == 5 ? 0 : 6 - i, true, false))];

    private static FlowSeries Flow()
    {
        var start = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-29);
        var days = new List<FlowPoint>(30);
        int created = 120, landed = 96, cancelled = 8;
        for (var i = 0; i < 30; i++)
        {
            created += 3 + (i % 4);
            landed += i is > 11 and < 19 ? 0 : 2 + (i % 3);
            cancelled += i % 7 == 0 ? 1 : 0;
            days.Add(new FlowPoint(start.AddDays(i), created, landed, cancelled));
        }

        return new FlowSeries(days);
    }

    private static IReadOnlyList<QuotaBurn> Quota() =>
    [
        new QuotaBurn("claude", "five_hour", Burn(78, 55), Now.AddHours(2), 55, 12, Eligible: true),
        // Flat: eligible, measured, and not being spent. The gauge for this one must not breathe.
        new QuotaBurn("copilot", "seven_day", Burn(61, 61), Now.AddDays(3), 61, 61, Eligible: true),
        new QuotaBurn("codex", "five_hour", Burn(40, 3), Now.AddHours(3.7), 3, null, Eligible: false),
    ];

    private static IReadOnlyList<BurnSample> Burn(double from, double to)
        => [.. Enumerable.Range(0, 8).Select(i => new BurnSample(Now.AddMinutes(-15 * (7 - i)), from + ((to - from) * i / 7.0)))];
}

/// <summary>
/// A clock the test drives by hand.
/// </summary>
/// <remarks>
/// The wall's whole behaviour is a function of time, and a <see cref="Avalonia.Threading.DispatcherTimer"/>
/// cannot be advanced. With this, a flash decaying and a number counting up happen exactly when a test
/// says they do — and "the timers stop when you leave the section" is an assertion about
/// <see cref="Running"/> rather than about a wall-clock wait.
/// </remarks>
public sealed class FakeWallClock : IWallClock
{
    private readonly List<Registration> _ticks = [];

    /// <summary>How many timers are currently alive.</summary>
    public int Running => _ticks.Count(t => !t.Stopped);

    public IDisposable Every(TimeSpan interval, Action tick)
    {
        var registration = new Registration(interval, tick);
        _ticks.Add(registration);
        return registration;
    }

    /// <summary>Fires every live timer once, as if <paramref name="times"/> intervals had passed.</summary>
    public void Fire(int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            foreach (var tick in _ticks.Where(t => !t.Stopped).ToList())
            {
                tick.Tick();
            }
        }
    }

    private sealed class Registration(TimeSpan interval, Action tick) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;

        public bool Stopped { get; private set; }

        public void Tick() => tick();

        public void Dispose() => Stopped = true;
    }
}
