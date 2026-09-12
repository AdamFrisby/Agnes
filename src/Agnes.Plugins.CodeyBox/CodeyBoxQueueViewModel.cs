using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// The CodeyBox work queue, and the live agent output of whichever item is selected.
/// </summary>
/// <remarks>
/// Two halves, matching the two things CodeyBox offers over the wire: the queue is read over REST and
/// kept current from the <c>/workitems/events</c> SSE feed, while the selected item's agent output is
/// streamed from the <c>agent-stdout</c> hub. Neither is polled.
///
/// <para>Selecting an item pulls its stdout <i>tail</i> first and then follows the live stream. Neither
/// alone is enough: a subscription only carries what happens next, so without the tail an item that has
/// been running for an hour opens blank.</para>
/// </remarks>
public sealed partial class CodeyBoxQueueViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CodeyBoxClient _client;
    private readonly Func<Action, Task> _toUi;
    private readonly StringBuilder _output = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Item ids the feed says have changed, coalesced before being read back — one transition
    /// commonly emits several events.</summary>
    private readonly Channel<CodeyBoxEvent> _pending =
        Channel.CreateUnbounded<CodeyBoxEvent>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>How long to keep collecting events before reading the queue back. Long enough to fold a
    /// burst into one read, short enough that a person does not perceive the delay.</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(250);

    private Task? _poller;
    private Task? _drainer;

    public CodeyBoxQueueViewModel(CodeyBoxClient client, Func<Action, Task> toUi, bool configured = true)
    {
        _client = client;
        _toUi = toUi;
        IsConfigured = configured;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        TogglePauseCommand = new AsyncRelayCommand(TogglePauseAsync);
        // Irreversible: armed here, executed only after the operator confirms against the named item.
        CancelCommand = new AsyncRelayCommand<WorkItemRow>(row => Confirm("Cancel", row, _client.CancelAsync));
        RetryCommand = new AsyncRelayCommand<WorkItemRow>(row => Act(row, _client.RetryAsync));
        PromoteCommand = new AsyncRelayCommand<WorkItemRow>(row => Act(row, _client.PromoteAsync));
        ReplayCommand = new AsyncRelayCommand<WorkItemRow>(row => Act(row, _client.ReplayAsync));
        AbandonCommand = new AsyncRelayCommand<WorkItemRow>(row => Confirm("Abandon", row, _client.AbandonAsync));
        UncancelCommand = new AsyncRelayCommand<WorkItemRow>(row => Act(row, _client.UncancelAsync));
        ResumeItemCommand = new AsyncRelayCommand<WorkItemRow>(row => Act(row, _client.ResumeWorkItemAsync));
        RecoverCommand = new AsyncRelayCommand<WorkItemRow>(row => Act(row, _client.RecoverAsync));
        AnswerQuestionCommand = new AsyncRelayCommand<WorkItemQuestion>(AnswerAsync);
        DismissQuestionCommand = new AsyncRelayCommand<WorkItemQuestion>(DismissAsync);

        // Whoever changes the question list changes the decision — the loader, an answer landing, a
        // dismissal. Subscribing is what makes that true of every writer rather than of the two that
        // remembered to say so.
        Questions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasOpenQuestions));
            RebuildDecision();
        };

        _client.StdoutReceived += OnStdout;
        _client.StreamCompleted += OnStreamCompleted;

        Composer = new ComposerViewModel(
            client,
            toUi,
            () => _projectRecords,
            () => _all,
            () => Board?.Next ?? [],
            RefreshAsync,
            SelectById);

        // The sections are handed ways to reach the queue rather than a reference to it: the overview
        // needs to hand a row over, and promoting a suggestion needs to open the composer. The sections
        // deliberately do not know what contains them.
        // The board goes in as a function, not as a value: "Now working" draws the running half of the
        // same runway this view model builds, and handing it a snapshot would freeze it at construction.
        Sections = new CodeyBoxSectionsViewModel(
            client, toUi, Confirmation, SelectById, promote: PromoteSuggestionViaComposer,
            board: () => Board);
    }

    /// <summary>A pending irreversible action, awaiting confirmation. Shared with the sections below, so
    /// there is one place the operator learns to look before something is destroyed.</summary>
    public Confirmation Confirmation { get; } = new();

    private Task Confirm(string verb, WorkItemRow? row, Func<string, CancellationToken, Task> action)
    {
        if (row is not null)
        {
            Confirmation.Ask(verb, row.ShortId, () => Act(row, action));
        }

        return Task.CompletedTask;
    }

    /// <summary>Everything the tab shows besides the queue — fleet, supervision, suggestions, releases and
    /// the orchestrator's own diagnostics. Each loads when first opened rather than up front.</summary>
    public CodeyBoxSectionsViewModel Sections { get; }

    /// <summary>Whether an API key was found. False renders a "configure me" state rather than an error
    /// loop — a machine with no CodeyBox is an ordinary machine, not a broken one.</summary>
    public bool IsConfigured { get; }

    /// <summary>Everything the orchestrator returned. The screen shows a slice of it — see
    /// <see cref="ApplyView"/>.</summary>
    private readonly List<WorkItemRow> _all = [];

    /// <summary>
    /// The items currently on screen, after search, filter and sort. Derived — to put items <i>into</i>
    /// the queue use <see cref="Load"/>, or writing here would be overwritten by the next view change.
    /// </summary>
    public ObservableCollection<WorkItemRow> Items { get; } = [];

    /// <summary>
    /// Replaces the queue's contents and re-derives what is on screen. The one way in, so the filter
    /// always has the whole set to narrow and the counts can say what is being hidden.
    /// </summary>
    public void Load(IEnumerable<WorkItemRow> items)
    {
        _all.Clear();
        _all.AddRange(items);

        // Reconciled: clearing this would drop the open agent dropdown's selection on every update.
        Reconcile.Apply(
            Agents,
            [.. _all.Select(i => i.Agent).Where(a => a is { Length: > 0 }).Distinct().Order()!],
            a => a);

        ApplyView();
    }

    /// <summary>Projects, for the filter and for the new-item picker.</summary>
    public ObservableCollection<ProjectChoice> Projects { get; } = [];

    /// <summary>
    /// The projects as the orchestrator describes them, kept beside the pickers' <see cref="Projects"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ProjectChoice"/> is a label and an id — everything a dropdown needs and nothing the
    /// board does. The runway needs the whole record, because reordering has to respect each project's
    /// own priority ceiling and a chain has to be able to say which project it belongs to.
    /// </remarks>
    private readonly List<Project> _projectRecords = [];

    /// <summary>The highest priority each project accepts, for the reorder maths.</summary>
    private IReadOnlyDictionary<string, int> ProjectCeilings =>
        _projectRecords.GroupBy(p => p.Id, StringComparer.Ordinal)
                       .ToDictionary(g => g.Key, g => g.First().PriorityCeiling, StringComparer.Ordinal);

    /// <summary>
    /// Dispatch slots, for the Now header.
    /// </summary>
    /// <remarks>
    /// Read from whatever the sections already fetched rather than asked for again: the board rebuilds on
    /// every state transition in the fleet, and a per-rebuild request for a number that changes when a
    /// slot frees would be one extra call per event. When Diagnostics has never been opened there is
    /// nothing to reuse, so <see cref="RefreshSlotsAsync"/> fetches it at most once a minute.
    /// </remarks>
    private (int Busy, int Total) Slots => (Sections.Concurrency ?? _concurrency) is { } c
        ? (c.CurrentlyRunningTotal, c.GlobalMaxConcurrent)
        : (0, 0);

    private Concurrency? _concurrency;

    private DateTimeOffset _concurrencyAt = DateTimeOffset.MinValue;

    /// <summary>How stale the slot count is allowed to get before it is re-read. Slots move when work
    /// starts and stops, which the board already learns about from the feed; this only exists so the
    /// header has a denominator at all.</summary>
    private static readonly TimeSpan ConcurrencyWindow = TimeSpan.FromMinutes(1);

    private async Task RefreshSlotsAsync()
    {
        if (Sections.Concurrency is not null || DateTimeOffset.UtcNow - _concurrencyAt < ConcurrencyWindow)
        {
            return;
        }

        _concurrencyAt = DateTimeOffset.UtcNow;
        _concurrency = await _client.GetConcurrencyAsync(_cts.Token).ConfigureAwait(false);
    }

    /// <summary>Agents seen in the queue, for the filter. Read from the items rather than configured, so
    /// it lists what has actually run here.</summary>
    public ObservableCollection<string> Agents { get; } = [];

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private string? _projectFilter;

    [ObservableProperty]
    private string? _agentFilter;

    /// <summary>How many items need a person, regardless of the current search — the number worth knowing
    /// even while looking at something else.</summary>
    public int AttentionCount => _all.Count(i => i.NeedsAttention);

    public bool HasAttention => AttentionCount > 0;

    /// <summary>What the current slice is showing, against the whole, so a search can never silently hide
    /// the rest of the queue.</summary>
    public string ViewSummary => _all.Count == 0
        ? string.Empty
        : $"{Items.Count} of {_all.Count}" + (AttentionCount > 0 ? $"  \u00b7  {AttentionCount} need attention" : string.Empty);

    /// <summary>Selects a row from a list that uses buttons rather than a ListBox.</summary>
    public IRelayCommand<WorkItemRow> SelectCommand =>
        _select ??= new RelayCommand<WorkItemRow>(row => { if (row is not null) { Selected = row; } });

    private IRelayCommand<WorkItemRow>? _select;

    /// <summary>
    /// Selects an item by id, for a caller holding an id rather than a row — the overview, which shows
    /// rows of its own making. Clears the narrowing if the item is not in the current slice, because
    /// sending someone to a row that a narrowed board hides looks exactly like the command doing nothing.
    /// </summary>
    private void SelectById(string id)
    {
        var row = _all.FirstOrDefault(i => i.Id == id);
        if (row is null)
        {
            return;
        }

        if (!Items.Contains(row))
        {
            Search = string.Empty;
            ProjectFilter = null;
            AgentFilter = null;
        }

        Selected = row;
    }

    public IRelayCommand ClearFiltersCommand => _clearFilters ??= new RelayCommand(() =>
    {
        Search = string.Empty;
        ProjectFilter = null;
        AgentFilter = null;
    });

    private IRelayCommand? _clearFilters;

    partial void OnSearchChanged(string value) => ApplyView();
    partial void OnProjectFilterChanged(string? value) => ApplyView();
    partial void OnAgentFilterChanged(string? value) => ApplyView();

    // ---- the runway ----

    /// <summary>
    /// The four horizons: what is running, what is next in dispatch order, what cannot start, and what
    /// landed. Null until the first build, which is why <see cref="HasBoard"/> exists — a screen that
    /// renders its empty state and its not-yet-built state identically tells the operator nothing.
    /// </summary>
    [ObservableProperty]
    private Board? _board;

    public bool HasBoard => Board is not null;

    partial void OnBoardChanged(Board? value)
    {
        OnPropertyChanged(nameof(HasBoard));
        Composer.NoteBoardChanged();
    }

    /// <summary>
    /// Chains that matched the search but sit outside the board's horizons — older than the landed
    /// window, or cancelled.
    /// </summary>
    /// <remarks>
    /// History is deliberately not a horizon: 372 of 404 items on the live instance are finished, so
    /// showing them by default buries the thirty that matter. But the moment someone SEARCHES they are
    /// looking for a specific thing, and are as likely to want last month's cancelled attempt as today's
    /// work. So search — and only search — reaches into history, and what it finds is listed apart from
    /// the live horizons rather than mixed into them.
    /// </remarks>
    [ObservableProperty]
    private IReadOnlyList<Chain> _historyMatches = [];

    public bool HasHistoryMatches => HistoryMatches.Count > 0;

    partial void OnHistoryMatchesChanged(IReadOnlyList<Chain> value)
        => OnPropertyChanged(nameof(HasHistoryMatches));

    /// <summary>Where the selected item sits in its chain: its parents, its children, and the one
    /// ancestor whose retry would unblock it.</summary>
    [ObservableProperty]
    private Relations? _relations;

    public bool HasRelations => Relations is not null;

    partial void OnRelationsChanged(Relations? value) => OnPropertyChanged(nameof(HasRelations));

    /// <summary>Selecting a chain selects the member it stands for — the running step, or whichever one
    /// is holding it up. The chain is what you read; an item is what you act on.</summary>
    public IRelayCommand<Chain> SelectChainCommand =>
        _selectChain ??= new RelayCommand<Chain>(chain => { if (chain is not null) { Selected = chain.Head; } });

    public IRelayCommand<Step> SelectStepCommand =>
        _selectStep ??= new RelayCommand<Step>(step => { if (step is not null) { Selected = step.Item; } });

    private IRelayCommand<Chain>? _selectChain;
    private IRelayCommand<Step>? _selectStep;

    /// <summary>Whether a search or a filter is narrowing the board. History is only reachable while this
    /// is true.</summary>
    private bool IsNarrowed => Search is { Length: > 0 }
        || ProjectFilter is { Length: > 0 } || AgentFilter is { Length: > 0 };

    /// <summary>
    /// Narrows the whole queue down to what is on screen, then rebuilds the runway from exactly that.
    /// </summary>
    /// <remarks>
    /// One narrowing feeds every horizon, so a project filter moves Now, Next, Waiting and Landed
    /// together rather than leaving three of them describing a fleet the operator is not looking at.
    /// Search matches the five handles a person has on an item they remember: title, id, work branch,
    /// external id, and the prompt it was given — the last because the median prompt on this instance is
    /// 2,726 characters and is frequently the only place a distinguishing word appears at all.
    /// </remarks>
    private void ApplyView()
    {
        IEnumerable<WorkItemRow> view = _all;

        if (ProjectFilter is { Length: > 0 } project)
        {
            view = view.Where(i => i.ProjectId == project);
        }

        if (AgentFilter is { Length: > 0 } agent)
        {
            view = view.Where(i => i.Agent == agent);
        }

        if (Search is { Length: > 0 } search)
        {
            view = view.Where(i => Matches(i, search));
        }

        // Dispatch order: the orchestrator works the queue by priority DESC then createdAt ASC, so it is
        // the ordering that predicts what happens next rather than merely describing what happened.
        var ordered = view
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => i.CreatedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

        // Reconciled, not rebuilt: an unchanged row keeps its container, so the list stays readable
        // while it updates instead of jumping back to the top. See Reconcile.
        Reconcile.Apply(Items, ordered, i => i.Id);

        OnPropertyChanged(nameof(ViewSummary));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HasAttention));

        ScheduleRebuild();
    }

    /// <summary>The handles a person actually has on an item they are trying to find again.</summary>
    internal static bool Matches(WorkItemRow item, string search)
        => DependencyPicker.Matches(item, search)
        || (item.Prompt?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Guards the background build: a narrowing typed while an older build is in flight must
    /// win, and the older one must not be allowed to land on top of it.</summary>
    private int _rebuildGeneration;

    private Task _rebuild = Task.CompletedTask;

    /// <summary>
    /// The in-flight board build, for a caller that needs the board to exist before it looks at it. The
    /// screen never awaits this — it binds, and lets the assignment arrive.
    /// </summary>
    public Task BoardReady => _rebuild;

    /// <summary>
    /// Rebuilds the runway off the UI thread and assigns it back on it.
    /// </summary>
    /// <remarks>
    /// Off-thread because <see cref="BoardModel.Build"/> walks the whole queue, groups it into chains and
    /// topologically sorts each one — four hundred items' worth of graph work, on every refresh, and the
    /// refresh is driven by an event feed that fires on every state transition in the fleet. Doing that
    /// inline is a stutter on each of them.
    /// </remarks>
    private void ScheduleRebuild()
    {
        var generation = Interlocked.Increment(ref _rebuildGeneration);
        var items = Items.ToList();
        var narrowed = IsNarrowed;
        var projects = _projectRecords.ToList();
        var slots = Slots;
        var selectedId = Selected?.Id;

        _rebuild = Task.Run(async () =>
        {
            try
            {
                var now = DateTimeOffset.Now;
                Board? board = null;
                IReadOnlyList<Chain> history = [];
                Relations? relations = null;

                board = BoardModel.Build(items, projects, slots.Busy, slots.Total, now);
                history = narrowed ? HistoryChains(board, items, now) : [];

                if (selectedId is not null && items.FirstOrDefault(i => i.Id == selectedId) is { } selected)
                {
                    relations = BoardModel.RelationsOf(selected, items, now);
                }

                if (generation != Volatile.Read(ref _rebuildGeneration))
                {
                    return;
                }

                await _toUi(() =>
                {
                    if (board is not null)
                    {
                        Board = board;
                    }

                    HistoryMatches = history;
                    Relations = relations;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Diagnostic.Report("build board", ex);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// The chains a search found in history — the matches the board did not place on a horizon.
    /// </summary>
    /// <remarks>
    /// <see cref="Board"/> reports history as a COUNT, not as rows, because by default history is not
    /// something to render. So the chains are recovered through the model's own
    /// <see cref="BoardModel.RelationsOf"/>, which hands back the chain any item belongs to: whatever the
    /// board did not show is asked for one item at a time and deduplicated by chain membership. Bounded,
    /// because a one-letter search matches most of a four-hundred-item queue and nobody reads four
    /// hundred rows.
    /// </remarks>
    private static IReadOnlyList<Chain> HistoryChains(Board board, IReadOnlyList<WorkItemRow> items, DateTimeOffset now)
    {
        if (board.HistoryCount == 0)
        {
            return [];
        }

        var shown = new HashSet<string>(
            board.Now.Concat(board.Next)
                 .Concat(board.Waiting.SelectMany(g => g.Chains))
                 .Concat(board.Landed.SelectMany(d => d.Chains))
                 .SelectMany(c => c.Steps)
                 .Select(step => step.Item.Id),
            StringComparer.Ordinal);

        var chains = new List<Chain>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items.Where(i => !shown.Contains(i.Id))
                                  .OrderByDescending(i => i.UpdatedAt)
                                  .Take(HistoryMatchLimit))
        {
            if (seen.Contains(item.Id))
            {
                continue;
            }

            var chain = BoardModel.RelationsOf(item, items, now).Chain;
            foreach (var step in chain.Steps)
            {
                seen.Add(step.Item.Id);
            }

            seen.Add(item.Id);
            chains.Add(chain);
        }

        return chains;
    }

    /// <summary>How many history chains a search lists before it stops. A search narrow enough to be
    /// useful never reaches this; one that does was not a search.</summary>
    private const int HistoryMatchLimit = 40;

    [ObservableProperty]
    private WorkItemRow? _selected;

    [ObservableProperty]
    private bool _queuePaused;

    [ObservableProperty]
    private string _status = "Not loaded";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The selected item's agent output, oldest first.</summary>
    public string Output => _output.ToString();

    /// <summary>Raised per appended chunk, so a renderer can grow a text view rather than rebuild it.</summary>
    public event Action<string>? OutputAppended;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand TogglePauseCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> CancelCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> RetryCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> PromoteCommand { get; }
    /// <summary>
    /// Whether the secondary lifecycle actions are shown. Collapsed by default: replay, resume, recover
    /// and uncancel are recovery moves for an item that has gone wrong, not part of ordinary work, and
    /// showing all eight at once made retry and abandon look like equivalent choices.
    /// </summary>
    [ObservableProperty]
    private bool _showMoreActions;

    public ICommand ToggleMoreActionsCommand =>
        _toggleMore ??= new RelayCommand(() => ShowMoreActions = !ShowMoreActions);

    private ICommand? _toggleMore;

    public IAsyncRelayCommand<WorkItemRow> ReplayCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> AbandonCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> UncancelCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> ResumeItemCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> RecoverCommand { get; }
    public IAsyncRelayCommand<WorkItemQuestion> AnswerQuestionCommand { get; }
    public IAsyncRelayCommand<WorkItemQuestion> DismissQuestionCommand { get; }

    // ---- the decision ------------------------------------------------------------------------------
    // An item blocked on a person is the only thing on this tab that a person MUST act on, and until now
    // the pane said so only by not saying anything else: a state name in a fact row, a truncated error
    // under the output, and eight lifecycle buttons of which exactly two or three applied. What the
    // decision was, what to read before making it, and what each button would actually do were all left
    // to the reader. See Decision.

    /// <summary>
    /// What the selected item is asking of the operator, or null when it is asking nothing.
    /// </summary>
    /// <remarks>
    /// Derived, never assigned from outside: <see cref="RebuildDecision"/> is the only writer, and it is
    /// called from each of the four places an input to it changes — the selection, the question list, the
    /// audit rows (which carry the ceiling), and a refresh that re-reads the projects.
    /// </remarks>
    [ObservableProperty]
    private Decision? _decision;

    public bool HasDecision => Decision is not null;

    /// <summary>
    /// Whether the transcript's own error box is the one telling the story.
    /// </summary>
    /// <remarks>
    /// It is, unless there is a card — the card carries the failure whole, labelled and beside the choices
    /// it bears on, and the box would repeat it a screen lower in truncated form. The box keeps the case
    /// it was written for: a Cancelled item carrying an error is most of the errors on a real instance and
    /// is asking nobody for anything, so it gets no card and needs the box.
    /// </remarks>
    public bool ShowErrorBox => Selected is { HasError: true } && Decision is null;

    partial void OnDecisionChanged(Decision? value)
    {
        OnPropertyChanged(nameof(HasDecision));
        OnPropertyChanged(nameof(ShowErrorBox));
    }

    /// <summary>The commands the decision's choices run. Built once: they are the same instances the rest
    /// of the pane binds, which is the point — the card rearranges the tab's actions rather than adding a
    /// second set of them.</summary>
    private DecisionActions Actions => _actions ??= new DecisionActions(
        Answer: AnswerQuestionCommand,
        Dismiss: DismissQuestionCommand,
        Retry: RetryCommand,
        RaiseCeiling: RaiseAuditCeilingCommand,
        Replay: ReplayCommand,
        Cancel: CancelCommand,
        ShowOutput: ShowOutputCommand,
        ShowTimeline: ShowTimelineCommand,
        ShowDiff: ShowDiffCommand);

    private DecisionActions? _actions;

    /// <summary>
    /// Recomputes the card from the selection, its questions, its project and its audit budget.
    /// </summary>
    /// <remarks>
    /// Cheap enough to call on every input change — it is a few string switches over one row — and that
    /// is deliberate: a card that is stale about whether a question is still open is worse than no card.
    /// </remarks>
    private void RebuildDecision()
    {
        Decision = Decision.For(Selected, [.. Questions], Actions, BaseBranchOf(Selected), CeilingOf(Selected));

        // Explicitly, not only through OnDecisionChanged: moving between two items that are both
        // undecided leaves Decision null on both, raises nothing, and would strand the error box on
        // whichever answer the previous item happened to give.
        OnPropertyChanged(nameof(ShowErrorBox));
    }

    /// <summary>The branch a merge would have gone into. The work item does not carry it; its project
    /// does, and a merge failure that cannot name what it failed against is half a sentence.</summary>
    private string? BaseBranchOf(WorkItemRow? item) => item?.ProjectId is { Length: > 0 } id
        ? _projectRecords.FirstOrDefault(p => p.Id == id)?.DefaultBaseBranch
        : null;

    /// <summary>
    /// The audit-iteration budget this item was measured against, or 0 when it is unknown.
    /// </summary>
    /// <remarks>
    /// Two sources, exact first: the item's own audit-progress rows state the ceiling each iteration ran
    /// against, and they are loaded with the timeline. Where they have not been read, the project's
    /// default is the same number on this deployment — the work item does not carry one of its own.
    /// </remarks>
    private int CeilingOf(WorkItemRow? item)
    {
        if (item is null)
        {
            return 0;
        }

        var fromRows = AuditRows.Count > 0 ? AuditRows.Max(r => r.MaxIterations) : 0;
        if (fromRows > 0)
        {
            return fromRows;
        }

        return item.ProjectId is { Length: > 0 } id
            ? _projectRecords.FirstOrDefault(p => p.Id == id)?.AuditMaxIterations ?? 0
            : 0;
    }

    /// <summary>
    /// Retries an item that exhausted its audit budget, with a bigger budget.
    /// </summary>
    /// <remarks>
    /// <para>Retry THEN patch, and that order is forced rather than chosen: <c>PATCH /workitems/{id}</c>
    /// refuses an audit-budget change on a terminal item, and AuditFailed is terminal. So the retry is
    /// what makes the item patchable, and the patch lands on the queued item behind it. The budget is
    /// read at audit time — after the work phase, minutes away — so the dispatcher picking the item up in
    /// between is not a race that matters.</para>
    ///
    /// <para>The step is the same five the overview's own "Extend ceiling" buys, for the same reason: a
    /// nudge, not a decision to stop measuring.</para>
    /// </remarks>
    public IAsyncRelayCommand<WorkItemRow> RaiseAuditCeilingCommand =>
        _raiseCeiling ??= new AsyncRelayCommand<WorkItemRow>(RaiseAuditCeilingAsync);

    private IAsyncRelayCommand<WorkItemRow>? _raiseCeiling;

    private async Task RaiseAuditCeilingAsync(WorkItemRow? row)
    {
        if (row is null)
        {
            return;
        }

        var ceiling = CeilingOf(row);
        if (ceiling <= 0)
        {
            await _toUi(() => Status = $"{row.ShortId}: no audit budget to raise.").ConfigureAwait(false);
            return;
        }

        try
        {
            IsBusy = true;
            await _client.RetryAsync(row.Id, _cts.Token).ConfigureAwait(false);
            await _client.PatchWorkItemAsync(
                row.Id,
                new AuditBudgetPatch(ceiling + Decision.CeilingStep),
                _cts.Token).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"raise audit ceiling {row.ShortId}", ex);
            await _toUi(() => Status = $"{row.ShortId}: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Questions the selected item's agent is waiting on. Kept beside the transcript rather than behind a
    /// section: an agent blocked on a person is the one thing here that should interrupt someone.
    /// </summary>
    public ObservableCollection<WorkItemQuestion> Questions { get; } = [];

    public bool HasOpenQuestions => Questions.Any(q => q.IsOpen);

    /// <summary>What the person is typing in reply to <see cref="AnsweringQuestion"/>.</summary>
    [ObservableProperty]
    private string _answerText = string.Empty;

    /// <summary>The question the answer box belongs to, or null when nothing is being answered.</summary>
    [ObservableProperty]
    private WorkItemQuestion? _answeringQuestion;

    private async Task AnswerAsync(WorkItemQuestion? question)
    {
        if (question is null)
        {
            return;
        }

        // Two clicks: the first opens the box for that question, the second sends what was typed. The
        // command carries the question either way, so answering never targets whichever row moved under it.
        if (!ReferenceEquals(AnsweringQuestion, question))
        {
            await _toUi(() => { AnsweringQuestion = question; AnswerText = string.Empty; }).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(AnswerText))
        {
            return;
        }

        try
        {
            await _client.AnswerQuestionAsync(question.WorkItemId, question.QuestionId, AnswerText.Trim(), _cts.Token)
                .ConfigureAwait(false);
            await _toUi(() => { AnsweringQuestion = null; AnswerText = string.Empty; }).ConfigureAwait(false);
            await LoadQuestionsAsync(question.WorkItemId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("answer question", ex);
            await _toUi(() => Status = $"Couldn't answer — {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task DismissAsync(WorkItemQuestion? question)
    {
        if (question is null)
        {
            return;
        }

        try
        {
            await _client.DismissQuestionAsync(question.WorkItemId, question.QuestionId, "dismissed from Agnes", _cts.Token)
                .ConfigureAwait(false);
            await LoadQuestionsAsync(question.WorkItemId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("dismiss question", ex);
            await _toUi(() => Status = $"Couldn't dismiss — {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How this item got to where it is: state transitions, agent starts and finishes, each auditor run
    /// and each audit iteration. The single richest explanation the orchestrator holds, and it was buried
    /// in a JSON dump behind a Detail button.
    /// </summary>
    public ObservableCollection<AgentRun> Runs { get; } = [];

    /// <summary>Per-phase duration and cost, joined from the two endpoints that each hold half of it.</summary>
    public ObservableCollection<PhaseSummary> Phases { get; } = [];

    /// <summary>What each auditor objected to, per iteration — why a round failed.</summary>
    public ObservableCollection<AuditIteration> AuditIterations { get; } = [];

    public bool HasRuns => Runs.Count > 0;

    public bool HasAuditIterations => AuditIterations.Count > 0;

    /// <summary>
    /// Whether the timeline has been fetched and came back empty — which is ordinary rather than broken.
    /// It is read out of audit logs that roll daily, so an item older than the retained window has none.
    /// </summary>
    [ObservableProperty]
    private bool _timelineEmpty;

    [ObservableProperty]
    private bool _isTimelineVisible;

    /// <summary>Whether the pane is showing the item's story — its failure, task and live output — rather
    /// than the timeline or the raw detail dump.</summary>
    public bool ShowStory => !IsDetailVisible && !IsTimelineVisible && !IsDiffVisible;

    // The three views are a segmented control, not three buttons: they change what you are LOOKING at and
    // mutate nothing, so they must not be rendered like Retry and Promote — and the active one must be
    // visible, which it was not. The queue filters two panes to the left already work this way; the pane
    // was teaching one convention and then breaking it.
    public bool IsOutputView => ShowStory;
    public bool IsTimelineView => IsTimelineVisible;
    public bool IsDetailView => IsDetailVisible;

    private void NotifyViewChanged()
    {
        OnPropertyChanged(nameof(ShowStory));
        OnPropertyChanged(nameof(IsOutputView));
        OnPropertyChanged(nameof(IsTimelineView));
        OnPropertyChanged(nameof(IsDetailView));
        OnPropertyChanged(nameof(IsDiffView));
    }

    /// <summary>
    /// Whether the task is shown whole. Collapsed by default: the median prompt on this instance is 2,726
    /// characters, so expanding it by default pushed the live output off the screen.
    /// </summary>
    [ObservableProperty]
    private bool _isTaskExpanded;

    public ICommand ToggleTaskCommand =>
        _toggleTask ??= new RelayCommand(() => IsTaskExpanded = !IsTaskExpanded);

    private ICommand? _toggleTask;

    public string TaskToggleLabel => IsTaskExpanded ? "Show less" : "Show full task";

    partial void OnIsTaskExpandedChanged(bool value) => OnPropertyChanged(nameof(TaskToggleLabel));

    /// <summary>
    /// The phase the last failed run died in. The error message itself is short — a median of 17
    /// characters here — and says what broke without saying where, which is the half an engineer needs to
    /// know which gate to look at.
    /// </summary>
    public string? FailingPhase =>
        _allRuns.OrderByDescending(r => r.StartedAt).FirstOrDefault(r => r.Failed)?.PhaseLabel;

    public bool HasFailingPhase => !string.IsNullOrEmpty(FailingPhase);

    /// <summary>Opens the merged pull request. 67 items here have one and the pane previously rendered it
    /// as a number in a text run, so the manager's closing question dead-ended.</summary>
    public ICommand OpenPrCommand => _openPr ??= new RelayCommand<WorkItemRow>(row =>
    {
        if (row?.PrUrl is { Length: > 0 } url)
        {
            OpenUrl(url);
        }
    });

    private ICommand? _openPr;

    /// <summary>
    /// Hands a URL to the desktop. Matches how the host opens links; a plugin has no window handle of its
    /// own to route through, and a failure here must never take the pane down with it.
    /// </summary>
    private static void OpenUrl(string url)
    {
        // Only http(s). The value comes off the orchestrator's API, which is a boundary: handing an
        // arbitrary string to the shell would make a stored value in someone else's database into a
        // command on this machine.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Diagnostic.Report("open-url", ex);
        }
    }

    partial void OnIsTimelineVisibleChanged(bool value) => NotifyViewChanged();

    public ICommand ShowTimelineCommand => _showTimeline ??= new RelayCommand(() =>
    {
        IsTimelineVisible = true;
        IsDetailVisible = false;
        IsDiffVisible = false;
        if (Selected is { } row)
        {
            _ = LoadTimelineAsync(row.Id);
        }
    });

    private ICommand? _showTimeline;

    /// <summary>
    /// Builds the item's history from the orchestrator's <b>database</b> rather than from its logs.
    /// </summary>
    /// <remarks>
    /// The admin UI reconstructs this by scraping audit logs, which is both lossy and short-lived: those
    /// roll daily, and on this instance every item's scraped timeline came back empty. The same item's
    /// agent-history returned seventy-two runs — every phase, every audit gate, every model fallback, with
    /// outcomes. Timings and costs fill in how long each phase took and what it spent, and the audit
    /// reports say what each gate objected to.
    /// </remarks>
    private async Task LoadTimelineAsync(string workItemId)
    {
        try
        {
            var runs = await _client.GetAgentRunsAsync(workItemId, _cts.Token).ConfigureAwait(false);
            var audits = await _client.GetAuditProgressAsync(workItemId, _cts.Token).ConfigureAwait(false);
            var phases = await _client.GetPhaseSummaryAsync(workItemId, _cts.Token).ConfigureAwait(false);

            await _toUi(() =>
            {
                _allRuns.Clear();
                _allRuns.AddRange(runs);

                // Gate-first, because the run list answers "what happened" and the gates answer "why it
                // is not passing" — which is the question actually being asked.
                // Newest iteration first: what blocked it most recently is what is being worked on.
                Reconcile.Apply(
                    AuditRows,
                    [.. audits.OrderByDescending(a => a.WorkAttemptKey, StringComparer.Ordinal)
                              .ThenByDescending(a => a.Iteration)],
                    a => a.Id);

                Reconcile.Apply(Gates, [.. AuditSummary.Gates(runs)], g => g.Gate);
                Progress = AuditSummary.Progress(runs, CeilingForSelected());

                // Reconciled so a re-read appends the new run and leaves the rest of the history alone.
                // A seventy-row history that rebuilds itself is unreadable: the row being read is torn
                // down and the list snaps back to the top, on every update.
                ApplyRuns();
                // Cost share is computed here because only the whole set knows the total. Falls back to
                // duration when nothing carried a cost, so the bars still say where the time went.
                var totalCost = phases.Sum(x => x.CostUsd);
                var totalMs = phases.Sum(x => x.DurationMs);
                Reconcile.Apply(
                    Phases,
                    [.. phases.Select(x => x with
                    {
                        Share = totalCost > 0
                            ? (double)(x.CostUsd / totalCost)
                            : (totalMs > 0 ? (double)x.DurationMs / totalMs : 0),
                    })],
                    x => x.Phase);
                TimelineEmpty = _allRuns.Count == 0;
                OnPropertyChanged(nameof(HasAuditRows));
                RebuildDecision();
                OnPropertyChanged(nameof(AuditSummaryLine));
                OnPropertyChanged(nameof(BlockedIterationCount));
                OnPropertyChanged(nameof(HasGates));
                OnPropertyChanged(nameof(GateSummaryLine));
                OnPropertyChanged(nameof(BlockingGateCount));
                OnPropertyChanged(nameof(FailingPhase));
                OnPropertyChanged(nameof(HasFailingPhase));
                OnPropertyChanged(nameof(HasRuns));
                OnPropertyChanged(nameof(HasAuditIterations));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"history {workItemId}", ex);
            await _toUi(() => Status = $"Couldn't load the history — {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Whether the right pane shows the item's detail rather than its live output.</summary>
    [ObservableProperty]
    private bool _isDetailVisible;

    partial void OnIsDetailVisibleChanged(bool value) => NotifyViewChanged();

    /// <summary>
    /// The real audit verdicts: every iteration, and the findings that blocked it.
    ///
    /// <para>Loaded only with the timeline, never with the queue — the heaviest items on this instance
    /// answer with 1.2–1.8 MB, because the orchestrator's list cap is 20,000 characters per description
    /// against a median finding of 524.</para>
    /// </summary>
    public ObservableCollection<AuditProgressRow> AuditRows { get; } = [];

    public bool HasAuditRows => AuditRows.Count > 0;

    /// <summary>Iterations that blocked, which is the number worth leading with.</summary>
    public int BlockedIterationCount => AuditRows.Count(r => r.Blocked);

    public string AuditSummaryLine => AuditRows.Count == 0
        ? string.Empty
        : BlockedIterationCount == 0
            ? $"{AuditRows.Count} iteration(s) · none blocked"
            : $"{BlockedIterationCount} of {AuditRows.Count} iterations blocked · " +
              $"{AuditRows.Sum(r => r.BlockingFindings)} finding(s)";

    /// <summary>
    /// Fetches one iteration in full and swaps it in.
    ///
    /// <para>Per ITERATION rather than per finding, because that is the granularity the orchestrator
    /// addresses: one request untruncates every finding in the row, so expanding a second finding in the
    /// same iteration costs nothing. The row is replaced rather than mutated — these are records, and
    /// Reconcile turns the swap into a single Replace at that index.</para>
    /// </summary>
    public IAsyncRelayCommand<AuditProgressRow> LoadFullFindingsCommand =>
        _loadFullFindings ??= new AsyncRelayCommand<AuditProgressRow>(LoadFullFindingsAsync);

    private IAsyncRelayCommand<AuditProgressRow>? _loadFullFindings;

    private async Task LoadFullFindingsAsync(AuditProgressRow? row)
    {
        if (row is null || Selected is not { } item)
        {
            return;
        }

        try
        {
            var full = await _client.GetAuditProgressRowAsync(item.Id, row.Id, _cts.Token).ConfigureAwait(false);
            if (full is null)
            {
                return;
            }

            await _toUi(() =>
            {
                var index = AuditRows.IndexOf(row);
                if (index >= 0)
                {
                    AuditRows[index] = full;
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"audit detail {row.Id}", ex);
            await _toUi(() => Status = $"Couldn't load the full finding — {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Which audit gates ran on this item and which of them blocked — the answer to "why is it
    /// not passing", built from the runs the API already returns.</summary>
    public ObservableCollection<GateSummary> Gates { get; } = [];

    public bool HasGates => Gates.Count > 0;

    /// <summary>Gates that have ever blocked this item, which is what the summary leads with.</summary>
    /// <summary>Gates with at least one failed invocation. Not gates that rejected the work — see
    /// <see cref="GateSummary"/> for why the API cannot answer that.</summary>
    public int BlockingGateCount => Gates.Count(g => g.EverBlocked);

    public string GateSummaryLine => Gates.Count == 0
        ? string.Empty
        : BlockingGateCount == 0
            ? $"{Gates.Count} gates ran · every run completed"
            : $"{BlockingGateCount} of {Gates.Count} gates had a run fail";

    /// <summary>How deep into its audit budget the item has gone.</summary>
    [ObservableProperty]
    private AuditProgress? _progress;

    public bool HasProgress => Progress is { IsKnown: true };

    partial void OnProgressChanged(AuditProgress? value) => OnPropertyChanged(nameof(HasProgress));

    /// <summary>
    /// Whether every agent run is listed, rather than the failures and the current iteration. Off by
    /// default: the worst item here holds 666 runs and the overwhelming majority are gates that passed
    /// and will pass again.
    /// </summary>
    [ObservableProperty]
    private bool _showAllRuns;

    partial void OnShowAllRunsChanged(bool value)
    {
        ApplyRuns();
        OnPropertyChanged(nameof(RunsToggleLabel));
    }

    public ICommand ToggleAllRunsCommand =>
        _toggleAllRuns ??= new RelayCommand(() => ShowAllRuns = !ShowAllRuns);

    private ICommand? _toggleAllRuns;

    public string RunsToggleLabel => ShowAllRuns
        ? "Show only failures and the current iteration"
        : $"Show all {_allRuns.Count} runs";

    /// <summary>Every run, before the default narrowing. Held so the toggle costs nothing.</summary>
    private readonly List<AgentRun> _allRuns = [];

    public string RunsSummaryLine => _allRuns.Count == 0
        ? string.Empty
        : ShowAllRuns
            ? $"all {_allRuns.Count} runs"
            : $"{Runs.Count} of {_allRuns.Count} runs — failures and the current iteration";

    /// <summary>
    /// The audit budget the selected item is measured against. It comes from the project because the work
    /// item does not carry one here; zero when the project is unknown, which the progress bar renders as
    /// a depth with no denominator rather than inventing a ceiling.
    /// </summary>
    private int CeilingForSelected()
        => Projects.FirstOrDefault(p => p.Id == Selected?.ProjectId)?.AuditMaxIterations ?? 0;

    private void ApplyRuns()
    {
        var shown = ShowAllRuns ? _allRuns : AuditSummary.Notable(_allRuns);
        Reconcile.Apply(Runs, [.. shown.OrderByDescending(r => r.StartedAt)], r => r.Id);
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(RunsSummaryLine));
        OnPropertyChanged(nameof(RunsToggleLabel));
    }

    /// <summary>The work branch's diff, classified for colouring.</summary>
    public ObservableCollection<DiffLine> Diff { get; } = [];

    public bool HasDiff => Diff.Count > 0;

    [ObservableProperty]
    private string _diffSummary = string.Empty;

    [ObservableProperty]
    private bool _isDiffVisible;

    partial void OnIsDiffVisibleChanged(bool value) => NotifyViewChanged();

    public bool IsDiffView => IsDiffVisible;

    public ICommand ShowDiffCommand => _showDiff ??= new RelayCommand(() =>
    {
        IsDiffVisible = true;
        IsTimelineVisible = false;
        IsDetailVisible = false;
        if (Selected is { } row)
        {
            _ = LoadDiffAsync(row.Id);
        }
    });

    private ICommand? _showDiff;

    private async Task LoadDiffAsync(string workItemId)
    {
        try
        {
            var text = await _client.GetDiffTextAsync(workItemId, _cts.Token).ConfigureAwait(false);
            var lines = UnifiedDiff.Parse(text);
            await _toUi(() =>
            {
                // Rebuilt, not reconciled. A diff is full of identical lines — every blank context line
                // is equal to every other — so there is no stable key to reconcile against, and keying by
                // content would silently collapse them. It is also loaded once per item on request, so
                // there is no reader to disturb.
                Diff.Clear();
                foreach (var line in lines)
                {
                    Diff.Add(line);
                }
                DiffSummary = lines.Count == 0
                    ? "No diff recorded for this item."
                    : UnifiedDiff.Summarise(lines);
                OnPropertyChanged(nameof(HasDiff));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"diff {workItemId}", ex);
            await _toUi(() => DiffSummary = $"Couldn't load the diff — {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>The selected item's detail, gathered from the orchestrator's per-item endpoints.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    public ICommand ShowOutputCommand => _showOutput ??= new RelayCommand(() =>
    {
        IsDetailVisible = false;
        IsTimelineVisible = false;
        IsDiffVisible = false;
    });

    public ICommand ShowDetailCommand => _showDetail ??= new RelayCommand(() =>
    {
        IsDetailVisible = true;
        IsTimelineVisible = false;
        IsDiffVisible = false;
        if (Selected is { } row)
        {
            _ = LoadDetailAsync(row.Id);
        }
    });

    private ICommand? _showOutput;
    private ICommand? _showDetail;

    /// <summary>
    /// Gathers everything the orchestrator knows about one work item into a single pane.
    /// </summary>
    /// <remarks>
    /// Fetched only when the pane is opened, and each part independently: these are eight endpoints, most
    /// of them optional, and an item with no diff yet or an orchestrator without audit reports should cost
    /// a line saying so rather than the whole pane. Rendered as JSON because these shapes are wide,
    /// instance-specific and not ours to model — see <see cref="RawJson"/>.
    /// </remarks>
    private async Task LoadDetailAsync(string workItemId)
    {
        await _toUi(() => Detail = "Loading…").ConfigureAwait(false);
        try
        {
            var parts = new (string Label, RawJson? Value)[]
            {
                ("work item", await _client.GetWorkItemAsync(workItemId, _cts.Token).ConfigureAwait(false)),
                ("replays", await _client.GetReplaysAsync(workItemId, _cts.Token).ConfigureAwait(false)),
                ("budget usage", await _client.GetWorkItemBudgetUsageAsync(workItemId, _cts.Token).ConfigureAwait(false)),
                ("agent history", await _client.GetAgentHistoryAsync(workItemId, _cts.Token).ConfigureAwait(false)),
                ("costs", await _client.GetCostsAsync(workItemId, _cts.Token).ConfigureAwait(false)),
                ("timings", await _client.GetTimingsAsync(workItemId, _cts.Token).ConfigureAwait(false)),
                ("dependents", await _client.GetDependentsAsync(workItemId, _cts.Token).ConfigureAwait(false)),
                ("audit reports", await _client.GetAuditReportsAsync(workItemId, _cts.Token).ConfigureAwait(false)),
                ("agent streams", await _client.GetAgentStreamsAsync(workItemId, _cts.Token).ConfigureAwait(false)),
                ("attachments", await _client.GetAttachmentsAsync(workItemId, _cts.Token).ConfigureAwait(false)),
            };

            var text = string.Join(Environment.NewLine + Environment.NewLine, parts.Select(p =>
                p.Value is null ? $"── {p.Label}: none" : $"── {p.Label}{Environment.NewLine}{p.Value.Text}"));
            await _toUi(() => Detail = text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"detail {workItemId}", ex);
            await _toUi(() => Detail = $"Couldn't load detail — {ex.Message}").ConfigureAwait(false);
        }
    }

    // ---- steering the dispatch order ----

    /// <summary>
    /// Moving a chain means REWRITING PRIORITIES, not calling a reorder endpoint.
    /// </summary>
    /// <remarks>
    /// <c>POST /workitems/reorder</c> exists and does nothing useful: it writes a <c>queue_position</c>
    /// hint the dispatcher never reads. The order that actually decides pickup is
    /// <c>priority DESC, created_at ASC, id ASC</c>, so a move is a set of priority patches — computed by
    /// <see cref="BoardModel.Reorder"/>, which prefers to move only the dragged item and renumbers its
    /// neighbours only when there is no gap left to land in.
    ///
    /// <para>Confirmed only when it touches more than <see cref="BulkMoveThreshold"/> items. Moving one
    /// chain up a place is an ordinary, visible, reversible act and a dialog on it would train the
    /// operator to dismiss dialogs; renumbering eleven items is a change they cannot see the whole of and
    /// should be told about first.</para>
    /// </remarks>
    private async Task MoveAsync(Chain? chain, Func<int, int> destination)
    {
        if (chain is null || Board is not { } board)
        {
            return;
        }

        var queued = board.Next;
        var index = IndexOf(queued, chain.Id);
        if (index < 0)
        {
            await _toUi(() => Status = "Only queued work can be reordered.").ConfigureAwait(false);
            return;
        }

        var target = Math.Clamp(destination(index), 0, Math.Max(0, queued.Count - 1));
        if (target == index)
        {
            return;
        }

        var changes = BoardModel.Reorder([.. queued.Select(c => c.Head)], chain.Head.Id, target, ProjectCeilings);

        if (changes.Count == 0)
        {
            return;
        }

        if (changes.Count > BulkMoveThreshold)
        {
            Confirmation.Ask(
                "Renumber",
                $"{changes.Count} items to move “{chain.Title}”",
                () => ApplyChangesAsync(changes));
            return;
        }

        await ApplyChangesAsync(changes).ConfigureAwait(false);
    }

    /// <summary>Above this many priority rewrites, a move is confirmed before it is sent.</summary>
    private const int BulkMoveThreshold = 3;

    private static int IndexOf(IReadOnlyList<Chain> chains, string id)
    {
        for (var i = 0; i < chains.Count; i++)
        {
            if (string.Equals(chains[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Sends one priority patch per change, in the order the model returned them.
    /// </summary>
    /// <remarks>
    /// Sequentially and in order: the changes are a renumbering, and applying them concurrently would let
    /// the queue pass through an order nobody asked for — briefly, but the dispatcher is watching and two
    /// slots are free. A failure halfway stops the rest and says so rather than continuing into a state
    /// that is neither the old order nor the new one.
    /// </remarks>
    private async Task ApplyChangesAsync(IReadOnlyList<PriorityChange> changes)
    {
        var sent = 0;
        try
        {
            foreach (var change in changes)
            {
                await _client.SetPriorityAsync(change.Id, change.To, _cts.Token).ConfigureAwait(false);
                sent++;
            }

            await _toUi(() => Status = changes.Count == 1
                ? string.Empty
                : $"Renumbered {changes.Count} items.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("reorder", ex);
            await _toUi(() => Status = $"Moved {sent} of {changes.Count} — {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Puts a chain at the head of the dispatch order: the next thing a free slot picks up.</summary>
    public IAsyncRelayCommand<Chain> RunNextCommand =>
        _runNext ??= new AsyncRelayCommand<Chain>(c => MoveAsync(c, _ => 0));

    public IAsyncRelayCommand<Chain> MoveUpCommand =>
        _moveUp ??= new AsyncRelayCommand<Chain>(c => MoveAsync(c, i => i - 1));

    public IAsyncRelayCommand<Chain> MoveDownCommand =>
        _moveDown ??= new AsyncRelayCommand<Chain>(c => MoveAsync(c, i => i + 1));

    private IAsyncRelayCommand<Chain>? _runNext, _moveUp, _moveDown;

    /// <summary>
    /// "Run after…" is two clicks, not a drag.
    /// </summary>
    /// <remarks>
    /// <see cref="BeginRunAfterCommand"/> arms a chain into <see cref="PendingMove"/> and every row in the
    /// Next list then offers "put it here"; <see cref="RunAfterCommand"/> takes the chain to land behind.
    /// Chosen over a single command carrying both chains because a tuple parameter is not something an
    /// AXAML <c>CommandParameter</c> can express without a converter, and over drag-and-drop because the
    /// dispatch order is a scrolling list of chains — a drag across it is a gesture the operator has to
    /// get right, where two clicks can be abandoned by pressing Escape.
    /// </remarks>
    [ObservableProperty]
    private Chain? _pendingMove;

    public bool HasPendingMove => PendingMove is not null;

    partial void OnPendingMoveChanged(Chain? value) => OnPropertyChanged(nameof(HasPendingMove));

    public IRelayCommand<Chain> BeginRunAfterCommand =>
        _beginRunAfter ??= new RelayCommand<Chain>(chain => PendingMove = chain);

    public IRelayCommand CancelPendingMoveCommand =>
        _cancelPendingMove ??= new RelayCommand(() => PendingMove = null);

    /// <summary>Lands the armed chain immediately after <c>after</c> in the dispatch order.</summary>
    public IAsyncRelayCommand<Chain> RunAfterCommand =>
        _runAfter ??= new AsyncRelayCommand<Chain>(RunAfterAsync);

    private IRelayCommand<Chain>? _beginRunAfter;
    private IRelayCommand? _cancelPendingMove;
    private IAsyncRelayCommand<Chain>? _runAfter;

    private async Task RunAfterAsync(Chain? after)
    {
        if (PendingMove is not { } moved || after is null || Board is not { } board)
        {
            return;
        }

        var moving = moved;
        await _toUi(() => PendingMove = null).ConfigureAwait(false);

        if (string.Equals(moving.Id, after.Id, StringComparison.Ordinal))
        {
            return;
        }

        var target = IndexOf(board.Next, after.Id);
        var from = IndexOf(board.Next, moving.Id);
        if (target < 0)
        {
            return;
        }

        // Landing "after" index n means index n when moving DOWN the list (everything between shifts up
        // by one as the mover leaves) and n+1 when moving up. Getting this wrong puts the chain one place
        // from where the operator pointed, which is the kind of error nobody reports and everybody
        // stops trusting.
        await MoveAsync(moving, _ => from >= 0 && from < target ? target : target + 1).ConfigureAwait(false);
    }

    // ---- editing what a chain waits on ----

    /// <summary>
    /// Drops one dependency edge.
    /// </summary>
    /// <remarks>
    /// Always confirmed and always named, because <c>dependsOn</c> is a REPLACE-SET: the call sends the
    /// whole remaining list, and a mis-click here does not remove an edge so much as declare a new set of
    /// them. Dropping an edge is also how an operator un-wedges a chain whose parent failed, so the
    /// alternative to the edge — retry the parent, uncancel it — sits beside it in the same band.
    /// </remarks>
    public IAsyncRelayCommand<Relation> RemoveDependencyCommand =>
        _removeDependency ??= new AsyncRelayCommand<Relation>(relation =>
        {
            if (relation is not null && Selected is { } item)
            {
                Confirmation.Ask(
                    "Stop waiting on",
                    $"“{relation.Item.Title}” ({relation.Item.ShortId})",
                    () => SetDependenciesAsync(item, [.. Parents(item).Where(id => id != relation.Item.Id)]));
            }

            return Task.CompletedTask;
        });

    private IAsyncRelayCommand<Relation>? _removeDependency;

    /// <summary>The selected item's dependency set as the orchestrator holds it — the list any edit has
    /// to send back whole.</summary>
    private static IReadOnlyList<string> Parents(WorkItemRow item) => [.. item.DependsOn ?? []];

    private async Task SetDependenciesAsync(WorkItemRow item, IReadOnlyList<string> dependsOn)
    {
        try
        {
            await _client.SetDependenciesAsync(item.Id, dependsOn, _cts.Token).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("set dependencies", ex);
            await _toUi(() => Status = $"Couldn't change what it waits on — {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Retries the parent that is holding the selected item up. The unblock for a Failed
    /// parent — a dependency is satisfied only by Done, so a failed one blocks its children forever
    /// until somebody does this or drops the edge.</summary>
    public IAsyncRelayCommand<Relation> RetryParentCommand =>
        _retryParent ??= new AsyncRelayCommand<Relation>(r => Act(r?.Item, _client.RetryAsync));

    public IAsyncRelayCommand<Relation> UncancelParentCommand =>
        _uncancelParent ??= new AsyncRelayCommand<Relation>(r => Act(r?.Item, _client.UncancelAsync));

    private IAsyncRelayCommand<Relation>? _retryParent, _uncancelParent;

    /// <summary>Whether the pane is showing the dependency picker.</summary>
    [ObservableProperty]
    private bool _isAddingDependency;

    /// <summary>The picker, shared by the pane and the composer so the two are learned once.</summary>
    public DependencyPicker Picker { get; } = new();

    public IRelayCommand OpenAddDependencyCommand => _openAddDependency ??= new RelayCommand(() =>
    {
        if (Selected is not { } item)
        {
            return;
        }

        Picker.Reset(item, _all, Parents(item));
        IsAddingDependency = true;
    });

    public IRelayCommand CancelAddDependencyCommand =>
        _cancelAddDependency ??= new RelayCommand(() => IsAddingDependency = false);

    /// <summary>
    /// Applies the ticked set as the item's new dependencies.
    /// </summary>
    /// <remarks>
    /// Checked for cycles first. The picker already excludes the subject's descendants, so a cycle should
    /// be unreachable — but the check is cheap, the queue may have moved under an open picker, and the
    /// alternative is a 400 from the orchestrator arriving after the operator committed.
    /// </remarks>
    public IAsyncRelayCommand ApplyAddDependencyCommand =>
        _applyAddDependency ??= new AsyncRelayCommand(ApplyAddDependencyAsync);

    /// <summary>The same command under the name the pane's "add dependencies" affordance reads best
    /// as. One instance, so arming or disabling one arms or disables the other.</summary>
    public IAsyncRelayCommand AddDependenciesCommand => ApplyAddDependencyCommand;

    private IRelayCommand? _openAddDependency, _cancelAddDependency;
    private IAsyncRelayCommand? _applyAddDependency;

    private async Task ApplyAddDependencyAsync()
    {
        if (Selected is not { } item)
        {
            return;
        }

        var added = Picker.Ticked.Where(id => !Parents(item).Contains(id)).ToList();
        var wanted = Parents(item).Concat(added).Distinct(StringComparer.Ordinal).ToList();

        if (added.Count > 0 && BoardModel.WouldCycle(item.Id, added, _all))
        {
            await _toUi(() => Status = "That would make the chain depend on itself.").ConfigureAwait(false);
            return;
        }

        await _toUi(() => IsAddingDependency = false).ConfigureAwait(false);
        await SetDependenciesAsync(item, wanted).ConfigureAwait(false);
    }

    // ---- the composer ----

    /// <summary>
    /// Creating work, in one place that already knows the answers.
    /// </summary>
    /// <remarks>
    /// Handed functions rather than a reference to this view model: the composer needs to READ the
    /// projects, the queue and the dispatch order at the moment it is opened, and to hand back a refresh
    /// and a selection — that is five arrows, not ownership, and stating them keeps the composer testable
    /// without a queue behind it.
    /// </remarks>
    public ComposerViewModel Composer { get; }

    /// <summary>Opens the composer for an intent, filling it in from what is selected and filtered.</summary>
    public IRelayCommand<ComposerIntent> OpenComposerCommand =>
        _openComposer ??= new RelayCommand<ComposerIntent>(intent =>
            Composer.Open(new ComposerContext(Selected, intent, ProjectFilter, null)));

    private IRelayCommand<ComposerIntent>? _openComposer;

    /// <summary>
    /// Promotes a suggestion through the composer rather than straight into the queue.
    /// </summary>
    /// <remarks>
    /// The orchestrator's own <c>promote</c> creates an item immediately, with the suggestion's title and
    /// rationale and every other field left to the project. That is the right default and the wrong only
    /// option: a promoted suggestion is usually the moment someone wants to say which project, what it
    /// should wait on, and where in the queue it lands. So the button opens the composer seeded from the
    /// suggestion, and creating is the operator's own act.
    /// </remarks>
    private void PromoteSuggestionViaComposer(Suggestion suggestion)
        => Composer.Open(new ComposerContext(null, ComposerIntent.Promote, suggestion.ProjectId, suggestion));

    // ---- creating work, and editing what is queued ----

    [ObservableProperty]
    private string _newTitle = string.Empty;

    [ObservableProperty]
    private string _newPrompt = string.Empty;

    /// <summary>
    /// The project a new item goes to, chosen from the list rather than typed. The form used to require
    /// the id exactly ("codeybox-self"), which is knowledge the interface already had and the person did
    /// not — a memory test standing between them and queueing work.
    /// </summary>
    [ObservableProperty]
    private ProjectChoice? _newProject;

    /// <summary>The agent that project will use unless something overrides it, shown so the choice is not
    /// invisible at the moment it is made.</summary>
    public string NewProjectAgent => NewProject?.DefaultAgent is { Length: > 0 } agent
        ? $"runs on {agent} by default"
        : string.Empty;

    partial void OnNewProjectChanged(ProjectChoice? value)
    {
        OnPropertyChanged(nameof(NewProjectAgent));
        OnPropertyChanged(nameof(CreateDefaultsLine));

        // The auditor profiles on offer are the ones this project actually configures, so choosing a
        // project narrows the choice instead of leaving a free-text field the operator must already know
        // the vocabulary for.
        var profiles = _projectAuditTypes.TryGetValue(value?.Id ?? string.Empty, out var types) ? types : [];
        Reconcile.Apply(AuditorProfiles, profiles, t => t);
        if (NewAuditorProfile is { } chosen && !profiles.Contains(chosen))
        {
            NewAuditorProfile = null;
        }

        OnPropertyChanged(nameof(HasAuditorProfiles));
    }

    /// <summary>Auditor types per project, kept from the projects read so the create form can offer them.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _projectAuditTypes = [];

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private int _priority;

    [ObservableProperty]
    private string _promptEdit = string.Empty;

    public ICommand ToggleCreateCommand => _toggleCreate ??= new RelayCommand(() =>
    {
        IsCreating = !IsCreating;
        // Preselect: the project being filtered on if there is one, else the busiest. Either beats an
        // empty picker, and both are better than asking for an id from memory.
        NewProject ??= Projects.FirstOrDefault(p => p.Id == ProjectFilter) ?? Projects.FirstOrDefault();
    });
    private ICommand? _toggleCreate;

    public IAsyncRelayCommand CreateCommand => _create ??= new AsyncRelayCommand(CreateAsync);
    private IAsyncRelayCommand? _create;

    public IAsyncRelayCommand SetPriorityCommand => _setPriority ??= new AsyncRelayCommand(SetPriorityAsync);
    private IAsyncRelayCommand? _setPriority;

    public IAsyncRelayCommand SetPromptCommand => _setPrompt ??= new AsyncRelayCommand(SetPromptAsync);
    private IAsyncRelayCommand? _setPrompt;

    /// <summary>
    /// Whether the extra creation options are shown. Collapsed by default on purpose: a project, a title
    /// and a prompt is the overwhelmingly common case and stays a three-box form. The remaining options
    /// exist because the endpoint accepts them, not because most items need them.
    /// </summary>
    [ObservableProperty]
    private bool _showCreateOptions;

    public ICommand ToggleCreateOptionsCommand =>
        _toggleCreateOptions ??= new RelayCommand(() => ShowCreateOptions = !ShowCreateOptions);

    private ICommand? _toggleCreateOptions;

    public string CreateOptionsLabel => ShowCreateOptions ? "Fewer options" : "More options";

    partial void OnShowCreateOptionsChanged(bool value) => OnPropertyChanged(nameof(CreateOptionsLabel));

    /// <summary>Agent override. Null means the project's default, which the form states rather than
    /// leaving the operator to guess.</summary>
    [ObservableProperty]
    private string? _newAgent;

    [ObservableProperty]
    private string _newPriority = string.Empty;

    [ObservableProperty]
    private string _newBaseBranch = string.Empty;

    [ObservableProperty]
    private string _newDependsOn = string.Empty;

    [ObservableProperty]
    private string _newAuditMaxIterations = string.Empty;

    [ObservableProperty]
    private string? _newAuditorProfile;

    [ObservableProperty]
    private string _newExternalId = string.Empty;

    [ObservableProperty]
    private bool _newIsRefactor;

    /// <summary>Auditor profiles offered by the chosen project, so this is a choice rather than a string
    /// the operator has to already know.</summary>
    public ObservableCollection<string> AuditorProfiles { get; } = [];

    public bool HasAuditorProfiles => AuditorProfiles.Count > 0;

    /// <summary>What the project would do if nothing here overrode it. Stated so an empty box is
    /// legible as a default rather than as a gap.</summary>
    public string CreateDefaultsLine => NewProject is not { } p
        ? string.Empty
        : $"defaults: agent {p.DefaultAgent ?? "—"}  ·  branch {p.DefaultBaseBranch ?? "—"}" +
          (p.AuditMaxIterations > 0 ? $"  ·  {p.AuditMaxIterations} audit iterations" : string.Empty);

    /// <summary>Parses an optional whole number from a form box. Blank means "unset", not zero — the
    /// distinction the API cares about.</summary>
    private static int? OptionalInt(string text)
        => int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.CurrentCulture, out var value)
            ? value
            : null;

    private static string? OptionalText(string text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private async Task CreateAsync()
    {
        if (NewProject is not { } project || string.IsNullOrWhiteSpace(NewTitle) ||
            string.IsNullOrWhiteSpace(NewPrompt))
        {
            await _toUi(() => Status = "A new work item needs a project, a title and a prompt.").ConfigureAwait(false);
            return;
        }

        try
        {
            var dependsOn = NewDependsOn
                .Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var id = await _client.CreateWorkItemAsync(
                new NewWorkItem(
                    project.Id,
                    NewTitle.Trim(),
                    NewPrompt.Trim(),
                    Agent: OptionalText(NewAgent ?? string.Empty),
                    BaseBranch: OptionalText(NewBaseBranch),
                    Priority: OptionalInt(NewPriority),
                    DependsOn: dependsOn.Length == 0 ? null : dependsOn,
                    AuditMaxIterations: OptionalInt(NewAuditMaxIterations),
                    AuditorProfile: OptionalText(NewAuditorProfile ?? string.Empty),
                    ExternalId: OptionalText(NewExternalId),
                    IsRefactor: NewIsRefactor ? true : null),
                _cts.Token).ConfigureAwait(false);
            await _toUi(() =>
            {
                Status = id is null ? "Created." : $"Created {id[..Math.Min(8, id.Length)]}.";
                NewTitle = string.Empty;
                NewPrompt = string.Empty;
                NewPriority = string.Empty;
                NewBaseBranch = string.Empty;
                NewDependsOn = string.Empty;
                NewAuditMaxIterations = string.Empty;
                NewExternalId = string.Empty;
                NewIsRefactor = false;
                NewAgent = null;
                NewAuditorProfile = null;
                IsCreating = false;
            }).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("create work item", ex);
            await _toUi(() => Status = $"Couldn't create — {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task SetPriorityAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        try
        {
            await _client.SetPriorityAsync(row.Id, Priority, _cts.Token).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("set priority", ex);
            await _toUi(() => Status = $"Couldn't set priority — {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task SetPromptAsync()
    {
        if (Selected is not { } row || string.IsNullOrWhiteSpace(PromptEdit))
        {
            return;
        }

        try
        {
            await _client.SetPromptAsync(row.Id, PromptEdit.Trim(), _cts.Token).ConfigureAwait(false);
            await _toUi(() => Status = "Prompt updated.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("set prompt", ex);
            await _toUi(() => Status = $"Couldn't update the prompt — {ex.Message}").ConfigureAwait(false);
        }
    }

    // ---- the remaining per-item surfaces, each addressed by something the operator supplies ----

    /// <summary>An attachment id, an auditor name, or an agent-stream file name, depending on which of the
    /// buttons beside it is pressed. One field rather than three, because they are used one at a time and
    /// three near-empty boxes would read as three features rather than one lookup.</summary>
    [ObservableProperty]
    private string _detailArgument = string.Empty;

    /// <summary>The JSON patch to apply to the selected item, or its external ids.</summary>
    [ObservableProperty]
    private string _patchBody = string.Empty;

    public IAsyncRelayCommand ShowAttachmentCommand => _showAttachment ??= new AsyncRelayCommand(async () =>
    {
        if (Selected is not { } row || string.IsNullOrWhiteSpace(DetailArgument)) { return; }
        var value = await _client.GetAttachmentAsync(row.Id, DetailArgument.Trim(), _cts.Token).ConfigureAwait(false);
        await _toUi(() => Detail = value is null ? "attachment: none" : value.Text).ConfigureAwait(false);
    });

    public IAsyncRelayCommand DeleteAttachmentCommand => _deleteAttachment ??= new AsyncRelayCommand(async () =>
    {
        if (Selected is not { } row || string.IsNullOrWhiteSpace(DetailArgument)) { return; }
        var attachment = DetailArgument.Trim();
        Confirmation.Ask("Delete attachment", attachment, () => Guarded("delete attachment", async () =>
        {
            await _client.DeleteAttachmentAsync(row.Id, attachment, _cts.Token).ConfigureAwait(false);
            await LoadDetailAsync(row.Id).ConfigureAwait(false);
        }));
        await Task.CompletedTask.ConfigureAwait(false);
    });

    /// <summary>One auditor's report as written, which is prose rather than a record.</summary>
    public IAsyncRelayCommand ShowAuditReportCommand => _showAudit ??= new AsyncRelayCommand(async () =>
    {
        if (Selected is not { } row || string.IsNullOrWhiteSpace(DetailArgument)) { return; }
        await Guarded("audit report", async () =>
        {
            // "<target>/<iteration>/<auditor>", the way the endpoint addresses one.
            var parts = DetailArgument.Split('/', 3);
            if (parts.Length != 3 || !int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var iteration))
            {
                await _toUi(() => Status = "Audit reports are addressed as target/iteration/auditor.").ConfigureAwait(false);
                return;
            }

            var text = await _client.GetAuditReportRawAsync(row.Id, parts[0], iteration, parts[2], _cts.Token)
                .ConfigureAwait(false);
            await _toUi(() => Detail = string.IsNullOrWhiteSpace(text) ? "audit report: none" : text).ConfigureAwait(false);
        }).ConfigureAwait(false);
    });

    public IAsyncRelayCommand ShowStreamAnalysisCommand => _showAnalysis ??= new AsyncRelayCommand(async () =>
    {
        if (Selected is not { } row || string.IsNullOrWhiteSpace(DetailArgument)) { return; }
        var value = await _client.GetAgentStreamAnalysisAsync(row.Id, DetailArgument.Trim(), _cts.Token).ConfigureAwait(false);
        await _toUi(() => Detail = value is null ? "stream analysis: none" : value.Text).ConfigureAwait(false);
    });

    public IAsyncRelayCommand PatchItemCommand => _patchItem ??= new AsyncRelayCommand(
        () => ApplyPatch(false));

    public IAsyncRelayCommand PatchExternalIdsCommand => _patchExternal ??= new AsyncRelayCommand(
        () => ApplyPatch(true));

    /// <summary>Reorders the queue to the order currently shown, which is what the operator can see and
    /// therefore the only order they could mean.</summary>
    public IAsyncRelayCommand ReorderCommand => _reorder ??= new AsyncRelayCommand(async () =>
        await Guarded("reorder", async () =>
        {
            await _client.ReorderAsync([.. Items.Select(i => i.Id)], _cts.Token).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }).ConfigureAwait(false));

    private IAsyncRelayCommand? _showAttachment, _deleteAttachment, _showAudit, _showAnalysis;
    private IAsyncRelayCommand? _patchItem, _patchExternal, _reorder;

    private async Task ApplyPatch(bool externalIds)
    {
        if (Selected is not { } row || string.IsNullOrWhiteSpace(PatchBody))
        {
            return;
        }

        await Guarded(externalIds ? "patch external ids" : "patch work item", async () =>
        {
            using var document = System.Text.Json.JsonDocument.Parse(PatchBody);
            if (externalIds)
            {
                var map = document.RootElement.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
                await _client.PatchExternalIdsAsync(row.Id, map, _cts.Token).ConfigureAwait(false);
            }
            else
            {
                await _client.PatchWorkItemAsync(row.Id, document.RootElement.Clone(), _cts.Token).ConfigureAwait(false);
            }

            await RefreshAsync().ConfigureAwait(false);
            await _toUi(() => Status = "Applied.").ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task Guarded(string what, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report(what, ex);
            await _toUi(() => Status = $"Couldn't {what} — {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task LoadQuestionsAsync(string workItemId)
    {
        var questions = await _client.GetQuestionsAsync(workItemId, _cts.Token).ConfigureAwait(false);
        await _toUi(() =>
        {
            Questions.Clear();
            foreach (var question in questions)
            {
                Questions.Add(question);
            }

            OnPropertyChanged(nameof(HasOpenQuestions));
            RebuildDecision();
        }).ConfigureAwait(false);
    }

    public string PauseButtonText => QueuePaused ? "Resume queue" : "Pause queue";

    partial void OnQueuePausedChanged(bool value) => OnPropertyChanged(nameof(PauseButtonText));

    /// <summary>Loads the queue once, then keeps it fresh. Safe to call more than once.</summary>
    public void Start()
    {
        if (!IsConfigured || _poller is not null)
        {
            return;
        }

        _poller = Task.Run(FollowAsync);
        _drainer = Task.Run(DrainAsync);

        // The tab opens on the overview, so the overview is loaded here rather than waiting for the
        // operator to press the section it is already looking at. Its own loop then keeps it current.
        Sections.StartOverviewRefresh();
        _ = Sections.LoadAsync(CodeyBoxSection.Dashboard);
    }

    /// <summary>
    /// Reads the queue once, then keeps it current from the orchestrator's event feed.
    ///
    /// <para>This used to be a five-second poll, which was wrong in the way that matters: it rebuilt the
    /// list on a timer whether or not anything had changed, so the queue could not be read while it was
    /// open. The orchestrator publishes every state transition over SSE, so the correct behaviour is to
    /// refresh when it says something moved and otherwise leave the view completely alone.</para>
    ///
    /// <para>Changed items are coalesced over a short window before being read back. A single transition
    /// commonly emits several events, and the feed replays its buffer on connect, so acting on each one
    /// individually would mean a burst of requests to describe one change.</para>
    /// </summary>
    private async Task FollowAsync()
    {
        // Stamped before the read, not after: an event that lands DURING the snapshot must be treated as
        // new, since the snapshot may have been taken before its effect was committed.
        var since = DateTimeOffset.UtcNow;
        await RefreshAsync().ConfigureAwait(false);

        var stream = _client.CreateEventStream();
        await stream.RunAsync(
            since,
            OnFeedEventAsync,
            async reconnected =>
            {
                // A reconnect may have missed more than the buffer holds, so the queue is re-read whole
                // rather than trusted to be current. The first connection is already covered by the read
                // above.
                if (reconnected)
                {
                    await RefreshAsync().ConfigureAwait(false);
                }
            },
            _cts.Token).ConfigureAwait(false);
    }

    private Task OnFeedEventAsync(CodeyBoxEvent evt)
    {
        // Every event, before the filter: the queue only needs to know that something moved, but the
        // wall's log names what moved and its heartbeat counts how often — and the phase-level events
        // this filter drops are most of what a busy fleet emits.
        Sections.NoteEvent(evt);

        if (evt.IsWorkItem || evt.IsQueue)
        {
            _pending.Writer.TryWrite(evt);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Drains coalesced feed events. Waits for the first, then gives the orchestrator a moment to finish
    /// emitting the rest of the same transition before reading anything back.
    /// </summary>
    private async Task DrainAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!await _pending.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    return;
                }

                var touchedSelected = false;
                while (_pending.Reader.TryRead(out var evt))
                {
                    touchedSelected |= evt.WorkItemId is { } id && id == Selected?.Id;
                }

                await Task.Delay(CoalesceWindow, _cts.Token).ConfigureAwait(false);
                while (_pending.Reader.TryRead(out var evt))
                {
                    touchedSelected |= evt.WorkItemId is { } id && id == Selected?.Id;
                }

                await RefreshAsync().ConfigureAwait(false);

                // The same transitions the queue reads back are what the overview is built from. It is
                // told, not re-read: it decides for itself whether it is visible and debounces the burst.
                Sections.NoteWorkItemsChanged();

                // The open item's history only changes when that item does, so it is re-read only then.
                if (touchedSelected && IsTimelineVisible && Selected is { } selected)
                {
                    await LoadTimelineAsync(selected.Id).ConfigureAwait(false);
                    await LoadQuestionsAsync(selected.Id).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Diagnostic.Report("feed-drain", ex);
            }
        }
    }

    public async Task RefreshAsync()
    {
        if (!IsConfigured)
        {
            await _toUi(() => Status = "No CodeyBox API key found.").ConfigureAwait(false);
            return;
        }

        try
        {
            var items = await _client.ListWorkItemsAsync(_cts.Token).ConfigureAwait(false);
            var queue = await _client.GetQueueStatusAsync(_cts.Token).ConfigureAwait(false);

            var projects = await _client.GetProjectsAsync(_cts.Token).ConfigureAwait(false);

            // The Now header's denominator. Read at most once a minute, and not at all when Diagnostics
            // has already fetched it — see Slots.
            await RefreshSlotsAsync().ConfigureAwait(false);

            await _toUi(() =>
            {
                var keep = Selected?.Id;

                _projectAuditTypes.Clear();
                _projectRecords.Clear();
                _projectRecords.AddRange(projects);
                foreach (var project in projects)
                {
                    _projectAuditTypes[project.Id] = project.AuditTypes ?? [];
                }

                Reconcile.Apply(
                    Projects,
                    [.. projects.Select(p => new ProjectChoice(
                        p.Id, p.DisplayName, p.DefaultAgent, p.AuditMaxIterations, p.DefaultBaseBranch))],
                    p => p.Id);

                // Agents come from the queue rather than from configuration, so the filter offers what has
                // actually run here — six of them on this instance.
                Load(items);

                // Re-point at the same item across a refresh: the rows are fresh records, so holding the
                // old instance would silently deselect on every poll.
                if (keep is not null)
                {
                    Selected = Items.FirstOrDefault(i => i.Id == keep) ?? Selected;
                }

                // The projects were just re-read, and they carry the base branch and the audit budget the
                // card's sentences are built from.
                RebuildDecision();

                QueuePaused = queue?.IsPaused ?? false;
                Status = QueuePaused ? "Queue paused" : string.Empty;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Diagnostic.Report("refresh", ex);
            await _toUi(() => Status = $"CodeyBox unreachable — {ex.Message}").ConfigureAwait(false);
        }
    }

    async partial void OnSelectedChanged(WorkItemRow? value)
    {
        if (value is null)
        {
            return;
        }

        await _toUi(() =>
        {
            _output.Clear();
            OnPropertyChanged(nameof(Output));
            Questions.Clear();
            Runs.Clear();
            Diff.Clear();
            DiffSummary = string.Empty;
            _allRuns.Clear();
            Gates.Clear();
            AuditRows.Clear();
            Progress = null;
            ShowAllRuns = false;
            Phases.Clear();
            AuditIterations.Clear();
            TimelineEmpty = false;
            OnPropertyChanged(nameof(HasRuns));
            OnPropertyChanged(nameof(HasAuditIterations));
            AnsweringQuestion = null;
            IsAddingDependency = false;
            OnPropertyChanged(nameof(HasOpenQuestions));
            RebuildDecision();

            // The relations band belongs to whichever item is selected, so it is rebuilt here as well as
            // on every refresh — otherwise it would keep describing the item you just navigated away from
            // until the next event happened to arrive.
            ScheduleRebuild();
        }).ConfigureAwait(false);

        try
        {
            // Tail first, then follow: a subscription carries only what happens next, so an item already
            // an hour into its run would otherwise open on an empty pane.
            await LoadQuestionsAsync(value.Id).ConfigureAwait(false);
            if (IsDetailVisible)
            {
                await LoadDetailAsync(value.Id).ConfigureAwait(false);
            }

            if (IsTimelineVisible)
            {
                await LoadTimelineAsync(value.Id).ConfigureAwait(false);
            }

            var tail = await _client.GetStdoutTailAsync(value.Id, _cts.Token).ConfigureAwait(false);
            await _toUi(() =>
            {
                _output.Append(tail);
                OnPropertyChanged(nameof(Output));
                OutputAppended?.Invoke(tail);
            }).ConfigureAwait(false);

            await _client.FollowAsync(value.Id, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"follow {value.ShortId}", ex);
            await _toUi(() => Status = $"Couldn't follow {value.ShortId} — {ex.Message}").ConfigureAwait(false);
        }
    }

    private void OnStdout(StdoutChunk chunk)
    {
        // The hub scopes delivery to the subscribed item's group, but a Follow that has not yet taken
        // effect can still land one chunk from the previous item.
        if (Selected is not { } selected || chunk.WorkItemId != selected.Id)
        {
            return;
        }

        _ = _toUi(() =>
        {
            _output.Append(chunk.Chunk);
            OnPropertyChanged(nameof(Output));
            OutputAppended?.Invoke(chunk.Chunk);
        });
    }

    private void OnStreamCompleted(string workItemId)
    {
        if (Selected?.Id == workItemId)
        {
            _ = _toUi(() => Status = "Agent stream finished.");
        }
    }

    private async Task TogglePauseAsync()
    {
        try
        {
            IsBusy = true;
            if (QueuePaused)
            {
                await _client.ResumeQueueAsync(_cts.Token).ConfigureAwait(false);
            }
            else
            {
                // The orchestrator requires a reason and rejects an empty one — a paused queue nobody can
                // explain later is exactly what that rule exists to prevent.
                await _client.PauseQueueAsync("Paused from Agnes", _cts.Token).ConfigureAwait(false);
            }

            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _toUi(() => Status = $"Couldn't change the queue — {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task Act(WorkItemRow? row, Func<string, CancellationToken, Task> action)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action(row.Id, _cts.Token).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _toUi(() => Status = $"{row.ShortId}: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.StdoutReceived -= OnStdout;
        _client.StreamCompleted -= OnStreamCompleted;
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
        await Sections.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
