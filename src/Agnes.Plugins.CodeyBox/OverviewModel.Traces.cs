namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// Band 2: one trace per live item.
//
// Two independent readings per item, deliberately kept apart because they answer different questions:
//
//   Motion       is it doing anything, and if not, is that a decision (parked, blocked) or a defect
//                (wedged)? This is the first question, and it is answered from timestamps, not counts.
//   Convergence  which way is the audit loop heading? Read from the *direction* of blocking findings
//                across iterations. The count of iterations is never the signal: this fleet's median
//                landed item took 9-12 of them, 88% of which blocked. Repetition is the mechanism.
//
// The only shape that is waste is Oscillating — the same gate objecting again after a rework, which is
// the loop failing to consume its own output rather than the loop working.
// ---------------------------------------------------------------------------------------------------

public static partial class OverviewModel
{
    /// <summary>States that are neither running nor terminal but are still mid-pipeline: an item sitting
    /// in one of these with nothing happening has as much right to be called wedged as one that says it
    /// is Working, and rather more, since nothing is even claiming to hold it.</summary>
    private static readonly string[] LivePhases =
    [
        "WorkComplete", "AuditPassed", "Merged", "UpstreamPushing",
        "Planning", "PlanReview", "PlanApproved", "ReworkingForConflict",
    ];

    /// <summary>States that mean "stopped on purpose, with a resume".</summary>
    private static readonly string[] ParkedStates =
    [
        "WaitingForQuotaReset", "WaitingForTransientRetry", "WaitingForAgentResume",
    ];

    /// <summary>
    /// One trace per item worth looking at. Done and Cancelled items are dropped entirely — they are
    /// finished history, and on a real queue they are 90% of the rows. The failed family is terminal but
    /// kept, because a failed item is not finished: it is waiting for someone to decide something.
    /// </summary>
    internal static IReadOnlyList<ItemTrace> BuildTraces(OverviewInputs inputs)
    {
        var progress = inputs.AuditProgress.ToDictionary(p => p.WorkItemId, p => p.Rows, StringComparer.Ordinal);

        return
        [
            .. inputs.Items
                .Where(i => !i.IsTerminal || i.IsFailed)
                .Select(i => BuildTrace(inputs, i, progress.TryGetValue(i.Id, out var rows) ? rows : []))
        ];
    }

    private static ItemTrace BuildTrace(OverviewInputs inputs, WorkItemRow item, IReadOnlyList<AuditProgressRow> rows)
    {
        var points = BuildPoints(rows);
        var ceiling = CeilingFor(rows, item, inputs.Ceilings);
        var shape = Shape(points);

        var lastSignal = rows.Count == 0 ? item.UpdatedAt : Max(item.UpdatedAt, rows.Max(r => r.RecordedAt));
        var since = inputs.Now - lastSignal;
        if (since < TimeSpan.Zero)
        {
            since = TimeSpan.Zero;
        }

        var motion = ReadMotion(inputs, item, since);

        var lastIteration = points.Count == 0 ? 0 : points[^1].Iteration;
        var nearCeiling = ceiling > 0
            && lastIteration >= ceiling - NearCeilingWithin
            && shape is Convergence.Converging or Convergence.New;

        var why = Explain(motion, shape, points, RepeatingGate(rows), lastIteration, ceiling, nearCeiling);

        return new ItemTrace(
            item,
            points,
            ceiling,
            motion.Motion,
            shape,
            why,
            nearCeiling,
            motion.NeedsPerson,
            since,
            Rank(motion, shape, nearCeiling));
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    // -------------------------------------------------------------------------------------------
    // The trace itself
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// One point per audit iteration, oldest first. An iteration can appear under more than one work
    /// attempt (a rework restarts the auditors); the latest recording is the one that is true now.
    /// </summary>
    internal static IReadOnlyList<TracePoint> BuildPoints(IReadOnlyList<AuditProgressRow> rows)
    {
        var latest = LatestPerIteration(rows);
        var points = new List<TracePoint>(latest.Count);
        HashSet<string>? previousGate = null;

        foreach (var row in latest)
        {
            var gate = Gate(row);

            // The distinguishing signal. An empty gate is not "the same gate" as an empty gate: a pass
            // followed by a pass is convergence, not repetition.
            var same = gate.Count > 0 && previousGate is not null && gate.SetEquals(previousGate);

            points.Add(new TracePoint(
                row.Iteration,
                row.BlockingFindings,
                row.Status.Equals("complete", StringComparison.OrdinalIgnoreCase),
                same));

            previousGate = gate;
        }

        return points;
    }

    /// <summary>One row per iteration: the latest recording of it, oldest iteration first. An iteration
    /// can appear under more than one work attempt, and the newest recording is the one that is true.</summary>
    internal static IReadOnlyList<AuditProgressRow> LatestPerIteration(IReadOnlyList<AuditProgressRow> rows)
        => rows.Count == 0
            ? []
            : [.. rows
                .GroupBy(r => r.Iteration)
                .Select(g => g.OrderByDescending(r => r.RecordedAt).First())
                .OrderBy(r => r.Iteration)];

    /// <summary>Which auditors objected — the gate, by name.</summary>
    private static HashSet<string> Gate(AuditProgressRow row) => [.. row.Blocking.Select(f => f.AuditorName)];

    /// <summary>
    /// The iteration cap this item is working under. The audit rows carry the number the orchestrator
    /// actually enforced, which beats the project's configured default when the two differ.
    /// </summary>
    internal static int CeilingFor(
        IReadOnlyList<AuditProgressRow> rows,
        WorkItemRow item,
        IReadOnlyDictionary<string, int> ceilings)
    {
        if (rows.Count > 0)
        {
            var latest = rows.OrderByDescending(r => r.RecordedAt).First();
            if (latest.MaxIterations > 0)
            {
                return latest.MaxIterations;
            }
        }

        return item.ProjectId is { Length: > 0 } project && ceilings.TryGetValue(project, out var cap) && cap > 0
            ? cap
            : 0;
    }

    // -------------------------------------------------------------------------------------------
    // Convergence: the shape of the loop
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the shape off the trace. Order matters: passing beats everything, a short trace has no
    /// shape to read, and oscillation beats both stuck and converging because a sawtooth that dips is
    /// still a sawtooth.
    /// </summary>
    internal static Convergence Shape(IReadOnlyList<TracePoint> points)
    {
        List<TracePoint> complete = [.. points.Where(p => p.Complete)];

        if (IsPassed(complete))
        {
            return Convergence.Passed;
        }

        if (IsNew(complete))
        {
            return Convergence.New;
        }

        if (IsOscillating(points))
        {
            return Convergence.Oscillating;
        }

        if (IsStuck(complete))
        {
            return Convergence.Stuck;
        }

        // Converging is both a shape and the default. IsConverging states the clear case; anything that
        // is not passing, new, oscillating or stuck is a noisy descent, which is ordinary work.
        return Convergence.Converging;
    }

    /// <summary>The last completed iteration found nothing blocking.</summary>
    internal static bool IsPassed(IReadOnlyList<TracePoint> complete)
        => complete.Count > 0 && complete[^1].BlockingFindings == 0;

    /// <summary>Too few completed iterations to have a direction.</summary>
    internal static bool IsNew(IReadOnlyList<TracePoint> complete) => complete.Count < ShapeMinPoints;

    /// <summary>
    /// Findings going down and back up within the recent window. Two rises make it a pattern rather than
    /// one rework that uncovered something; a rise where the same auditors objected again is the direct
    /// evidence that the loop is not consuming its own output, and enough turns is the same story told
    /// by the shape alone.
    /// </summary>
    internal static bool IsOscillating(IReadOnlyList<TracePoint> points)
    {
        List<TracePoint> window = [.. points.Skip(Math.Max(0, points.Count - OscillationWindow))];
        if (window.Count < 3)
        {
            return false;
        }

        var rises = 0;
        var risesOnTheSameGate = 0;
        var turns = 0;
        var lastDirection = 0;

        for (var i = 1; i < window.Count; i++)
        {
            var delta = window[i].BlockingFindings - window[i - 1].BlockingFindings;
            if (delta > 0)
            {
                rises++;
                if (window[i].SameGateAsPrevious)
                {
                    risesOnTheSameGate++;
                }
            }

            var direction = Math.Sign(delta);
            if (direction != 0)
            {
                if (lastDirection != 0 && direction != lastDirection)
                {
                    turns++;
                }

                lastDirection = direction;
            }
        }

        return rises >= OscillationRises
            && (risesOnTheSameGate > 0 || turns >= OscillationDirectionChanges);
    }

    /// <summary>The same non-zero verdict, several completed iterations running. Rework is happening and
    /// changing nothing.</summary>
    internal static bool IsStuck(IReadOnlyList<TracePoint> complete)
    {
        if (complete.Count < StuckRun)
        {
            return false;
        }

        List<TracePoint> tail = [.. complete.Skip(complete.Count - StuckRun)];
        var count = tail[0].BlockingFindings;
        return count > 0 && tail.TrueForAll(p => p.BlockingFindings == count);
    }

    /// <summary>
    /// Heading down: either the latest iteration beat the worst of the three before it, or the last
    /// three did not go up and something in the last five genuinely came down.
    /// </summary>
    internal static bool IsConverging(IReadOnlyList<TracePoint> complete)
    {
        if (complete.Count < ShapeMinPoints)
        {
            return false;
        }

        var last = complete[^1].BlockingFindings;

        List<TracePoint> previous = [.. complete.Take(complete.Count - 1).TakeLast(3)];
        if (previous.Count > 0 && last < previous.Max(p => p.BlockingFindings))
        {
            return true;
        }

        List<TracePoint> lastThree = [.. complete.Skip(complete.Count - 3)];
        var nonIncreasing = lastThree[0].BlockingFindings >= lastThree[1].BlockingFindings
            && lastThree[1].BlockingFindings >= lastThree[2].BlockingFindings;

        List<TracePoint> lastFive = [.. complete.Skip(Math.Max(0, complete.Count - 5))];
        var cameDown = false;
        for (var i = 1; i < lastFive.Count; i++)
        {
            if (lastFive[i].BlockingFindings < lastFive[i - 1].BlockingFindings)
            {
                cameDown = true;
            }
        }

        return nonIncreasing && cameDown;
    }

    // -------------------------------------------------------------------------------------------
    // Motion: is it doing anything, and whose problem is it
    // -------------------------------------------------------------------------------------------

    /// <summary>Motion plus the two things that come with it: the line the row shows, and whether it is
    /// a person who is being waited on.</summary>
    internal readonly record struct MotionReading(Motion Motion, string Why, bool NeedsPerson);

    /// <summary>
    /// Reads motion in the order the questions matter: is it stopped on purpose, is it stopped waiting
    /// on something outside the pipeline, is it stopped for no stated reason at all — and only then, by
    /// elimination, is it moving.
    /// </summary>
    internal static MotionReading ReadMotion(OverviewInputs inputs, WorkItemRow item, TimeSpan since)
    {
        var resume = SoonestResume(inputs.Now, item);

        if (Array.IndexOf(ParkedStates, item.State) >= 0 || resume is not null)
        {
            var why = resume is { } at
                ? Inv($"resumes {Clock(at)}")
                : item.State switch
                {
                    "WaitingForAgentResume" => "agent paused",
                    "WaitingForTransientRetry" => "waiting to retry",
                    _ => "waiting for quota",
                };

            return new MotionReading(Motion.Parked, why, NeedsPerson: false);
        }

        if (item.State == "Queued" && !item.DependsOnSatisfied)
        {
            var n = item.DependsOn?.Count ?? 0;
            return new MotionReading(
                Motion.Blocked,
                n > 0 ? Inv($"waiting on {n} {Plural(n, "dependency", "dependencies")}") : "waiting on a dependency",
                NeedsPerson: false);
        }

        if (item.State == "NeedsOperatorInput")
        {
            var open = Questions(inputs, item.Id);
            return new MotionReading(
                Motion.Blocked,
                open > 0 ? Inv($"{open} open {Plural(open, "question", "questions")}") : "needs a decision",
                NeedsPerson: true);
        }

        if (item.IsFailed)
        {
            return new MotionReading(Motion.Blocked, FailureLine(item), NeedsPerson: true);
        }

        if ((item.IsActive || Array.IndexOf(LivePhases, item.State) >= 0) && since > WedgeAfter)
        {
            return new MotionReading(Motion.Wedged, Inv($"quiet for {Duration(since)}"), NeedsPerson: false);
        }

        // Moving. A queued item that is genuinely eligible is moving too — it is waiting for the
        // dispatcher, which is the system working — but past half an hour that is worth saying out loud.
        var waiting = item.State == "Queued" && item.DependsOnSatisfied && since > QueuedPatience;
        return new MotionReading(Motion.Moving, waiting ? "waiting for a slot" : string.Empty, NeedsPerson: false);
    }

    /// <summary>The earliest resume time the orchestrator has published for this item, if it is still in
    /// the future. Only one is ever shown, and the soonest is the one that governs.</summary>
    private static DateTimeOffset? SoonestResume(DateTimeOffset now, WorkItemRow item)
    {
        DateTimeOffset? soonest = null;
        foreach (var candidate in new[] { item.NextQuotaRetryAt, item.NextTransientRetryAt, item.QuotaResetAt })
        {
            if (candidate is { } at && at > now && (soonest is null || at < soonest))
            {
                soonest = at;
            }
        }

        return soonest;
    }

    /// <summary>Why a failed item needs a person, in one clause. The kind is the orchestrator's own
    /// classification and is worth more than the first line of a stack trace, so it wins.</summary>
    private static string FailureLine(WorkItemRow item)
    {
        if (item.FailureKind is { Length: > 0 } kind)
        {
            return Inv($"failed: {kind}");
        }

        if (item.LastError is { Length: > 0 } error)
        {
            var line = error.Split('\n')[0].Trim();
            if (line.Length > 60)
            {
                line = string.Concat(line.AsSpan(0, 59), "…");
            }

            if (line.Length > 0)
            {
                return Inv($"failed: {line}");
            }
        }

        return "needs a decision";
    }

    // -------------------------------------------------------------------------------------------
    // The line the row shows, and where the row sorts
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// One short line per row. A stopped item's reason always wins: knowing it resumes at 06:00 is more
    /// use than knowing its audit loop is descending. Only when the item is moving does the shape of the
    /// loop get to speak.
    /// </summary>
    private static string Explain(
        MotionReading motion,
        Convergence shape,
        IReadOnlyList<TracePoint> points,
        string repeatingGate,
        int lastIteration,
        int ceiling,
        bool nearCeiling)
    {
        if (motion.Motion != Motion.Moving)
        {
            return motion.Why;
        }

        if (nearCeiling)
        {
            return Inv($"iteration {lastIteration} of {ceiling}, {(shape == Convergence.New ? "still early" : "converging")}");
        }

        if (shape == Convergence.Oscillating)
        {
            return repeatingGate.Length > 0
                ? Inv($"same gate repeating: {repeatingGate}")
                : "findings going back up";
        }

        if (shape == Convergence.Stuck)
        {
            var count = points.LastOrDefault(p => p.Complete)?.BlockingFindings ?? 0;
            return Inv($"same {count} {Plural(count, "finding", "findings")} for {StuckRun} iterations");
        }

        return motion.Why;
    }

    /// <summary>The auditors that objected again on the most recent repeat — the name of the loop that
    /// is not closing.</summary>
    internal static string RepeatingGate(IReadOnlyList<AuditProgressRow> rows)
    {
        // The names live on the rows rather than on TracePoint, which carries only the fact that the set
        // repeated. Walk back to the most recent repeat and name it.
        var latest = LatestPerIteration(rows);
        for (var i = latest.Count - 1; i >= 1; i--)
        {
            var gate = Gate(latest[i]);
            if (gate.Count > 0 && gate.SetEquals(Gate(latest[i - 1])))
            {
                return Listed([.. gate.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Where the row sorts. The order is the order the questions matter in: something stopped for no
    /// reason first, then iteration that is not progress, then work about to be thrown away at the cap,
    /// then a flat loop, then a person, then the two kinds of deliberate waiting, then everything fine.
    /// </summary>
    internal static int Rank(MotionReading motion, Convergence shape, bool nearCeiling) => motion.Motion switch
    {
        Motion.Wedged => RankWedged,
        _ when shape == Convergence.Oscillating => RankOscillating,
        _ when nearCeiling => RankNearCeiling,
        _ when shape == Convergence.Stuck => RankStuck,
        _ when motion.NeedsPerson => RankNeedsPerson,
        Motion.Parked => RankParked,
        Motion.Blocked => RankBlocked,
        _ => RankMoving,
    };
}
