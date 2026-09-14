using System.Collections.ObjectModel;
using Agnes.App.Mobile.Services;
using Agnes.Plugins.CodeyBox;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>The three things a phone asks of a fleet, in the order it asks them.</summary>
public enum CodeyBoxSegment
{
    /// <summary>Should I do anything? The sentence, the vitals, what needs a look.</summary>
    Overview,

    /// <summary>What is it doing this second? One card per busy slot.</summary>
    NowWorking,

    /// <summary>What is in the pipeline, in the order it will be picked.</summary>
    Queue,
}

/// <summary>
/// CodeyBox on a phone: the fleet's own destination, built over the plugin's pure models.
/// </summary>
/// <remarks>
/// <para><b>Not a plugin.</b> The Android head has no plugin loader and is not growing one for this. What
/// it takes from <c>Agnes.Plugins.CodeyBox</c> is the part that is not a desktop pane at all — the client,
/// the gather, <see cref="OverviewModel"/>, <see cref="BoardModel"/>, <see cref="Decision"/> and
/// <see cref="NowWorkingViewModel"/>, none of which know what a window is. The plugin's own
/// <c>Views/*.axaml</c> are deliberately not reused: they are laid out for a 660–1200 px pane and they ask
/// for the desktop head's role names. The screens here are this head's own, in this head's vocabulary.</para>
///
/// <para><b>It costs nothing until it is configured.</b> No client is built, no request is made and the
/// tab does not appear until More › CodeyBox has both an address and a key. Almost nobody running Agnes
/// runs CodeyBox, and for them the app must be exactly the four-destination app it was.</para>
///
/// <para><b>It only runs while it is on screen.</b> The change feed and the wall's timers start in
/// <see cref="OnShown"/> and stop in <see cref="OnHidden"/>. A phone in a pocket holding an SSE connection
/// open and re-gathering forty items' audit history every fifteen seconds is a battery complaint, and
/// nothing on the screen is being read. The one exception is <see cref="RefreshNeedsYouAsync"/>, which the
/// Inbox calls: two cheap reads, because "something in the fleet is waiting on you" is the fact the phone
/// exists to carry and it must not depend on the tab being open.</para>
/// </remarks>
public sealed partial class CodeyBoxViewModel : ObservableObject, IDisposable
{
    private readonly IAppShell _shell;
    private readonly Func<IWallClock?> _clock;

    /// <summary>
    /// How a client is built for a configured orchestrator.
    /// </summary>
    /// <remarks>
    /// A seam, not an abstraction for its own sake: the headless preview harness hands in one over a
    /// canned message handler, and the action tests hand in one that records the exact HTTP calls a
    /// choice makes. Returning null is allowed and means "there is no orchestrator behind this" — which
    /// is what lets a screen be photographed from canned models with nothing at the other end.
    /// </remarks>
    private readonly Func<CodeyBoxOptions, CodeyBoxClient?> _clientFactory;

    private CodeyBoxClient? _client;
    private OverviewGather? _gather;
    private CancellationTokenSource? _live;

    /// <summary>The work-item list and projects the last gather read, so the wall and the runway are
    /// built from the same facts rather than asking for them again.</summary>
    private IReadOnlyList<WorkItemRow> _items = [];
    private IReadOnlyList<Project> _projects = [];
    private Board? _board;

    /// <summary>Something in the fleet moved and the gather has not caught up with it yet.</summary>
    private volatile bool _dirty;
    private DateTimeOffset _gatheredAt;

    /// <summary>How long a burst of feed events is allowed to accumulate before the screen re-gathers.
    /// One transition emits several events and the feed replays its buffer on connect, so acting on each
    /// one would be dozens of requests to describe one change.</summary>
    private static readonly TimeSpan Coalesce = TimeSpan.FromSeconds(4);

    /// <summary>The floor: a fleet that emits nothing still has elapsed timers, a drain estimate and a
    /// quota burn-down that go stale, so the screen re-reads on its own this often.</summary>
    private static readonly TimeSpan IdleRefresh = TimeSpan.FromSeconds(60);

    public CodeyBoxViewModel(
        IAppShell shell,
        Func<IWallClock?>? clock = null,
        Func<CodeyBoxOptions, CodeyBoxClient?>? clientFactory = null)
    {
        _shell = shell;
        _clock = clock ?? (() => null);
        _clientFactory = clientFactory ?? (options => new CodeyBoxClient(options));

        NowWorking = new NowWorkingViewModel(
            () => _board,
            () => Overview,
            () => _items,
            ToUi,
            (id, token) => _client?.GetStdoutTailAsync(id, token) ?? Task.FromResult(string.Empty),
            clock: _clock())
        {
            // Calm is ON by default on a phone, where the desktop wall has it off. Calm keeps every live
            // update and removes every flash, pulse and count-up — which on a 33 ms frame timer is the
            // difference between a screen you can leave open and one that warms the phone in your hand.
            // It is still a switch: the header offers it, because a phone propped up beside a desk is the
            // one place the wall's decoration earns its keep.
            IsCalm = true,
        };

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        ShowOverviewCommand = new RelayCommand(() => Segment = CodeyBoxSegment.Overview);
        ShowNowWorkingCommand = new RelayCommand(() => Segment = CodeyBoxSegment.NowWorking);
        ShowQueueCommand = new RelayCommand(() => Segment = CodeyBoxSegment.Queue);
        OpenTraceCommand = new RelayCommand<CodeyBoxTraceRow>(row => OpenItem(row?.Item));
        OpenChainCommand = new RelayCommand<CodeyBoxChainRow>(row => OpenItem(row?.Item));
        OpenNeedsCommand = new RelayCommand<CodeyBoxNeedsRow>(row => OpenItem(row?.Item));
        OpenSetupCommand = new RelayCommand(() => _shell.Push(new CodeyBoxSetupPageViewModel(_shell, this)));

        Apply(CodeyBoxConfig.Load());
    }

    // ---- configuration ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigured))]
    private CodeyBoxConfig _config = new();

    /// <summary>Whether CodeyBox exists for this device at all. Everything about it — the tab, the inbox
    /// rows, every request — is behind this one answer.</summary>
    public bool IsConfigured => Config.IsConfigured;

    /// <summary>Raised when CodeyBox is configured or forgotten, so the shell can show or hide the tab.</summary>
    public event Action? ConfigurationChanged;

    /// <summary>
    /// Points the screens at a (possibly different, possibly absent) orchestrator.
    /// </summary>
    /// <remarks>
    /// The old client is disposed, not reused: it holds a base address, a bearer header and a hub
    /// connection, so re-pointing one would leave a phone talking to the previous host with the previous
    /// key. The caches go with it for the same reason — a trace keyed on an item id means nothing once
    /// the fleet behind it has changed.
    /// </remarks>
    public void Apply(CodeyBoxConfig config)
    {
        Stop();
        _client?.DisposeAsync().AsTask().ContinueWith(
            t => _ = t.Exception, TaskScheduler.Default);
        _client = null;
        _gather = null;
        _items = [];
        _projects = [];
        _board = null;
        Overview = null;
        Sections.Clear();
        NeedsYou.Clear();
        NeedsYouChanged?.Invoke();

        Config = config;
        if (config.IsConfigured)
        {
            _client = _clientFactory(config.ToOptions());
            _gather = _client is null ? null : new OverviewGather(_client, new OverviewHistory(CodeyBoxConfig.HistoryPath));
        }

        ConfigurationChanged?.Invoke();
    }

    /// <summary>Saves a new address and key and re-points at them. One call, so the file and the live
    /// client can never disagree.</summary>
    public void Save(CodeyBoxConfig config)
    {
        config.Save();
        Apply(config);
    }

    // ---- segments ---------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsNowWorking), nameof(IsQueue))]
    private CodeyBoxSegment _segment = CodeyBoxSegment.Overview;

    public bool IsOverview => Segment == CodeyBoxSegment.Overview;
    public bool IsNowWorking => Segment == CodeyBoxSegment.NowWorking;
    public bool IsQueue => Segment == CodeyBoxSegment.Queue;

    public IRelayCommand ShowOverviewCommand { get; }
    public IRelayCommand ShowNowWorkingCommand { get; }
    public IRelayCommand ShowQueueCommand { get; }

    partial void OnSegmentChanged(CodeyBoxSegment value)
    {
        // The wall's timers follow the segment, not the tab: it is the only one that ticks every second,
        // and a screen you are not looking at must not.
        if (_running)
        {
            if (value == CodeyBoxSegment.NowWorking)
            {
                NowWorking.Start();
            }
            else
            {
                NowWorking.Stop();
            }
        }

        _shell.Haptics.Tick();
    }

    // ---- what is on screen ------------------------------------------------------------------------

    /// <summary>The overview as the pure model built it; null until the first gather lands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverview))]
    private Overview? _overview;

    public bool HasOverview => Overview is not null;

    // Flattened rather than bound through the nullable record. `{Binding Overview.Sentence}` resolves to
    // nothing while there is no overview, and Avalonia logs a binding error for every such path on every
    // refresh — the same reason the shell flattens its toast.
    public string Sentence => Overview?.Sentence ?? "Reading the fleet…";

    public string AsOf => Overview?.AsOf ?? string.Empty;

    /// <summary>The tone the sentence wears: the one-word verdict the overview expands.</summary>
    public bool VerdictIsActive => Overview?.Verdict == TileTone.Active;
    public bool VerdictIsAttention => Overview?.Verdict == TileTone.Attention;
    public bool VerdictIsBad => Overview?.Verdict == TileTone.Bad;

    public FlowSeries? Flow => Overview?.Flow;

    public bool HasFlow => Flow is { HasData: true };

    public string HealthyLabel => Overview?.HealthyLabel ?? string.Empty;

    public bool HasHealthy => Overview is { HasHealthy: true };

    partial void OnOverviewChanged(Overview? value)
    {
        foreach (var name in new[]
                 {
                     nameof(Sentence), nameof(AsOf), nameof(Flow), nameof(HasFlow),
                     nameof(VerdictIsActive), nameof(VerdictIsAttention), nameof(VerdictIsBad),
                     nameof(HealthyLabel), nameof(HasHealthy),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Live items needing a look, most urgent first.</summary>
    public ObservableCollection<CodeyBoxTraceRow> Attention { get; } = [];

    /// <summary>Items stopped at the same phase boundary, one line per boundary: they need a slot, not
    /// a look, so they are a count rather than twenty rows saying the same thing.</summary>
    public ObservableCollection<AttentionGroup> Folded { get; } = [];

    public ObservableCollection<Vital> Vitals { get; } = [];

    public ObservableCollection<QuotaCard> QuotaCards { get; } = [];

    /// <summary>Now, Next, one per waiting group, one per landed day.</summary>
    public ObservableCollection<CodeyBoxQueueSection> Sections { get; } = [];

    /// <summary>The wall. Shares the board and the overview this tab already holds.</summary>
    public NowWorkingViewModel NowWorking { get; }

    public bool HasAttention => Attention.Count > 0;
    public bool HasFolded => Folded.Count > 0;
    public bool HasSections => Sections.Count > 0;

    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>What the screen says when it cannot say anything else: the first read's failure, or
    /// nothing at all once one has succeeded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    public bool HasStatus => Status.Length > 0;

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand<CodeyBoxTraceRow> OpenTraceCommand { get; }
    public IRelayCommand<CodeyBoxChainRow> OpenChainCommand { get; }
    public IRelayCommand<CodeyBoxNeedsRow> OpenNeedsCommand { get; }
    public IRelayCommand OpenSetupCommand { get; }

    private void OpenItem(WorkItemRow? item)
    {
        if (item is null || _client is null)
        {
            return;
        }

        _shell.Push(new CodeyBoxItemPageViewModel(_shell, this, _client, item));
    }

    /// <summary>The item as the last gather saw it, or null once it has left the list. The item page
    /// re-reads itself through this after an action rather than asking the orchestrator for one row —
    /// the action already cost a gather, and two readings of one item is how they start disagreeing.</summary>
    internal WorkItemRow? Find(string id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>The live client, or null while nothing is configured. The Inbox answers a fleet question
    /// through this rather than building a second client with a second copy of the key.</summary>
    internal CodeyBoxClient? Client => _client;

    /// <summary>The audit-iteration budget an item was measured against, from its project. The work item
    /// does not carry one on this deployment, which is why the decision card takes it as an argument.</summary>
    internal int CeilingOf(WorkItemRow item)
        => item.ProjectId is { Length: > 0 } id
            ? _projects.FirstOrDefault(p => p.Id == id)?.AuditMaxIterations ?? 0
            : 0;

    /// <summary>The branch a merge would have gone into. Same source, same reason.</summary>
    internal string? BaseBranchOf(WorkItemRow item)
        => item.ProjectId is { Length: > 0 } id
            ? _projects.FirstOrDefault(p => p.Id == id)?.DefaultBaseBranch
            : null;

    // ---- the inbox's cheap read -------------------------------------------------------------------

    /// <summary>Fleet items waiting on a person, for the Inbox.</summary>
    public ObservableCollection<CodeyBoxNeedsRow> NeedsYou { get; } = [];

    public event Action? NeedsYouChanged;

    /// <summary>
    /// Re-reads only what the Inbox needs: the work-item list, and the questions of the items that say
    /// they are waiting on one.
    /// </summary>
    /// <remarks>
    /// Deliberately not a gather. The Inbox refreshes when the tab is opened and whenever a session's
    /// attention state moves, and it must work with the CodeyBox screen closed — so it costs one list
    /// read plus one read per parked item, not forty audit histories. A failure is silent: an
    /// unreachable orchestrator contributes no rows, exactly as an unreachable Agnes host does.
    /// </remarks>
    public async Task RefreshNeedsYouAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { } client)
        {
            return;
        }

        List<CodeyBoxNeedsRow> rows = [];
        try
        {
            var items = await client.ListWorkItemsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in items.Where(i => i.State == "NeedsOperatorInput"))
            {
                var questions = await client.GetQuestionsAsync(item.Id, cancellationToken).ConfigureAwait(false);
                rows.AddRange(questions.Where(q => q.IsOpen).Select(q => new CodeyBoxNeedsRow(item, q)));
            }

            // A failed item is not asking a question; it is asking for a decision, which needs the card.
            rows.AddRange(items.Where(i => i.IsFailed).Select(i => new CodeyBoxNeedsRow(i, null)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostic.Report("codeybox inbox", ex);
            return;
        }

        await ToUi(() =>
        {
            NeedsYou.Clear();
            foreach (var row in rows)
            {
                NeedsYou.Add(row);
            }

            NeedsYouChanged?.Invoke();
        }).ConfigureAwait(false);
    }

    // ---- lifecycle --------------------------------------------------------------------------------

    private bool _running;

    /// <summary>Whether the feed and the timers are running. Public for the test that proves leaving the
    /// tab switches them off.</summary>
    public bool IsLive => _running;

    /// <summary>The tab became visible: gather once, follow the feed, and start the wall if that is the
    /// segment showing.</summary>
    public void OnShown()
    {
        if (_running || _client is null)
        {
            return;
        }

        _running = true;
        _live = new CancellationTokenSource();
        var token = _live.Token;
        _ = Task.Run(() => LiveLoopAsync(token), token);
        _ = Task.Run(() => FollowAsync(token), token);
        if (Segment == CodeyBoxSegment.NowWorking)
        {
            NowWorking.Start();
        }
    }

    /// <summary>The tab was left. Everything stops — there is nothing on screen that a live connection
    /// would be keeping current.</summary>
    public void OnHidden() => Stop();

    private void Stop()
    {
        _running = false;
        NowWorking.Stop();
        _live?.Cancel();
        _live?.Dispose();
        _live = null;
    }

    public void Dispose()
    {
        Stop();
        NowWorking.Dispose();
        _client?.DisposeAsync().AsTask().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        _client = null;
    }

    /// <summary>
    /// Gathers on arrival, then whenever the feed says something moved — coalesced — and on a slow floor
    /// so the clock-driven figures do not rot on an idle fleet.
    /// </summary>
    private async Task LiveLoopAsync(CancellationToken token)
    {
        await RefreshAsync(token).ConfigureAwait(false);

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Coalesce, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_dirty || DateTimeOffset.UtcNow - _gatheredAt >= IdleRefresh)
            {
                _dirty = false;
                await RefreshAsync(token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Follows the orchestrator's change feed.
    /// </summary>
    /// <remarks>
    /// Every event reaches the wall — its log names what moved and its heartbeat counts how often, and the
    /// phase-level events are most of what a busy fleet emits. The expensive gather is only marked dirty,
    /// because on a phone the difference between "the log moved instantly" and "the numbers moved four
    /// seconds later" is not worth forty requests per transition.
    /// </remarks>
    private async Task FollowAsync(CancellationToken token)
    {
        if (_client is not { } client)
        {
            return;
        }

        try
        {
            var since = DateTimeOffset.UtcNow;
            await client.CreateEventStream().RunAsync(
                since,
                evt =>
                {
                    NowWorking.Note(evt);
                    _dirty = true;
                    return Task.CompletedTask;
                },
                reconnected =>
                {
                    // A reconnect may have missed more than the buffer holds — which on a phone is the
                    // ordinary case, since the radio drops whenever the screen does.
                    if (reconnected)
                    {
                        _dirty = true;
                    }

                    return Task.CompletedTask;
                },
                token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A feed that is down costs the screen its liveness, not its content: the loop above still
            // re-gathers on its floor.
            Diagnostic.Report("codeybox feed", ex);
        }
    }

    /// <summary>One gather, applied to every segment.</summary>
    public async Task RefreshAsync(CancellationToken token = default)
    {
        if (_gather is not { } gather)
        {
            return;
        }

        await ToUi(() => IsRefreshing = true).ConfigureAwait(false);
        try
        {
            var gathered = await gather.GatherAsync(token).ConfigureAwait(false);

            // Off the UI thread on purpose: BoardModel.Build walks the whole queue, groups it into chains
            // and topologically sorts each one. Four hundred items of graph work on a phone's UI thread is
            // a visible stall on every refresh.
            var board = Runway(gathered);
            var sections = BuildSections(board);

            _gatheredAt = DateTimeOffset.UtcNow;

            await ToUi(() => Apply(gathered, board, sections)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostic.Report("codeybox gather", ex);
            await ToUi(() => Status = Explain(ex)).ConfigureAwait(false);
        }
        finally
        {
            await ToUi(() => IsRefreshing = false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What went wrong, in the words a person on a phone can act on.
    /// </summary>
    /// <remarks>
    /// The two failures that actually happen are "wrong address" (the LAN address changed, or the phone is
    /// on mobile data) and "wrong key". A raw <c>HttpRequestException</c> says neither.
    /// </remarks>
    internal static string Explain(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } =>
            "CodeyBox refused the API key. Check it in More › CodeyBox.",
        HttpRequestException or TaskCanceledException =>
            "Can't reach CodeyBox at that address. It's on your LAN, so this phone has to be on the same network.",
        _ => ex.Message,
    };

    /// <summary>The runway the queue segment draws, from a gather. Pure, and off the UI thread in the
    /// live path.</summary>
    internal static Board Runway(GatheredOverview gathered)
    {
        ArgumentNullException.ThrowIfNull(gathered);
        var busy = gathered.Concurrency?.CurrentlyRunningTotal ?? 0;
        var total = gathered.Concurrency?.GlobalMaxConcurrent ?? 0;
        return BoardModel.Build(gathered.Items, gathered.Projects, busy, total, DateTimeOffset.Now);
    }

    /// <summary>
    /// Shows a gather that has already happened.
    /// </summary>
    /// <remarks>
    /// Public because these screens are a <em>function of a gather</em> and nothing else, and that is what
    /// makes them photographable: the headless preview harness and the render tests hand in a canned
    /// <see cref="GatheredOverview"/> and get the real screens with no orchestrator behind them. The live
    /// path calls the same code with the real one.
    /// </remarks>
    public void Show(GatheredOverview gathered)
    {
        var board = Runway(gathered);
        Apply(gathered, board, BuildSections(board));
    }

    private void Apply(GatheredOverview gathered, Board board, IReadOnlyList<CodeyBoxQueueSection> sections)
    {
        _items = gathered.Items;
        _projects = gathered.Projects;
        _board = board;
        Overview = gathered.Overview;
        ApplyOverview(gathered.Overview);
        Reconcile.Apply(Sections, sections, s => s.Header);
        OnPropertyChanged(nameof(HasSections));
        Status = string.Empty;
        NowWorking.Rebuild();
    }

    private void ApplyOverview(Overview overview)
    {
        Reconcile.Apply(Vitals, overview.Vitals, v => v.Label);
        Reconcile.Apply(Attention, [.. overview.Attention.Select(t => new CodeyBoxTraceRow(t))], r => r.Item.Id);
        Reconcile.Apply(Folded, overview.Folded, g => g.Title);
        Reconcile.Apply(QuotaCards, overview.QuotaCards, c => c.Agent);
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(HasFolded));
    }

    /// <summary>
    /// The runway as sections a phone scrolls through: Now, Next, one per waiting group, one per landed
    /// day. Pure — it is a function of the board, which is what lets a test assert the shape without a
    /// network.
    /// </summary>
    internal static IReadOnlyList<CodeyBoxQueueSection> BuildSections(Board board)
    {
        ArgumentNullException.ThrowIfNull(board);

        var sections = new List<CodeyBoxQueueSection>();
        if (board.HasNow)
        {
            sections.Add(new CodeyBoxQueueSection(board.NowHeader, board.NowSharedWhy, board.Now));
        }

        if (board.HasNext)
        {
            sections.Add(new CodeyBoxQueueSection(board.NextHeader, board.NextSharedWhy, board.Next));
        }

        foreach (var group in board.Waiting)
        {
            sections.Add(new CodeyBoxQueueSection(group.Header, group.SharedWhy, group.Chains));
        }

        foreach (var day in board.Landed)
        {
            sections.Add(new CodeyBoxQueueSection(day.Header, day.SharedWhy, day.Chains));
        }

        return sections;
    }

    /// <summary>
    /// The plugin's <c>toUi</c> seam, over this head's dispatcher.
    /// </summary>
    /// <remarks>
    /// The plugin asks for a <c>Func&lt;Action, Task&gt;</c> and awaits it, because a caller that goes on
    /// to touch what it just posted must know the post has happened; Avalonia's own dispatcher post is
    /// fire-and-forget. This is the adapter between the two.
    /// </remarks>
    private Task ToUi(Action action)
    {
        var done = new TaskCompletionSource();
        _shell.Dispatcher.Post(() =>
        {
            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }
}
