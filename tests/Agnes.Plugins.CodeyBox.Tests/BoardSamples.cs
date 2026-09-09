using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// A whole runway, hand-built: a seven-step chain part-way through, a singleton running, four queued
/// chains in dispatch order, one waiting group per reason, two days of landed work, and a history match.
/// </summary>
/// <remarks>
/// It lives in the test project for the same reason <see cref="OverviewSamples"/> does. The board view
/// renders a <see cref="Board"/> and a handful of commands, so anyone wiring the model up can point at
/// this and see the screen fully populated before a single request goes out — and the cases that are
/// awkward to reach on a live host (a chain held by a <em>failed</em> parent, a landed day that is not
/// today, a singleton with no strip at all) are here rather than waiting to be discovered.
/// </remarks>
public static class BoardSamples
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 14, 30, 0, TimeSpan.Zero);

    /// <summary>The board the screenshots and the render tests are taken against.</summary>
    public static Board Fleet() => new(
        Now: [Series(), Singleton()],
        Next: Queued(),
        Waiting: Waiting(),
        Landed: Landed(),
        HistoryCount: 372,
        Slots: (2, 3));

    /// <summary>What search turns up beyond the four horizons: older, or cancelled.</summary>
    public static IReadOnlyList<Chain> History() =>
    [
        Solo("Retire the Uno desktop head", "Cancelled", "codex", StepState.Cancelled,
             Horizon.History, WaitReason.None, "cancelled 21 Aug — superseded", days: 16),
    ];

    /// <summary>The relations band's subject: step 4 of the series, held by the step that failed.</summary>
    public static Relations Held()
    {
        var chain = Series();
        var item = chain.Steps[3].Item;

        return new Relations(
            Item: item,
            Parents:
            [
                new Relation(chain.Steps[2].Item, StepState.Done, Satisfied: true),
                new Relation(chain.Steps[1].Item, StepState.Failed, Satisfied: false),
            ],
            Children: [new Relation(chain.Steps[4].Item, StepState.Blocked, Satisfied: false)],
            Chain: chain,
            BlockingRoot: new Relation(chain.Steps[1].Item, StepState.Failed, Satisfied: false));
    }

    /// <summary>The same band with nothing wrong: parents satisfied, nothing holding it.</summary>
    public static Relations Clear()
    {
        var chain = Series();
        return new Relations(
            Item: chain.Steps[2].Item,
            Parents: [new Relation(chain.Steps[1].Item, StepState.Done, Satisfied: true)],
            Children: [],
            Chain: chain,
            BlockingRoot: null);
    }

    /// <summary>Things the picker could offer: one already ticked, one from another project.</summary>
    public static IReadOnlyList<DependencyCandidate> Candidates()
    {
        var chain = Series();
        return
        [
            new DependencyCandidate(chain.Steps[1].Item, StepState.Failed, Ticked: true, SameChain: true, SameProject: true),
            new DependencyCandidate(chain.Steps[4].Item, StepState.Blocked, Ticked: false, SameChain: true, SameProject: true),
            new DependencyCandidate(Item("Bake the sandbox image", "Queued", "codex", 40, "agnes"),
                                    StepState.Ready, Ticked: false, SameChain: false, SameProject: false),
        ];
    }

    /// <summary>
    /// The seven-step series: three done, one running, one ready, two blocked behind the one that failed.
    /// </summary>
    public static Chain Series()
    {
        string[] titles =
        [
            "Test selection (RTS) — 1/7 map the test graph",
            "Test selection (RTS) — 2/7 wire the coverage probe",
            "Test selection (RTS) — 3/7 persist the graph",
            "Test selection (RTS) — 4/7 select from a diff",
            "Test selection (RTS) — 5/7 fall back to a full run",
            "Test selection (RTS) — 6/7 report what was skipped",
            "Test selection (RTS) — 7/7 turn it on in CI",
        ];

        StepState[] states =
        [
            StepState.Done, StepState.Failed, StepState.Done, StepState.Running,
            StepState.Ready, StepState.Blocked, StepState.Blocked,
        ];

        var steps = new List<Step>(7);
        for (var i = 0; i < titles.Length; i++)
        {
            steps.Add(new Step(
                Item(titles[i], StateWord(states[i]), i % 2 == 0 ? "claude" : "codex", 20 - i, "codeybox-self"),
                i,
                states[i],
                $"{i + 1}/7"));
        }

        return new Chain(
            Id: steps[0].Item.Id,
            Title: "Test selection (RTS)",
            ProjectId: "codeybox-self",
            Steps: steps,
            Head: steps[3].Item,
            Horizon: Horizon.Now,
            Reason: WaitReason.None,
            Why: "step 4 of 7 running on claude",
            Blocker: null,
            DispatchRank: -1,
            LastActivity: Now.AddMinutes(-3),
            Landed: null);
    }

    private static Chain Singleton()
        => Solo("Rewrite the credential broker", "Working", "claude", StepState.Running,
                Horizon.Now, WaitReason.None, "running on claude for 4h 12m", days: 0);

    private static IReadOnlyList<Chain> Queued() =>
    [
        Solo("Harden the Incus volume lifecycle", "Queued", "codex", StepState.Ready,
             Horizon.Next, WaitReason.None, "next when a slot frees", days: 0, rank: 0),
        Solo("Port the mobile inbox to sheets", "Queued", "claude", StepState.Ready,
             Horizon.Next, WaitReason.None, "2nd in dispatch order", days: 0, rank: 1),
        // A queued CHAIN, so the Next list is not accidentally all singletons.
        Pair("Copilot BYOK", "Queued", "claude", Horizon.Next, "3rd in dispatch order", rank: 2),
        Solo("Add a Pi smoke test", "Queued", "pi", StepState.Ready,
             Horizon.Next, WaitReason.None, "4th in dispatch order", days: 0, rank: 3),
    ];

    /// <summary>One group per reason, because a reason is an unblock and each needs to be seen.</summary>
    private static IReadOnlyList<WaitGroup> Waiting()
    {
        var held = Series() with
        {
            Horizon = Horizon.Waiting,
            Reason = WaitReason.Parent,
            Why = "waiting on step 2, which failed",
            Blocker = Series().Steps[1],
            Head = Series().Steps[4].Item,
        };

        return
        [
            new WaitGroup(WaitReason.Parent, "Waiting on a parent", [held]),
            new WaitGroup(WaitReason.Slot, "Waiting for a slot",
            [
                Solo("Document the event spine", "Queued", "claude", StepState.Ready,
                     Horizon.Waiting, WaitReason.Slot, "runnable — every slot is busy", days: 0),
            ]),
            new WaitGroup(WaitReason.Quota, "Parked until a quota window reopens",
            [
                Solo("Bake the sandbox image", "Queued", "codex", StepState.Parked,
                     Horizon.Waiting, WaitReason.Quota, "quota window reopens at 18:10", days: 0),
            ]),
            new WaitGroup(WaitReason.Person, "Needs a person",
            [
                Solo("Decide the plugin signing story", "Failed", "claude", StepState.NeedsPerson,
                     Horizon.Waiting, WaitReason.Person, "failed twice — needs a call", days: 0),
            ]),
            new WaitGroup(WaitReason.Paused, "Paused by an operator",
            [
                Solo("Tidy the release workflow", "Queued", "codex", StepState.Blocked,
                     Horizon.Waiting, WaitReason.Paused, "the codeybox-self queue is paused", days: 0),
            ]),
        ];
    }

    private static IReadOnlyList<LandedDay> Landed() =>
    [
        new LandedDay(DateOnly.FromDateTime(Now.UtcDateTime), "Today",
        [
            Solo("Land the Copilot effort flag", "Done", "claude", StepState.Done,
                 Horizon.Landed, WaitReason.None, "landed 14:02", days: 0, landed: Now.AddMinutes(-28)),
            Pair("Overview vitals", "Done", "codex", Horizon.Landed, "landed 11:47", rank: 0,
                 last: StepState.Done, landed: Now.AddHours(-3)),
        ]),
        new LandedDay(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-1), "Yesterday",
        [
            Solo("Fix the flaky sandbox teardown", "Done", "codex", StepState.Done,
                 Horizon.Landed, WaitReason.None, "landed 17:31", days: 1, landed: Now.AddDays(-1)),
        ]),
    ];

    /// <summary>A chain of one: no strip, no progress, no disclosure.</summary>
    private static Chain Solo(string title, string state, string agent, StepState step, Horizon horizon,
                              WaitReason reason, string why, int days, int rank = -1,
                              DateTimeOffset? landed = null)
    {
        var item = Item(title, state, agent, 10 - rank, "codeybox-self");
        var only = new Step(item, 0, step, string.Empty);

        return new Chain(
            Id: item.Id,
            Title: title,
            ProjectId: item.ProjectId,
            Steps: [only],
            Head: item,
            Horizon: horizon,
            Reason: reason,
            Why: why,
            Blocker: null,
            DispatchRank: rank,
            LastActivity: Now.AddDays(-days),
            Landed: landed);
    }

    /// <summary>Two steps: the smallest thing that still draws a strip and a progress count.</summary>
    private static Chain Pair(string title, string state, string agent, Horizon horizon, string why,
                              int rank, StepState? last = null, DateTimeOffset? landed = null)
    {
        var first = Item(title + " — 1/2 the model", "Done", agent, 10 - rank, "agnes");
        var second = Item(title + " — 2/2 the view", state, agent, 10 - rank, "agnes");
        List<Step> steps =
        [
            new Step(first, 0, StepState.Done, "1/2"),
            new Step(second, 1, last ?? StepState.Ready, "2/2"),
        ];

        return new Chain(
            Id: first.Id,
            Title: title,
            ProjectId: "agnes",
            Steps: steps,
            Head: second,
            Horizon: horizon,
            Reason: WaitReason.None,
            Why: why,
            Blocker: null,
            DispatchRank: rank,
            LastActivity: Now.AddMinutes(-11),
            Landed: landed);
    }

    private static string StateWord(StepState state) => state switch
    {
        StepState.Done => "Done",
        StepState.Running => "Working",
        StepState.Failed => "Failed",
        StepState.Cancelled => "Cancelled",
        _ => "Queued",
    };

    private static WorkItemRow Item(string title, string state, string agent, int priority, string project)
        => new(
            Id: $"{Math.Abs(title.GetHashCode(StringComparison.Ordinal)):x8}00000000000000000000000",
            Title: title,
            State: state,
            Agent: agent,
            ProjectId: project,
            QueuePosition: priority,
            UpdatedAt: Now.AddMinutes(-7),
            LastError: state == "Failed" ? "the audit never converged" : null,
            Prompt: "Do the thing the title says, then open a PR.",
            Priority: priority,
            CreatedAt: Now.AddHours(-9));
}
