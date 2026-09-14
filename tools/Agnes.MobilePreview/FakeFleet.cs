using Agnes.Plugins.CodeyBox;

namespace Agnes.App.Mobile.Preview;

/// <summary>
/// A CodeyBox fleet with no CodeyBox behind it.
/// </summary>
/// <remarks>
/// <para>The harness's job is to prove the phone's screens render — a missing resource, an unresolvable
/// binding or a control that measures to nothing are all silent at build time and fatal on a device. The
/// CodeyBox screens need a fleet to draw, and the live orchestrator is somebody's real work; so this
/// builds the inputs and runs them through the <em>real</em> <see cref="OverviewModel"/>,
/// <see cref="BoardModel"/> and <see cref="QuotaHistoryMap"/>. Nothing here fabricates an
/// <see cref="Overview"/> or a <see cref="Board"/> directly: what is captured is what those models make
/// of a queue, which is the only version worth photographing.</para>
///
/// <para>The shapes are the ones a real queue has and a tidy sample does not: a chain part-way through, a
/// run of items all stuck at the same phase boundary, an item oscillating on one gate, one wedged, one
/// parked on a question, one failed. Those are what have actually broken this layout.</para>
///
/// <para>It is <see cref="FakeDisplayHost"/>'s sibling, and exists for the same reason.</para>
/// </remarks>
internal static class FakeFleet
{
    /// <summary>
    /// The moment the fleet is pretending to be at, which is the moment the shot is taken.
    /// </summary>
    /// <remarks>
    /// Not a fixed date. The wall's elapsed timers count from the real clock, "landed today" is a question
    /// about the reader's own calendar day, and a quota reset is drawn against now — so a sample pinned to
    /// a date in the past photographs as a fleet that has been running for months.
    /// </remarks>
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private const string Project = "codeybox-self";

    internal static GatheredOverview Build()
    {
        var items = Items();
        var projects = new List<Project>
        {
            new("codeybox-self", "CodeyBox", null, "main", "claude", AuditMaxIterations: 25),
            new("agnes", "Agnes", null, "main", "codex", AuditMaxIterations: 18),
        };

        var probes = Probes();
        var concurrency = new Concurrency(3, 3, new Dictionary<string, int>());
        var queue = new QueueStatus("Running", null, null);

        var inputs = new OverviewInputs(
            Now,
            items,
            Traces(items),
            new Dictionary<string, int>(StringComparer.Ordinal) { [Id("q1")] = 1 },
            queue,
            concurrency,
            probes,
            Quota(),
            new TransitionHealth(0.93, 0.07, 412, "audit"),
            History(),
            new Dictionary<string, int>(StringComparer.Ordinal) { [Project] = 25, ["agnes"] = 18 })
        {
            Effort = Effort(items),
        };

        return new GatheredOverview(
            OverviewModel.Build(inputs), items, projects, probes, concurrency, queue);
    }

    /// <summary>What each running agent is printing, keyed the way the wall's cards are.</summary>
    internal static IReadOnlyDictionary<string, string> Tails() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Id("a1")] = "reading src/Agnes.Sandbox/CredentialBroker.cs\nwriting src/Agnes.Sandbox/CredentialBroker.cs\nPASS 412 tests, 0 failed",
        [Id("b2")] = "dotnet test tests/CodeyBox.Tests --filter Coverage\n  coverage 84.2% of changed lines\nauditor csharp:coverage reporting…",
        [Id("c3")] = "git rebase --onto main HEAD~3\nCONFLICT (content): Merge conflict in src/Incus/VolumeLifecycle.cs\nresolving with the upstream hunk",
    };

    /// <summary>The item the item page is shot against: failed, with a real error and a real budget.</summary>
    internal static WorkItemRow Failed() => Row(
        "f1", "Land the upstream push retry in the merge state machine", "AuditFailed", "claude",
        Now.AddMinutes(-52),
        error: "completeness:llm-review blocked iteration 25: the retry path does not cover a 403 from "
             + "the forge, which is the failure the item was opened for.",
        failureKind: "audit",
        cost: 18.40m);

    // ---- the queue ---------------------------------------------------------------------------------

    private static IReadOnlyList<WorkItemRow> Items()
    {
        var rows = new List<WorkItemRow>
        {
            Row("a1", "Rewrite the credential broker so a sandbox never sees a long-lived token",
                "Working", "claude", Now.AddMinutes(-9), cost: 4.12m),
            Row("b2", "tests:coverage — deterministic diff-scoped code-coverage audit gate",
                "Auditing", "copilot", Now.AddMinutes(-21), cost: 11.90m),
            Row("c3", "Harden the Incus volume lifecycle against a mid-bake restart",
                "Reworking", "codex", Now.AddHours(-1).AddMinutes(-14), cost: 26.40m),
            Row("q1", "Decide the token scope for the credential broker", "NeedsOperatorInput", "claude",
                Now.AddHours(-3), cost: 2.05m),
            Failed(),
        };

        // A seven-step chain part-way through: the shape the runway exists to show.
        for (var step = 1; step <= 7; step++)
        {
            var done = step <= 3;
            rows.Add(Row(
                $"s{step}",
                $"Test selection (RTS) {step}/7 — {Titles[step - 1]}",
                done ? "Done" : "Queued",
                done ? "claude" : null,
                Now.AddHours(-6 + step),
                dependsOn: step == 1 ? null : [Id($"s{step - 1}")],
                depsOk: done || step == 4,
                priority: 40) with
            { ExternalId = $"rts-{step}" });
        }

        // A run of items all stopped at the same phase boundary — the case the folded groups exist for.
        for (var i = 0; i < 9; i++)
        {
            rows.Add(Row($"w{i:00}", $"Audit gate {i + 1}: re-run only the affected tests", "WorkComplete",
                         "copilot", Now.AddHours(-11), priority: 10));
        }

        // Landed work, across two days, so the flow chart and the landed sections have something to say.
        for (var i = 0; i < 14; i++)
        {
            rows.Add(Row($"d{i:00}", $"Landed work {i + 1}", "Done", i % 2 == 0 ? "claude" : "codex",
                         Now.AddHours(-i * 5), cost: 1.4m + i));
        }

        return rows;
    }

    private static readonly string[] Titles =
    [
        "map the dependency graph", "record the touched files", "score the affected suites",
        "wire the selector into the gate", "prove it against a known regression",
        "report what it skipped", "turn it on by default",
    ];

    private static IReadOnlyList<ItemAuditProgress> Traces(IReadOnlyList<WorkItemRow> items)
    {
        var traces = new List<ItemAuditProgress>
        {
            // Converging: a descending staircase, which is the loop doing its job.
            new(Id("b2"), Rows(Id("b2"), [11, 9, 7, 6, 4, 3, 2, 1])),
            // Oscillating on one gate: down and back up, which is the one kind of repetition that is waste.
            new(Id("c3"), Rows(Id("c3"), [7, 4, 6, 3, 7, 2, 8, 3, 6])),
            // Spent its whole budget and still blocked.
            new(Id("f1"), Rows(Id("f1"), [.. Enumerable.Repeat(3, 25)])),
        };
        _ = items;
        return traces;
    }

    private static IReadOnlyList<AuditProgressRow> Rows(string itemId, IReadOnlyList<int> blocking)
        => [.. blocking.Select((count, i) => new AuditProgressRow(
            Id: $"{itemId}-{i}",
            WorkAttemptKey: "attempt-1",
            Iteration: i + 1,
            MaxIterations: 25,
            Status: "complete",
            BlockingFindings: count,
            NonBlockingFindings: 1,
            RecordedAt: Now.AddMinutes(-((blocking.Count - i) * 7)),
            ScheduledAuditors: ["completeness:llm-review"],
            CompletedAuditors: ["completeness:llm-review"],
            BlockingFindingsDetails: null,
            Findings: null,
            Truncated: false))];

    private static IReadOnlyList<ItemEffort> Effort(IReadOnlyList<WorkItemRow> items)
        => [.. items
            .Where(i => i.State == "Done" || !i.IsTerminal)
            .Select((item, i) => new ItemEffort(
                item.Id, TimeSpan.FromMinutes(28 + (i * 7 % 55)), item.State == "Done", item.UpdatedAt))];

    // ---- quota -------------------------------------------------------------------------------------

    private static IReadOnlyList<QuotaProbe> Probes() =>
    [
        new("claude", "sonnet", "Subscription", false, null, true, null,
            new QuotaSnapshot(41.5, true, Now.AddHours(8).AddMinutes(24))),
        new("codex", "gpt-5", "Subscription", false, null, true, null,
            new QuotaSnapshot(78.0, true, Now.AddHours(2).AddMinutes(6))),
        new("copilot", null, "Subscription", true, "quota window", false, null,
            new QuotaSnapshot(3.0, true, Now.AddHours(1))),
    ];

    /// <summary>Two windows per agent, burning at different rates, through the real mapper.</summary>
    private static IReadOnlyList<QuotaBurn> Quota()
    {
        var rows = new List<QuotaHistoryRow>();
        foreach (var (agent, window, from, to, hours) in new[]
                 {
                     ("claude", "seven_day", 96.0, 41.5, 72),
                     ("claude", "five_hour", 88.0, 52.0, 4),
                     ("codex", "seven_day", 92.0, 78.0, 72),
                     ("copilot", "five_hour", 40.0, 3.0, 4),
                 })
        {
            for (var i = 0; i <= 40; i++)
            {
                var t = i / 40.0;
                var pct = from + ((to - from) * t);
                rows.Add(new QuotaHistoryRow(
                    SampledAt: Now.AddHours(-hours + (hours * t)),
                    Agent: agent,
                    ModelId: null,
                    OverallPct: pct,
                    WouldAllow: true,
                    Notes: null,
                    WindowName: window,
                    WindowPct: pct,
                    WindowResetAt: Now.AddHours(window == "seven_day" ? 30 : 2),
                    IsKnown: true,
                    UnknownReason: null));
            }
        }

        return QuotaHistoryMap.ToBurnDown(rows, Probes(), Now);
    }

    /// <summary>The plugin's own sparkline history: enough samples for a control band.</summary>
    private static IReadOnlyList<OverviewSample> History()
        => [.. Enumerable.Range(0, 24).Select(i => new OverviewSample(
            At: Now.AddHours(-24 + i),
            Landed7d: 28 + (i % 7),
            InMotion: 3 + (i % 3),
            Parked: 2,
            Blocked: 1 + (i % 2),
            Wedged: i % 11 == 0 ? 1 : 0,
            EligibleAgents: 2,
            SlotsBusy: 2 + (i % 2),
            SlotsTotal: 3,
            InfraFailureRate: 0.04 + ((i % 5) * 0.01),
            BlockedOnYou: i % 4 == 0 ? 2 : 1))];

    // ---- the shapes --------------------------------------------------------------------------------

    /// <summary>
    /// An id in the shape the orchestrator hands out, from a short name a sample can read.
    /// </summary>
    /// <remarks>
    /// Every family here uses a fixed-width number ("d01", not "d1") on purpose: right-padding with zeros
    /// makes "d1" and "d10" the same id, and BoardModel builds its chains from a dictionary keyed on it.
    /// </remarks>
    private static string Id(string name) => name.PadRight(32, '0');

    private static WorkItemRow Row(
        string id,
        string title,
        string state,
        string? agent,
        DateTimeOffset updated,
        decimal cost = 0,
        string? error = null,
        string? failureKind = null,
        IReadOnlyList<string>? dependsOn = null,
        bool depsOk = true,
        int priority = 0) => new(
        Id: Id(id),
        Title: title,
        State: state,
        Agent: agent,
        ProjectId: Project,
        QueuePosition: 0,
        UpdatedAt: updated,
        LastError: error,
        Prompt: "Wire the credential broker into the sandbox so a sandboxed agent can push without a "
              + "long-lived token, then open a pull request.",
        DependsOn: dependsOn,
        Priority: priority,
        CreatedAt: updated.AddDays(-2),
        DependsOnSatisfied: depsOk,
        FailureKind: failureKind,
        WorkBranch: $"codeybox/{id}",
        UsageTotal: cost > 0 ? new UsageTotal(cost, 840_000, 61_000) : null);

    /// <summary>Ids the harness has to name from outside, for the routes below.</summary>
    internal static string ParkedId => Id("q1");

    internal static string FailedId => Id("f1");

    /// <summary>
    /// An orchestrator that is not there, answering politely.
    /// </summary>
    /// <remarks>
    /// The item page reads its own questions, and its lookups fetch output, a diff and a timeline. None of
    /// that can reach the operator's real CodeyBox from a screenshot run, and none of it is what the shot
    /// is of — so this answers the handful of routes an item page touches, with the one canned question
    /// that makes the parked item's decision card worth photographing. The fleet screens themselves make
    /// no requests at all: the harness hands them a gather rather than letting them ask for one.
    /// </remarks>
    internal sealed class OfflineHandler : System.Net.Http.HttpMessageHandler
    {
        private const string Question =
            """
            [{"id":"aa11","questionId":"q-token-scope",
              "questionText":"The broker can mint a token per push or one per session. Per push is safer but adds a round trip to every commit — which do you want?",
              "state":"open","askedAt":"2026-09-12T11:30:00+00:00","answeredAt":null,
              "answerText":null,"answeredBy":null,"dismissedAt":null,"workItemId":"PARKED"}]
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                _ when path.EndsWith("/questions", StringComparison.Ordinal)
                       && path.Contains(ParkedId, StringComparison.Ordinal)
                    => Question.Replace("PARKED", ParkedId, StringComparison.Ordinal),
                _ when path.EndsWith("/questions", StringComparison.Ordinal) => "[]",
                _ => "[]",
            };

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(
                    body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
