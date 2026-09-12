using System.Globalization;

namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// THE NOW-WORKING CONTRACT
//
// The overview answers "should I do something". This answers a different question, and it is a real one:
// "is the fleet alive right now, and what is it doing this second". It is the screen you leave on a
// spare monitor — half status board, half pleasure — so it is built to be read from across a room and
// to reward being watched.
//
// Four things are on it, in this order of importance:
//
//   1. SLOTS      one big card per busy dispatch slot: who, what, which phase, how long, and the last
//                 few lines the agent actually printed. Free slots are drawn as dim outlines, because
//                 idle capacity is a fact and an absence is not.
//   2. NUMBERS    a column of large figures that count to their new value when they change.
//   3. TICKER     the orchestrator's own event feed as a log, newest first, last dozen kept.
//   4. PULSE      events per minute over the last hour, the cumulative flow chart, and quota gauges.
//
// THE ONE RULE: nothing here is invented. Every bar, dot and number comes from the orchestrator — the
// board, the overview, the SSE feed, the agents' stdout. Motion that is not backed by a change is a lie
// that happens to look busy, which is the exact failure mode a screen like this invites. The only
// animation not caused by data is the second hand on an elapsed timer and the gentle breathing of a
// quota gauge that is genuinely burning; both are switched off by CALM.
//
// Everything in this file is an immutable record or enum. No I/O, no Avalonia, no MVVM.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// What a line on the wall means, in the app's one-meaning-per-hue vocabulary.
/// </summary>
public enum WallTone
{
    /// <summary>Present, not asking for anything. Faint.</summary>
    Idle,

    /// <summary>In motion — a turn running, a phase starting. Sky.</summary>
    Working,

    /// <summary>Finished whole. Mint.</summary>
    Landed,

    /// <summary>Blocked on a person. Amber.</summary>
    Attention,

    /// <summary>Failed. Pink.</summary>
    Failed,
}

/// <summary>
/// One dispatch slot as the wall draws it — busy or free.
/// </summary>
/// <param name="Index">Position on the wall; free slots sort after busy ones.</param>
/// <param name="ItemId">Empty for a free slot, which is the one thing that distinguishes the two.</param>
/// <param name="Phase">The phase as a person says it: Working, Auditing, Reworking, Merging.</param>
/// <param name="Since">When the item entered this phase — the elapsed timer counts from here.</param>
/// <param name="Trace">The item's audit loop, when it has reached audit; null otherwise.</param>
/// <param name="Output">The last few lines the agent printed, oldest first. Empty until a tail lands.</param>
public sealed record SlotCard(
    int Index,
    string ItemId,
    string Title,
    string Agent,
    string Phase,
    string Project,
    string State,
    DateTimeOffset Since,
    ItemTrace? Trace,
    IReadOnlyList<string> Output)
{
    /// <summary>A slot with nothing in it. Drawn, not omitted: capacity you cannot see is capacity you
    /// forget you have.</summary>
    public static SlotCard Free(int index) => new(
        index, string.Empty, "free", string.Empty, string.Empty, string.Empty, string.Empty,
        default, null, []);

    public bool IsFree => ItemId.Length == 0;

    public bool IsBusy => ItemId.Length > 0;

    public bool HasTrace => Trace is { Points.Count: > 0 };

    public bool HasOutput => Output.Count > 0;

    /// <summary>Sky while it runs, amber when it is asking for a person, pink when it broke.</summary>
    public WallTone Tone => State switch
    {
        "Failed" or "AuditFailed" or "MergeConflictResolutionFailed" or "AbandonedAfterRecoveryAttempts" => WallTone.Failed,
        "NeedsOperatorInput" => WallTone.Attention,
        "Done" or "Merged" => WallTone.Landed,
        _ => IsBusy ? WallTone.Working : WallTone.Idle,
    };

    public Motion Motion => Trace?.Motion ?? (IsBusy ? Motion.Moving : Motion.Parked);

    public string ShortId => ItemId.Length >= 8 ? ItemId[..8] : ItemId;

    /// <summary>
    /// The three output lines, oldest first, as three fields rather than a list.
    /// </summary>
    /// <remarks>
    /// Because they are not equals: the newest line is what the agent is doing and the two above it are
    /// context, so the card fades them. A list plus an index-to-opacity converter would say the same
    /// thing with more machinery and no more meaning.
    /// </remarks>
    public string Older => Line(3);

    public string Recent => Line(2);

    public string Latest => Line(1);

    private string Line(int fromEnd) => Output.Count >= fromEnd ? Output[^fromEnd] : string.Empty;

    /// <summary>The identity line under the title: where it lives and what is running it.</summary>
    public string Provenance => IsFree
        ? string.Empty
        : string.Join("  ·  ", new[] { Agent, Project, ShortId }.Where(p => p.Length > 0));
}

/// <summary>
/// One line of the log: when, what it happened to, and what happened.
/// </summary>
/// <param name="Id">The feed's own sequence number, so a replayed event is never printed twice.</param>
/// <param name="Subject">The item's title, or the subsystem's name for a fleet-level event.</param>
/// <param name="Verb">"→ Auditing", "landed", "audit passed", "queue paused".</param>
public sealed record TickerLine(long Id, DateTimeOffset At, string Subject, string Verb, WallTone Tone)
{
    /// <summary>Local time, to the second: this screen is watched, so the seconds are the point.</summary>
    public string Time => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public bool HasSubject => Subject.Length > 0;

    /// <summary>The whole line as one string, for a log that has no room for columns.</summary>
    public string Text => HasSubject ? $"{Time}  {Subject}  ·  {Verb}" : $"{Time}  {Verb}";
}

/// <summary>How a headline figure is written out. The number animates; the shape of it does not.</summary>
public enum NumberFormat
{
    /// <summary>A plain count: 34.</summary>
    Count,

    /// <summary>Dollars: $12.40.</summary>
    Money,

    /// <summary>A span given in minutes: 6h 40m.</summary>
    Duration,

    /// <summary>A fraction of a known whole: "2 / 3".</summary>
    Ratio,
}

/// <summary>
/// One large figure on the wall.
/// </summary>
/// <param name="Key">Stable across rebuilds — it is what lets a number animate from its old value to
/// its new one rather than being replaced by a different number that happens to sit in the same box.</param>
/// <param name="Value">The figure itself; <see cref="Of"/> is the denominator for a ratio.</param>
/// <param name="Spark">Recent history where the overview already keeps some; empty otherwise. Never
/// synthesised — a sparkline drawn from one point is a decoration pretending to be evidence.</param>
public sealed record BigNumber(
    string Key,
    string Label,
    double Value,
    NumberFormat Format,
    string Caption,
    TileTone Tone,
    IReadOnlyList<double> Spark,
    double? Of = null)
{
    public bool HasSpark => Spark.Count >= 2;

    public bool HasCaption => Caption.Length > 0;

    /// <summary>The figure at rest, for a screen with the count-up switched off.</summary>
    public string Text => NowWorkingModel.Format(Value, Format, Of);
}

/// <summary>
/// The whole wall, ready to draw: what the pure model makes of one moment.
/// </summary>
/// <param name="Slots">Busy slots first, longest-running first; free slots after them.</param>
/// <param name="Numbers">The headline column, in reading order.</param>
/// <param name="Minutes">Events per minute over the last hour, oldest first — always 60 long, so a
/// quiet fleet draws a flat chart rather than an empty one.</param>
public sealed record Wall(
    DateTimeOffset At,
    IReadOnlyList<SlotCard> Slots,
    IReadOnlyList<BigNumber> Numbers,
    IReadOnlyList<int> Minutes)
{
    public int BusySlots => Slots.Count(s => s.IsBusy);

    public int TotalSlots => Slots.Count;

    /// <summary>The one-line state of the world, as the header says it.</summary>
    public string Headline => BusySlots == 0
        ? "Nothing running"
        : $"{BusySlots} of {TotalSlots} slots busy";
}
