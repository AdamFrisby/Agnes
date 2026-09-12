using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// How the wall gets its ticks.
/// </summary>
/// <remarks>
/// A seam, not an abstraction for its own sake. The wall is the one screen in this plugin whose whole
/// behaviour is a function of time, and a <see cref="DispatcherTimer"/> cannot be created — let alone
/// advanced — without a running dispatcher. With the clock injected, every timed behaviour (a flash
/// decaying, a number counting up, a card ordering after an event) is testable by handing it a fake and
/// stepping it, and "the timers stop when you leave the section" becomes an assertion rather than a hope.
/// </remarks>
public interface IWallClock
{
    /// <summary>Calls <paramref name="tick"/> every <paramref name="interval"/> until disposed.</summary>
    IDisposable Every(TimeSpan interval, Action tick);
}

/// <summary>The real one: an Avalonia timer on the UI thread.</summary>
public sealed class DispatcherWallClock : IWallClock
{
    public IDisposable Every(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => tick();
        timer.Start();
        return new Stop(timer);
    }

    private sealed class Stop(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

/// <summary>
/// "Now working" — the wall. A live picture of what the fleet is doing this second.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> The overview is for deciding; this is for watching. It is meant to be
/// left on a spare monitor for hours, so two things follow. Everything on it must be real — see the
/// header of <see cref="NowWorkingModel"/> — and everything decorative about it must be switchable off,
/// which is what CALM is. Calm keeps every live update and removes every flash, pulse and count-up.</para>
///
/// <para><b>What it costs.</b> Nothing it draws is gathered twice. The board and the overview are read
/// from the objects the tab already holds; the event feed is the one the queue already subscribes to,
/// handed along rather than opened again. The single request the wall makes on its own is the stdout
/// tail of each <em>running</em> item — at most a handful, a few seconds apart, and only while the wall
/// is the section on screen.</para>
///
/// <para><b>Two timers, both stopped when you leave.</b> A one-second tick for the things that move with
/// the clock (elapsed timers, the heartbeat's rolling window, a re-read of the board) and a frame tick
/// for the decoration, which does not run at all while calm is on.</para>
/// </remarks>
public sealed partial class NowWorkingViewModel : ObservableObject, IDisposable
{
    private readonly Func<Board?> _board;
    private readonly Func<Overview?> _overview;
    private readonly Func<IReadOnlyList<WorkItemRow>> _items;
    private readonly Func<string, CancellationToken, Task<string>>? _tail;
    private readonly Func<Action, Task> _toUi;
    private readonly Func<DateTimeOffset> _now;
    private readonly IWallClock _clock;

    /// <summary>How long a flash stays visible. Long enough to catch out of the corner of an eye, short
    /// enough that two events a second apart read as two.</summary>
    internal static readonly TimeSpan FlashLife = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long a card or a log line takes to slide into place.</summary>
    internal static readonly TimeSpan SlideLife = TimeSpan.FromMilliseconds(450);

    /// <summary>How long a number takes to travel to its new value.</summary>
    internal static readonly TimeSpan CountLife = TimeSpan.FromMilliseconds(600);

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    /// <summary>How often a running item's output is re-read. Slow enough to be nothing next to what the
    /// agents themselves are doing, fast enough that the lines visibly move.</summary>
    private static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(4);

    private IDisposable? _secondTimer;
    private IDisposable? _frameTimer;
    private CancellationTokenSource? _tails;

    /// <summary>The last output seen per running item, already trimmed to the lines a card shows.</summary>
    private readonly Dictionary<string, string> _output = new(StringComparer.Ordinal);

    /// <summary>When each feed event arrived, for the heartbeat. Pruned to the chart's own window.</summary>
    private readonly List<DateTimeOffset> _beats = [];

    /// <summary>The state the wall last saw each item in, which is what turns "→ Auditing" into
    /// "Working → Auditing". The feed carries only the state after a transition.</summary>
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);

    private Board? _lastBoard;
    private Overview? _lastOverview;

    public NowWorkingViewModel(
        Func<Board?> board,
        Func<Overview?> overview,
        Func<IReadOnlyList<WorkItemRow>> items,
        Func<Action, Task> toUi,
        Func<string, CancellationToken, Task<string>>? tail = null,
        Func<DateTimeOffset>? now = null,
        IWallClock? clock = null)
    {
        _board = board;
        _overview = overview;
        _items = items;
        _toUi = toUi;
        _tail = tail;
        _now = now ?? (() => DateTimeOffset.Now);
        _clock = clock ?? new DispatcherWallClock();

        ToggleWallCommand = new RelayCommand(() => IsWall = !IsWall);
        ToggleCalmCommand = new RelayCommand(() => IsCalm = !IsCalm);
    }

    // ---- what is on screen ------------------------------------------------------------------------

    /// <summary>One card per dispatch slot, busy first. Reconciled rather than replaced, so a card that
    /// is still the same item keeps its flash and its elapsed timer across a rebuild.</summary>
    public ObservableCollection<SlotCardViewModel> Slots { get; } = [];

    /// <summary>The headline column.</summary>
    public ObservableCollection<BigNumberViewModel> Numbers { get; } = [];

    /// <summary>The log, newest first.</summary>
    public ObservableCollection<TickerRowViewModel> Ticker { get; } = [];

    /// <summary>One gauge per agent whose quota this host can read.</summary>
    public ObservableCollection<GaugeViewModel> Gauges { get; } = [];

    /// <summary>Events per minute over the last hour, oldest first.</summary>
    [ObservableProperty]
    private IReadOnlyList<int> _minutes = NowWorkingModel.Heartbeat([], DateTimeOffset.MinValue);

    /// <summary>The cumulative flow chart's series, straight off the overview.</summary>
    [ObservableProperty]
    private FlowSeries? _flow;

    [ObservableProperty]
    private string _headline = "Waiting for the fleet…";

    /// <summary>Whether a board has arrived. Before it does the wall says so rather than drawing an
    /// empty one, which would read as an idle fleet.</summary>
    [ObservableProperty]
    private bool _hasData;

    /// <summary>The busiest minute in the heartbeat's window — the chart's own scale, said in words.</summary>
    public string Pulse
    {
        get
        {
            var total = Minutes.Sum();
            return total == 0
                ? "no events in the last hour"
                : FormattableString.Invariant($"{total} events in the last hour  ·  peak {Minutes.Max()}/min");
        }
    }

    public bool HasTicker => Ticker.Count > 0;

    public bool HasGauges => Gauges.Count > 0;

    public bool HasFlow => Flow is { HasData: true };

    partial void OnMinutesChanged(IReadOnlyList<int> value) => OnPropertyChanged(nameof(Pulse));

    partial void OnFlowChanged(FlowSeries? value) => OnPropertyChanged(nameof(HasFlow));

    // ---- the two switches -------------------------------------------------------------------------

    /// <summary>
    /// Fills the tab: the plugin's own rail and pane header are hidden and the wall has the lot. The
    /// Agnes tab strip stays — a screen you cannot leave is not a mode, it is a trap.
    /// </summary>
    [ObservableProperty]
    private bool _isWall;

    /// <summary>
    /// Decoration off. Flashes, slides, count-ups and the quota breathing stop; every live update
    /// continues. This is the setting for a screen somebody is working beside rather than watching.
    /// </summary>
    [ObservableProperty]
    private bool _isCalm;

    public string WallButtonText => IsWall ? "Exit wall" : "Wall";

    public string CalmButtonText => IsCalm ? "Calm on" : "Calm";

    public IRelayCommand ToggleWallCommand { get; }

    public IRelayCommand ToggleCalmCommand { get; }

    partial void OnIsWallChanged(bool value) => OnPropertyChanged(nameof(WallButtonText));

    partial void OnIsCalmChanged(bool value)
    {
        OnPropertyChanged(nameof(CalmButtonText));
        if (value)
        {
            Settle();
        }

        // The frame timer exists only to animate. Calm does not slow it down; it stops it.
        Animate(_running && !value);
    }

    /// <summary>Puts every animated value at rest, so switching calm on takes effect immediately rather
    /// than leaving a half-faded flash frozen on the screen.</summary>
    private void Settle()
    {
        foreach (var slot in Slots)
        {
            slot.Settle();
        }

        foreach (var row in Ticker)
        {
            row.Settle();
        }

        foreach (var number in Numbers)
        {
            number.Settle();
        }

        foreach (var gauge in Gauges)
        {
            gauge.Settle();
        }
    }

    // ---- running ----------------------------------------------------------------------------------

    private bool _running;

    public bool IsRunning => _running;

    /// <summary>
    /// Starts the wall. Idempotent — the section can be shown twice without starting two sets of timers.
    /// </summary>
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        Rebuild();
        _secondTimer = _clock.Every(Second, () => Tick(_now()));
        Animate(!IsCalm);
        StartTails();
    }

    /// <summary>
    /// Stops everything: both timers and the tail reader. Called when the section is left, because a
    /// screensaver for a screen nobody is looking at is pure cost — on this machine and on the
    /// orchestrator.
    /// </summary>
    public void Stop()
    {
        _running = false;
        _secondTimer?.Dispose();
        _secondTimer = null;
        Animate(false);
        _tails?.Cancel();
        _tails?.Dispose();
        _tails = null;
    }

    private void Animate(bool on)
    {
        if (on && _frameTimer is null)
        {
            _frameTimer = _clock.Every(Frame, () => Frames(_now()));
            return;
        }

        if (!on)
        {
            _frameTimer?.Dispose();
            _frameTimer = null;
        }
    }

    /// <summary>Whether the decoration is currently being driven. Public for the test that proves calm
    /// and leaving the section both switch it off.</summary>
    public bool IsAnimating => _frameTimer is not null;

    public void Dispose() => Stop();

    // ---- the ticks --------------------------------------------------------------------------------

    /// <summary>
    /// The one-second tick: the clock-driven half. Elapsed timers advance, the heartbeat's window rolls,
    /// and the board is re-read if the tab has built a new one.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        var board = _board();
        var overview = _overview();
        if (!ReferenceEquals(board, _lastBoard) || !ReferenceEquals(overview, _lastOverview))
        {
            Rebuild(now);
        }

        foreach (var slot in Slots)
        {
            slot.Advance(now);
        }

        Prune(now);
        Minutes = NowWorkingModel.Heartbeat(_beats, now);
    }

    /// <summary>The frame tick: decay, ease and breathe. Never runs while calm is on.</summary>
    public void Frames(DateTimeOffset now)
    {
        foreach (var slot in Slots)
        {
            slot.Frame(now);
        }

        foreach (var row in Ticker)
        {
            row.Frame(now);
        }

        foreach (var number in Numbers)
        {
            number.Frame(now);
        }

        foreach (var gauge in Gauges)
        {
            gauge.Frame(now);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-NowWorkingModel.HeartbeatMinutes);
        var drop = 0;
        while (drop < _beats.Count && _beats[drop] < cutoff)
        {
            drop++;
        }

        if (drop > 0)
        {
            _beats.RemoveRange(0, drop);
        }
    }

    // ---- the feed ---------------------------------------------------------------------------------

    /// <summary>
    /// One event off the orchestrator's feed. Called for <em>every</em> event, including the ones the log
    /// does not print: the heartbeat's whole point is that it counts what actually happened, and the
    /// phase-level events are most of what happens.
    /// </summary>
    /// <remarks>
    /// Called from the feed reader's own task. The list and the collections it touches belong to the UI
    /// thread, so the work is marshalled — and dropped entirely when the wall is not running, which is
    /// what keeps a section nobody is looking at from accumulating an hour of history.
    /// </remarks>
    internal void Note(CodeyBoxEvent evt)
    {
        if (!_running)
        {
            return;
        }

        _ = _toUi(() => Accept(evt, _now()));
    }

    /// <summary>The UI-thread half of <see cref="Note"/>, and the seam every ticker test drives.</summary>
    internal void Accept(CodeyBoxEvent evt, DateTimeOffset now)
    {
        // Stamped with our own clock, not the orchestrator's: the heartbeat is "what have I seen in the
        // last hour", and a replayed buffer carries timestamps from days ago.
        _beats.Add(now);
        Minutes = NowWorkingModel.Heartbeat(_beats, now);

        var previous = evt.WorkItemId is { Length: > 0 } id && _states.TryGetValue(id, out var was) ? was : null;
        if (evt.WorkItemId is { Length: > 0 } key && evt.State is { Length: > 0 } state)
        {
            _states[key] = state;
        }

        if (NowWorkingModel.Describe(evt, previous) is not { } line)
        {
            return;
        }

        // The log's retention and its two rejections — a replayed frame, and a burst repeating itself —
        // are decided by the pure model; this only carries out what it decided.
        var current = Ticker.Select(r => r.Line).ToList();
        var kept = NowWorkingModel.Push(current, line);
        if (kept.Count == 0 || !ReferenceEquals(kept[0], line))
        {
            return;
        }

        Ticker.Insert(0, new TickerRowViewModel(line, now, IsCalm));
        while (Ticker.Count > NowWorkingModel.TickerDepth)
        {
            Ticker.RemoveAt(Ticker.Count - 1);
        }

        OnPropertyChanged(nameof(HasTicker));
    }

    // ---- building ---------------------------------------------------------------------------------

    /// <summary>Rebuilds every panel from whatever the tab currently holds.</summary>
    public void Rebuild(DateTimeOffset? at = null)
    {
        var now = at ?? _now();
        var board = _board();
        var overview = _overview();
        _lastBoard = board;
        _lastOverview = overview;

        ApplySlots(NowWorkingModel.Slots(board, overview, _output, now), now);
        ApplyNumbers(NowWorkingModel.Numbers(board, overview, _items(), now), now);
        ApplyGauges(overview);

        Flow = overview?.Flow;
        HasData = board is not null || overview is not null;
        Headline = board is null
            ? "Waiting for the fleet…"
            : new Wall(now, [.. Slots.Select(s => s.Card)], [], Minutes).Headline;
    }

    /// <summary>
    /// Reconciles the cards, keyed on the item.
    /// </summary>
    /// <remarks>
    /// Keyed rather than positional, and that is the whole trick: a card that is still the same item
    /// keeps its elapsed timer and its unfinished flash even when it has moved along the row, while a
    /// card whose item has changed is a new card and arrives with a slide. Positional reconciliation
    /// would make every reorder look like every card being replaced.
    /// </remarks>
    private void ApplySlots(IReadOnlyList<SlotCard> cards, DateTimeOffset now)
    {
        // The first paint is not an event. Opening the section is not four things happening at once, and
        // a wall that flashes every card the moment you arrive has spent the flash before anything moved.
        var first = Slots.Count == 0;
        var existing = Slots.ToDictionary(s => Key(s.Card), StringComparer.Ordinal);
        var next = new List<SlotCardViewModel>(cards.Count);
        foreach (var card in cards)
        {
            if (existing.TryGetValue(Key(card), out var found))
            {
                found.Apply(card, now, IsCalm);
                next.Add(found);
            }
            else
            {
                next.Add(new SlotCardViewModel(card, now, IsCalm || first));
            }
        }

        Reconcile.Apply(Slots, next, s => Key(s.Card));
        for (var i = 0; i < Slots.Count; i++)
        {
            Slots[i].Advance(now);
        }
    }

    /// <summary>A busy card is its item; a free one is its position, since one empty slot is any other.</summary>
    private static string Key(SlotCard card) => card.IsFree
        ? FormattableString.Invariant($"free:{card.Index}")
        : card.ItemId;

    private void ApplyNumbers(IReadOnlyList<BigNumber> numbers, DateTimeOffset now)
    {
        var existing = Numbers.ToDictionary(n => n.Key, StringComparer.Ordinal);
        var next = new List<BigNumberViewModel>(numbers.Count);
        foreach (var number in numbers)
        {
            if (existing.TryGetValue(number.Key, out var found))
            {
                found.Apply(number, now, IsCalm);
                next.Add(found);
            }
            else
            {
                next.Add(new BigNumberViewModel(number, IsCalm));
            }
        }

        Reconcile.Apply(Numbers, next, n => n.Key);
    }

    private void ApplyGauges(Overview? overview)
    {
        var burns = (overview?.QuotaCards ?? [])
            .Select(c => c.Primary)
            .Where(b => b.NowPct is not null)
            .ToList();

        var existing = Gauges.ToDictionary(g => g.Label, StringComparer.Ordinal);
        var next = new List<GaugeViewModel>(burns.Count);
        foreach (var burn in burns)
        {
            if (existing.TryGetValue(burn.Label, out var found))
            {
                found.Apply(burn, IsCalm);
                next.Add(found);
            }
            else
            {
                next.Add(new GaugeViewModel(burn, IsCalm));
            }
        }

        Reconcile.Apply(Gauges, next, g => g.Label);
        OnPropertyChanged(nameof(HasGauges));
    }

    // ---- the one request the wall makes on its own ------------------------------------------------

    /// <summary>
    /// Puts an item's latest output on the wall without going near the network.
    /// </summary>
    /// <remarks>
    /// Internal: in production the tail loop below is the only caller. A render test needs the same
    /// effect at a known moment rather than whenever a background task happens to land, and a screen
    /// whose output can only arrive asynchronously cannot be photographed twice and compared.
    /// </remarks>
    internal void SetOutput(string itemId, string tail)
        => _output[itemId] = string.Join('\n', NowWorkingModel.Lines(tail, NowWorkingModel.OutputLines));

    private void StartTails()
    {
        if (_tail is null || _tails is not null)
        {
            return;
        }

        _tails = new CancellationTokenSource();
        var token = _tails.Token;
        _ = Task.Run(() => TailLoopAsync(token), token);
    }

    /// <summary>
    /// Follows the running items' output.
    /// </summary>
    /// <remarks>
    /// Polled, deliberately, where the rest of this plugin is pushed. The orchestrator's stdout hub
    /// scopes a connection to <em>one</em> work item at a time — following a second unsubscribes the
    /// first — and that one subscription already belongs to whichever item the operator has selected in
    /// the queue. Taking it would break the pane that is actually being read. The tail endpoint is a
    /// plain GET, there are only ever a couple of running items, and four seconds apart is nothing
    /// beside what those agents are doing.
    /// </remarks>
    private async Task TailLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var ids = Slots.Where(s => s.Card.IsBusy).Select(s => s.Card.ItemId).ToList();
                var changed = false;
                foreach (var id in ids)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    var tail = await _tail!(id, token).ConfigureAwait(false);
                    var lines = string.Join('\n', NowWorkingModel.Lines(tail, NowWorkingModel.OutputLines));
                    if (!_output.TryGetValue(id, out var was) || was != lines)
                    {
                        _output[id] = lines;
                        changed = true;
                    }
                }

                if (changed)
                {
                    await _toUi(() => Rebuild()).ConfigureAwait(false);
                }

                await Task.Delay(TailInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Diagnostic.Report("now-working-tail", ex);
                try
                {
                    await Task.Delay(TailInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>
/// One slot card, with the state that cannot live on an immutable record: how long it has been running,
/// and where its flash and its arrival have got to.
/// </summary>
public sealed partial class SlotCardViewModel : ObservableObject
{
    private DateTimeOffset _flashedAt;
    private DateTimeOffset _enteredAt;

    public SlotCardViewModel(SlotCard card, DateTimeOffset now, bool calm)
    {
        Card = card;
        _enteredAt = now;
        _flashedAt = card.IsBusy ? now : DateTimeOffset.MinValue;
        Elapsed = NowWorkingModel.Elapsed(now - card.Since);
        if (calm)
        {
            Settle();
        }
    }

    [ObservableProperty]
    private SlotCard _card;

    /// <summary>The second hand. The one thing on this screen that moves without anything having
    /// happened — because an agent that has been working for nine minutes is a different fact from one
    /// that started nine minutes ago and stopped.</summary>
    [ObservableProperty]
    private string _elapsed = string.Empty;

    /// <summary>1 just after something happened to this slot, 0 once the flash has decayed.</summary>
    [ObservableProperty]
    private double _glow;

    /// <summary>How far the card still has to slide, as a margin that leaves the layout height alone —
    /// a top margin cancelled by an equal negative bottom one.</summary>
    [ObservableProperty]
    private Thickness _enter;

    [ObservableProperty]
    private double _appear = 1;

    public string Phase => Card.Phase;

    public bool IsBusy => Card.IsBusy;

    public bool IsFree => Card.IsFree;

    public bool IsWorking => Card.Tone == WallTone.Working;

    public bool IsAttention => Card.Tone == WallTone.Attention;

    public bool IsFailed => Card.Tone == WallTone.Failed;

    public bool IsLanded => Card.Tone == WallTone.Landed;

    /// <summary>
    /// Re-points the card at a newer reading of the same slot, flashing when the item's phase changed.
    /// </summary>
    public void Apply(SlotCard card, DateTimeOffset now, bool calm)
    {
        var moved = card.State != Card.State || card.ItemId != Card.ItemId;
        Card = card;
        if (moved && !calm)
        {
            _flashedAt = now;
        }

        foreach (var name in new[] { nameof(Phase), nameof(IsBusy), nameof(IsFree), nameof(IsWorking),
                                     nameof(IsAttention), nameof(IsFailed), nameof(IsLanded) })
        {
            OnPropertyChanged(name);
        }

        if (calm)
        {
            Settle();
        }
    }

    /// <summary>The second-hand update. Runs whether or not the decoration does.</summary>
    public void Advance(DateTimeOffset now)
        => Elapsed = Card.IsBusy ? NowWorkingModel.Elapsed(now - Card.Since) : string.Empty;

    /// <summary>The decorative update: the flash decaying and the arrival sliding home.</summary>
    public void Frame(DateTimeOffset now)
    {
        Glow = NowWorkingModel.Decay(now - _flashedAt, NowWorkingViewModel.FlashLife);
        var entering = NowWorkingModel.Decay(now - _enteredAt, NowWorkingViewModel.SlideLife);
        Enter = new Thickness(0, entering * 14, 0, entering * -14);
        Appear = 1 - (entering * 0.75);
    }

    public void Settle()
    {
        Glow = 0;
        Enter = default;
        Appear = 1;
        _flashedAt = DateTimeOffset.MinValue;
        _enteredAt = DateTimeOffset.MinValue;
    }
}

/// <summary>One line of the log, with its arrival.</summary>
public sealed partial class TickerRowViewModel : ObservableObject
{
    private DateTimeOffset _at;

    public TickerRowViewModel(TickerLine line, DateTimeOffset now, bool calm)
    {
        Line = line;
        _at = now;
        if (calm)
        {
            Settle();
        }
    }

    public TickerLine Line { get; }

    public string Time => Line.Time;

    public string Subject => Line.Subject;

    public string Verb => Line.Verb;

    public bool HasSubject => Line.HasSubject;

    public bool IsWorking => Line.Tone == WallTone.Working;

    public bool IsLanded => Line.Tone == WallTone.Landed;

    public bool IsAttention => Line.Tone == WallTone.Attention;

    public bool IsFailed => Line.Tone == WallTone.Failed;

    /// <summary>Brightens the newest line for a moment, then lets it join the log.</summary>
    [ObservableProperty]
    private double _glow = 1;

    /// <summary>
    /// How far the row still has to slide in from the right, as a left margin.
    /// </summary>
    /// <remarks>
    /// A margin rather than a transform, because a binding inside a <c>RenderTransform</c> does not
    /// inherit the row's data context; and a POSITIVE left margin rather than one cancelled by a negative
    /// right, because the cancelled version pushes the row's right-hand column past the edge of the panel
    /// and the verb — the part that says what happened — is the part that gets clipped. Squeezing the
    /// title, which already trims, costs nothing.
    /// </remarks>
    [ObservableProperty]
    private Thickness _enter = new(28, 0, 0, 0);

    public void Frame(DateTimeOffset now)
    {
        Glow = NowWorkingModel.Decay(now - _at, NowWorkingViewModel.FlashLife);
        var sliding = NowWorkingModel.Decay(now - _at, NowWorkingViewModel.SlideLife);
        Enter = new Thickness(sliding * 28, 0, 0, 0);
    }

    public void Settle()
    {
        Glow = 0;
        Enter = default;
        _at = DateTimeOffset.MinValue;
    }
}

/// <summary>One headline figure, and the count-up it is part-way through.</summary>
public sealed partial class BigNumberViewModel : ObservableObject
{
    private DateTimeOffset _startedAt = DateTimeOffset.MinValue;
    private double _from;

    public BigNumberViewModel(BigNumber number, bool calm)
    {
        Number = number;
        _from = number.Value;
        Display = number.Value;
        if (calm)
        {
            Settle();
        }
    }

    [ObservableProperty]
    private BigNumber _number;

    /// <summary>Where the count-up has got to. Equal to the target at rest.</summary>
    [ObservableProperty]
    private double _display;

    public string Key => Number.Key;

    public string Label => Number.Label;

    public string Caption => Number.Caption;

    public bool HasCaption => Number.HasCaption;

    public bool HasSpark => Number.HasSpark;

    public IReadOnlyList<double> Spark => Number.Spark;

    public TileTone Tone => Number.Tone;

    public bool IsActive => Number.Tone == TileTone.Active;

    public bool IsAttention => Number.Tone == TileTone.Attention;

    public bool IsBad => Number.Tone == TileTone.Bad;

    /// <summary>The figure as it is written right now — mid-count when it is counting.</summary>
    public string Text => NowWorkingModel.Format(Display, Number.Format, Number.Of);

    partial void OnDisplayChanged(double value) => OnPropertyChanged(nameof(Text));

    /// <summary>Points the tile at a new reading, starting a count-up if the figure actually moved.</summary>
    public void Apply(BigNumber number, DateTimeOffset now, bool calm)
    {
        var moved = Math.Abs(number.Value - Number.Value) > 0.0001;
        var wasDisplay = Display;
        Number = number;
        foreach (var name in new[] { nameof(Label), nameof(Caption), nameof(HasCaption), nameof(HasSpark),
                                     nameof(Spark), nameof(Tone), nameof(IsActive), nameof(IsAttention),
                                     nameof(IsBad), nameof(Text) })
        {
            OnPropertyChanged(name);
        }

        if (calm || !moved)
        {
            Display = number.Value;
            _from = number.Value;
            _startedAt = DateTimeOffset.MinValue;
            return;
        }

        _from = wasDisplay;
        _startedAt = now;
    }

    public void Frame(DateTimeOffset now)
    {
        if (_startedAt == DateTimeOffset.MinValue)
        {
            return;
        }

        Display = NowWorkingModel.CountUp(_from, Number.Value, now - _startedAt, NowWorkingViewModel.CountLife);
        if (Math.Abs(Display - Number.Value) < 0.0001)
        {
            Display = Number.Value;
            _startedAt = DateTimeOffset.MinValue;
        }
    }

    public void Settle()
    {
        Display = Number.Value;
        _from = Number.Value;
        _startedAt = DateTimeOffset.MinValue;
    }
}

/// <summary>One agent's quota gauge, breathing while it is actually being spent.</summary>
public sealed partial class GaugeViewModel : ObservableObject
{
    public GaugeViewModel(QuotaBurn burn, bool calm)
    {
        Burn = burn;
        if (calm)
        {
            Settle();
        }
    }

    [ObservableProperty]
    private QuotaBurn _burn;

    /// <summary>Opacity, between 0.7 and 0.95. Never 1, which is the value a gauge that is NOT burning
    /// sits at — so "breathing" and "still" are told apart by the number and not only by the eye.</summary>
    [ObservableProperty]
    private double _pulse = 1;

    public string Label => Burn.Label;

    public string Agent => Burn.Agent;

    public string Window => Burn.WindowShort;

    public double? Pct => Burn.NowPct;

    public bool Eligible => Burn.Eligible;

    public string Headroom => Burn.NowPct is { } pct
        ? FormattableString.Invariant($"{pct:0}%")
        : "—";

    public void Apply(QuotaBurn burn, bool calm)
    {
        Burn = burn;
        foreach (var name in new[] { nameof(Label), nameof(Agent), nameof(Window), nameof(Pct),
                                     nameof(Eligible), nameof(Headroom) })
        {
            OnPropertyChanged(name);
        }

        if (calm)
        {
            Settle();
        }
    }

    /// <summary>A two-second breath, in phase across every burning gauge so the row reads as one fleet
    /// rather than as several blinking lights.</summary>
    public void Frame(DateTimeOffset now)
    {
        if (!Burn.IsBurning)
        {
            Pulse = 1;
            return;
        }

        var phase = now.ToUnixTimeMilliseconds() % 2000 / 2000.0;
        Pulse = 0.825 + (0.125 * Math.Cos(phase * 2 * Math.PI));
    }

    public void Settle() => Pulse = 1;
}
