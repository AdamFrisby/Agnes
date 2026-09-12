using System.Globalization;
using System.Text;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// Everything the wall shows, derived. Pure: the same inputs always make the same screen, so every part
/// of it can be tested without a fleet, a dispatcher or a clock.
/// </summary>
/// <remarks>
/// Nothing here reads the network. The wall is assembled from surfaces that already exist — the board's
/// Now horizon, the overview record, the work-item list and the orchestrator's own event feed — because
/// a second screen that gathers its own copy of the fleet's state would double the load on an
/// orchestrator that is busy doing the actual work, and would then disagree with the first screen.
/// </remarks>
public static class NowWorkingModel
{
    /// <summary>How many lines of an agent's output a card carries. Three is the most that stays
    /// readable at the size these cards are drawn, and the newest is the one that matters.</summary>
    public const int OutputLines = 3;

    /// <summary>How many log lines the ticker keeps. A dozen is about a minute of a busy fleet — enough
    /// to see a transition you looked away from, short enough to still read as a list.</summary>
    public const int TickerDepth = 12;

    /// <summary>The heartbeat's span, one bar per minute.</summary>
    public const int HeartbeatMinutes = 60;

    // ---- slots ----------------------------------------------------------------------------------

    /// <summary>
    /// One card per dispatch slot: the board's running chains first, then an outline for each slot the
    /// orchestrator says it has and is not using.
    /// </summary>
    /// <param name="tails">The last output seen per item id; items with nothing yet simply have none.</param>
    /// <remarks>
    /// Busy cards are ordered by how long they have been in their current phase, longest first. That is
    /// a stable order — the key does not change while the card sits there — which matters more here than
    /// any cleverer ranking would: a wall whose cards reshuffle on every tick is unreadable, and the
    /// longest-running item is in any case the one most likely to want a look.
    /// </remarks>
    public static IReadOnlyList<SlotCard> Slots(
        Board? board,
        Overview? overview,
        IReadOnlyDictionary<string, string>? tails,
        DateTimeOffset now)
    {
        var traces = new Dictionary<string, ItemTrace>(StringComparer.Ordinal);
        if (overview is not null)
        {
            foreach (var trace in overview.Attention.Concat(overview.Healthy))
            {
                traces[trace.Item.Id] = trace;
            }
        }

        var cards = new List<SlotCard>();
        foreach (var chain in board?.Now ?? [])
        {
            var item = chain.Head;
            traces.TryGetValue(item.Id, out var trace);
            var tail = tails is not null && tails.TryGetValue(item.Id, out var text) ? text : string.Empty;
            cards.Add(new SlotCard(
                Index: cards.Count,
                ItemId: item.Id,
                Title: item.Title,
                Agent: item.Agent ?? string.Empty,
                Phase: Phase(item.State),
                Project: item.ProjectId ?? string.Empty,
                State: item.State,
                // UpdatedAt is when the orchestrator last wrote the item, which for a running item is
                // when it entered this phase. Clamped to now: a host whose clock is ahead would
                // otherwise show a timer counting up from a negative number.
                Since: item.UpdatedAt > now ? now : item.UpdatedAt,
                Trace: trace,
                Output: Lines(tail, OutputLines)));
        }

        cards.Sort((a, b) => a.Since.CompareTo(b.Since));

        var total = Math.Max(board?.Slots.Total ?? 0, cards.Count);
        var ordered = new List<SlotCard>(total);
        for (var i = 0; i < cards.Count; i++)
        {
            ordered.Add(cards[i] with { Index = i });
        }

        for (var i = cards.Count; i < total; i++)
        {
            ordered.Add(SlotCard.Free(i));
        }

        return ordered;
    }

    /// <summary>The orchestrator's state word as a person says it. Anything unrecognised is passed
    /// through rather than guessed at — a new state is better read than mislabelled.</summary>
    public static string Phase(string state) => state switch
    {
        "Working" => "Working",
        "Auditing" => "Auditing",
        "Reworking" => "Reworking",
        "Merging" => "Merging",
        "WorkComplete" => "Between phases",
        "UpstreamPushing" => "Pushing",
        "NeedsOperatorInput" => "Asking you",
        "Queued" => "Queued",
        _ => state,
    };

    /// <summary>
    /// The last few lines an agent printed, oldest first.
    /// </summary>
    /// <remarks>
    /// Terminal control sequences are stripped: the tail is a PTY's output, and an unstripped escape
    /// renders as mojibake on a screen that is meant to be looked at rather than read closely. Blank
    /// lines are dropped for the same reason — three lines of which two are empty says less than one.
    /// </remarks>
    public static IReadOnlyList<string> Lines(string tail, int count)
    {
        if (string.IsNullOrWhiteSpace(tail) || count <= 0)
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var raw in tail.Split('\n'))
        {
            var line = Strip(raw).TrimEnd();
            if (line.Trim().Length == 0)
            {
                continue;
            }

            lines.Add(line.Length > MaxLine ? line[..MaxLine] + "…" : line);
        }

        return lines.Count <= count ? lines : lines[^count..];
    }

    private const int MaxLine = 200;

    /// <summary>The escape a terminal control sequence opens with, and the bell an OSC string ends with.</summary>
    private const char Esc = '\u001b';
    private const char Bel = '\u0007';

    /// <summary>Removes ANSI CSI and OSC sequences, and the carriage returns a progress bar leaves.</summary>
    private static string Strip(string text)
    {
        if (text.IndexOf(Esc) < 0 && text.IndexOf('\r') < 0)
        {
            return text;
        }

        var output = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                // A progress bar redraws its line: the last redraw is the one worth keeping.
                output.Clear();
                continue;
            }

            if (c != Esc || i + 1 >= text.Length)
            {
                output.Append(c);
                continue;
            }

            var next = text[i + 1];
            if (next == '[')
            {
                i += 2;
                while (i < text.Length && !char.IsBetween(text[i], '@', '~'))
                {
                    i++;
                }
            }
            else if (next == ']')
            {
                i += 2;
                while (i < text.Length && text[i] != Bel && text[i] != Esc)
                {
                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        return output.ToString();
    }

    // ---- the ticker -----------------------------------------------------------------------------

    /// <summary>
    /// One feed event as a log line, or null for an event that says nothing a person would read.
    /// </summary>
    /// <remarks>
    /// <para>The feed is chattier than the log should be. One item moving from Working to Auditing emits
    /// <c>work_item.auditing</c>, <c>audit.started</c> and <c>iteration.started</c> within the same
    /// second, and printing all three says one thing three times in the space of three. So the log takes
    /// the item- and fleet-level events — the ones whose subject is a thing an operator recognises — and
    /// the finer-grained phase events feed the heartbeat instead, where being numerous is the point.</para>
    ///
    /// <para><paramref name="previousState"/> is what the wall last saw this item in, which is what turns
    /// "→ Auditing" into "Working → Auditing". It is not in the event: the feed carries the item's state
    /// after the transition and not before.</para>
    /// </remarks>
    internal static TickerLine? Describe(CodeyBoxEvent evt, string? previousState = null)
    {
        var title = Shorten(evt.Title ?? string.Empty);
        var (verb, tone, keep) = Verb(evt, previousState);
        return keep ? new TickerLine(evt.Id, evt.OccurredAt, title, verb, tone) : null;
    }

    private static (string Verb, WallTone Tone, bool Keep) Verb(CodeyBoxEvent evt, string? previousState)
    {
        switch (evt.Type)
        {
            case "work_item.done":
                return ("landed", WallTone.Landed, true);
            case "work_item.merged":
                return ("merged", WallTone.Landed, true);
            case "work_item.audit_passed":
                return ("audit passed", WallTone.Landed, true);
            case "work_item.pull_request_opened":
                return ("PR opened", WallTone.Landed, true);
            case "work_item.failed":
                return ("failed", WallTone.Failed, true);
            case "work_item.recovered":
                return ("recovered", WallTone.Attention, true);
            case "work_item.auto_retry":
                return ("retrying", WallTone.Attention, true);
            case "work_item.waiting_for_transient_retry":
                return ("backing off", WallTone.Attention, true);
            case "work_item.needs_operator_input":
                return ("needs you", WallTone.Attention, true);
            case "work_item.suggestion":
                return ("raised a suggestion", WallTone.Idle, true);
            case "queue.paused":
                return ("queue paused", WallTone.Attention, true);
            case "queue.resumed":
                return ("queue resumed", WallTone.Working, true);
            default:
                break;
        }

        if (evt.Type.StartsWith("sandbox.", StringComparison.Ordinal))
        {
            return (evt.Type["sandbox.".Length..].Replace('_', ' '), WallTone.Idle, true);
        }

        if (evt.IsWorkItem && evt.State is { Length: > 0 } state)
        {
            var word = Phase(state);
            var arrow = previousState is { Length: > 0 } from && !string.Equals(from, state, StringComparison.Ordinal)
                ? $"{Phase(from)} → {word}"
                : $"→ {word}";
            return (arrow, ToneOf(state), true);
        }

        // Everything else — iteration.*, audit.*, merge.*, upstream.* — is real and is counted by the
        // heartbeat, but it duplicates a work-item line that is already in the log.
        return (string.Empty, WallTone.Idle, false);
    }

    private static WallTone ToneOf(string state) => state switch
    {
        "Done" or "Merged" => WallTone.Landed,
        "Failed" or "AuditFailed" or "MergeConflictResolutionFailed" or "AbandonedAfterRecoveryAttempts" => WallTone.Failed,
        "NeedsOperatorInput" => WallTone.Attention,
        "Cancelled" => WallTone.Idle,
        _ => WallTone.Working,
    };

    /// <summary>A title cut to what fits one line of a log without pushing the verb off the end.</summary>
    public static string Shorten(string title, int max = 68)
    {
        var text = title.Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    /// <summary>
    /// Adds a line to the log, newest first, keeping at most <paramref name="max"/>.
    /// </summary>
    /// <remarks>
    /// Two guards, both learned from the live feed. A replayed frame carries an id the log has already
    /// printed and must not be printed twice; and a burst commonly repeats the same subject and verb
    /// within a second (an item entering Auditing emits the state twice as its auditors are scheduled),
    /// which is one event to a reader.
    /// </remarks>
    public static IReadOnlyList<TickerLine> Push(IReadOnlyList<TickerLine> log, TickerLine line, int max = TickerDepth)
    {
        if (log.Count > 0)
        {
            var newest = log[0];
            if (newest.Id == line.Id ||
                (newest.Subject == line.Subject && newest.Verb == line.Verb && line.At - newest.At < TimeSpan.FromSeconds(2)))
            {
                return log;
            }
        }

        var next = new List<TickerLine>(Math.Min(max, log.Count + 1)) { line };
        next.AddRange(log.Take(max - 1));
        return next;
    }

    // ---- the heartbeat --------------------------------------------------------------------------

    /// <summary>
    /// Events per minute over the last hour, oldest first, always <paramref name="minutes"/> long.
    /// </summary>
    /// <remarks>
    /// The last bucket is the minute in progress and is therefore always short — which is correct and is
    /// why the chart is drawn as bars rather than as a line: a partial bar reads as a minute that has
    /// not finished, a dipping line reads as a fleet falling over.
    /// </remarks>
    public static IReadOnlyList<int> Heartbeat(
        IEnumerable<DateTimeOffset> events, DateTimeOffset now, int minutes = HeartbeatMinutes)
    {
        var buckets = new int[Math.Max(1, minutes)];
        foreach (var at in events)
        {
            var age = (now - at).TotalMinutes;
            if (age >= buckets.Length)
            {
                continue;
            }

            // An event stamped slightly in the future (clock skew between us and the orchestrator) is
            // this minute's, not a reason to drop it.
            var index = buckets.Length - 1 - (int)Math.Max(0, age);
            buckets[index]++;
        }

        return buckets;
    }

    // ---- the numbers ----------------------------------------------------------------------------

    /// <summary>
    /// The headline column, in reading order: what landed, what is moving, what it is costing, and what
    /// is waiting on a person.
    /// </summary>
    /// <remarks>
    /// Everything here is derived from surfaces the tab already has. Where the overview keeps a series
    /// for a figure, the sparkline is that series; where it does not, the figure goes without one rather
    /// than being given a line drawn from a single point.
    /// </remarks>
    public static IReadOnlyList<BigNumber> Numbers(
        Board? board,
        Overview? overview,
        IReadOnlyList<WorkItemRow> items,
        DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        var landedToday = items.Count(i => i.State == "Done" && i.UpdatedAt.ToLocalTime().Date == today);
        var flow = overview?.Flow;

        var numbers = new List<BigNumber>
        {
            new("landed-today", "LANDED TODAY", landedToday, NumberFormat.Count,
                landedToday == 0 ? "nothing has landed yet today" : "items reached Done today",
                landedToday > 0 ? TileTone.Active : TileTone.Neutral,
                DailyLanded(flow, 14)),

            new("landed-week", "LANDED THIS WEEK", overview?.Sample.Landed7d ?? WeekLanded(flow),
                NumberFormat.Count, "the last seven days", TileTone.Neutral, RollingWeek(flow, 14)),

            new("in-flight", "IN FLIGHT", board?.NowCount ?? overview?.Sample.InMotion ?? 0,
                NumberFormat.Count, "items occupying a slot", TileTone.Active, []),

            new("slots", "SLOTS", board?.Slots.Busy ?? 0, NumberFormat.Ratio,
                board is { Slots.Total: 0 } ? "the orchestrator reports no slots" : "busy of total",
                board is { Slots.Busy: > 0 } ? TileTone.Active : TileTone.Neutral,
                [], Of: board?.Slots.Total ?? 0),

            new("waiting", "WAITING", board?.WaitingCount ?? 0, NumberFormat.Count,
                "cannot start until something changes", TileTone.Neutral, []),
        };

        if (overview?.Burn is { } burn)
        {
            numbers.Add(new BigNumber(
                "drain", "TIME TO DRAIN", burn.Wall.TotalMinutes, NumberFormat.Duration,
                $"{burn.Remaining} left at today's pace", TileTone.Neutral, burn.SparkHours));
        }

        var cost = items
            .Where(i => i.UpdatedAt.ToLocalTime().Date == today && i.UsageTotal is not null)
            .Sum(i => (double)i.UsageTotal!.CostUsd);
        if (cost > 0)
        {
            // Honest caption: the orchestrator totals cost per item, not per day, so this is what today's
            // movers have cost in total — not what was spent today.
            numbers.Add(new BigNumber(
                "cost", "COST ON TODAY'S ITEMS", cost, NumberFormat.Money,
                "total spend of the items that moved today", TileTone.Neutral, []));
        }

        var needsYou = overview?.Sample.BlockedOnYou ?? 0;
        numbers.Add(new BigNumber(
            "needs-you", "NEEDS YOU", needsYou, NumberFormat.Count,
            needsYou == 0 ? "nothing is waiting on you" : "questions and decisions",
            needsYou > 0 ? TileTone.Attention : TileTone.Neutral, []));

        return numbers;
    }

    /// <summary>Landed per day over the tail of the flow series — the difference between consecutive cumulative
    /// counts, which is the only way this plugin can know a daily rate.</summary>
    private static IReadOnlyList<double> DailyLanded(FlowSeries? flow, int days)
    {
        if (flow is not { Days.Count: >= 2 })
        {
            return [];
        }

        var d = flow.Days;
        var from = Math.Max(1, d.Count - days);
        return [.. Enumerable.Range(from, d.Count - from).Select(i => (double)(d[i].Landed - d[i - 1].Landed))];
    }

    /// <summary>A rolling seven-day total, so the week's figure has a shape behind it.</summary>
    private static IReadOnlyList<double> RollingWeek(FlowSeries? flow, int points)
    {
        if (flow is not { Days.Count: >= 9 })
        {
            return [];
        }

        var d = flow.Days;
        var from = Math.Max(7, d.Count - points);
        return [.. Enumerable.Range(from, d.Count - from).Select(i => (double)(d[i].Landed - d[i - 7].Landed))];
    }

    private static int WeekLanded(FlowSeries? flow)
    {
        if (flow is not { Days.Count: >= 8 })
        {
            return 0;
        }

        var d = flow.Days;
        return d[^1].Landed - d[^8].Landed;
    }

    /// <summary>A figure written out. The number animates; the way it is written does not.</summary>
    public static string Format(double value, NumberFormat format, double? of = null) => format switch
    {
        NumberFormat.Money => value >= 100
            ? "$" + Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : "$" + value.ToString("0.00", CultureInfo.InvariantCulture),
        NumberFormat.Duration => Duration(TimeSpan.FromMinutes(Math.Max(0, value))),
        NumberFormat.Ratio => FormattableString.Invariant($"{Math.Round(value):0} / {Math.Round(of ?? 0):0}"),
        _ => Math.Round(value).ToString("0", CultureInfo.InvariantCulture),
    };

    /// <summary>A span at the coarsest scale that still says something: "2d 4h", "6h 40m", "48m".</summary>
    public static string Duration(TimeSpan span)
    {
        // "< 1m", not "under a minute": this is a headline figure in a fixed-width tile, and the words
        // were the one value that did not fit — a live fleet with an empty queue drew it trimmed to
        // "under …", which says nothing at all.
        if (span.TotalMinutes < 1)
        {
            return "< 1m";
        }

        if (span.TotalHours < 1)
        {
            return FormattableString.Invariant($"{(int)span.TotalMinutes}m");
        }

        if (span.TotalDays < 1)
        {
            return FormattableString.Invariant($"{(int)span.TotalHours}h {span.Minutes:00}m");
        }

        return FormattableString.Invariant($"{(int)span.TotalDays}d {span.Hours:00}h");
    }

    /// <summary>An elapsed timer, ticking every second: "42s", "4m 12s", "1h 04m".</summary>
    public static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalMinutes < 1)
        {
            return FormattableString.Invariant($"{span.Seconds}s");
        }

        if (span.TotalHours < 1)
        {
            return FormattableString.Invariant($"{span.Minutes}m {span.Seconds:00}s");
        }

        return FormattableString.Invariant($"{(int)span.TotalHours}h {span.Minutes:00}m");
    }

    // ---- motion ---------------------------------------------------------------------------------

    /// <summary>
    /// Where a count-up has got to: <paramref name="from"/> at the start, <paramref name="to"/> at the
    /// end, eased so it arrives rather than stopping dead.
    /// </summary>
    /// <remarks>
    /// Cubic ease-out, which is the one that reads as a number settling. The caller passes elapsed and
    /// total rather than a clock, so this stays a function of its arguments and the animation can be
    /// tested at any point on its curve without waiting for one.
    /// </remarks>
    public static double CountUp(double from, double to, TimeSpan elapsed, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || elapsed >= duration)
        {
            return to;
        }

        if (elapsed <= TimeSpan.Zero)
        {
            return from;
        }

        var t = elapsed / duration;
        var eased = 1 - Math.Pow(1 - t, 3);
        return from + ((to - from) * eased);
    }

    /// <summary>
    /// How bright a flash still is: 1 at the moment it fired, 0 once it has decayed.
    /// </summary>
    /// <remarks>
    /// Linear on purpose. A flash is a notification, not a transition — it has to be gone at a
    /// predictable moment so that two of them close together read as two events rather than as one long
    /// glow.
    /// </remarks>
    public static double Decay(TimeSpan since, TimeSpan life)
        => since <= TimeSpan.Zero ? 1 : (since >= life || life <= TimeSpan.Zero ? 0 : 1 - (since / life));
}
