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

        _client.StdoutReceived += OnStdout;
        _client.StreamCompleted += OnStreamCompleted;

        Sections = new CodeyBoxSectionsViewModel(client, toUi, Confirmation);
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

        // Reconciled: clearing this would drop the open filter dropdown's selection on every update.
        Reconcile.Apply(
            Agents,
            [.. _all.Select(i => i.Agent).Where(a => a is { Length: > 0 }).Distinct().Order()!],
            a => a);

        ApplyView();
    }

    /// <summary>The same items grouped by project, for when one queue serves several repositories.</summary>
    public ObservableCollection<WorkItemGroup> Groups { get; } = [];

    /// <summary>Projects, for the filter and for the new-item picker.</summary>
    public ObservableCollection<ProjectChoice> Projects { get; } = [];

    /// <summary>Agents seen in the queue, for the filter. Read from the items rather than configured, so
    /// it lists what has actually run here.</summary>
    public ObservableCollection<string> Agents { get; } = [];

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private QueueFilter _filter = QueueFilter.NeedsAttention;

    [ObservableProperty]
    private QueueSort _sort = QueueSort.Priority;

    [ObservableProperty]
    private string? _projectFilter;

    [ObservableProperty]
    private string? _agentFilter;

    [ObservableProperty]
    private bool _groupByProject;

    /// <summary>How many items need a person, regardless of the current filter — the number worth knowing
    /// even while looking at something else.</summary>
    public int AttentionCount => _all.Count(i => i.NeedsAttention);

    public bool HasAttention => AttentionCount > 0;

    /// <summary>What the current slice is showing, against the whole, so a filter can never silently hide
    /// the rest of the queue.</summary>
    public string ViewSummary => _all.Count == 0
        ? string.Empty
        : $"{Items.Count} of {_all.Count}" + (AttentionCount > 0 ? $"  ·  {AttentionCount} need attention" : string.Empty);

    public bool IsFilterNeedsAttention => Filter == QueueFilter.NeedsAttention;
    public bool IsFilterActive => Filter == QueueFilter.Active;
    public bool IsFilterDone => Filter == QueueFilter.Done;
    public bool IsFilterCancelled => Filter == QueueFilter.Cancelled;
    public bool IsFilterAll => Filter == QueueFilter.All;

    public IRelayCommand<QueueFilter> SetFilterCommand =>
        _setFilter ??= new RelayCommand<QueueFilter>(f => Filter = f);

    public IRelayCommand<QueueSort> SetSortCommand =>
        _setSort ??= new RelayCommand<QueueSort>(sort => Sort = sort);

    public IRelayCommand ToggleGroupCommand =>
        _toggleGroup ??= new RelayCommand(() => GroupByProject = !GroupByProject);

    /// <summary>Selects a row from the grouped list, which uses buttons rather than a ListBox.</summary>
    public IRelayCommand<WorkItemRow> SelectCommand =>
        _select ??= new RelayCommand<WorkItemRow>(row => { if (row is not null) { Selected = row; } });

    private IRelayCommand<WorkItemRow>? _select;

    public IRelayCommand ClearFiltersCommand => _clearFilters ??= new RelayCommand(() =>
    {
        Search = string.Empty;
        ProjectFilter = null;
        AgentFilter = null;
        Filter = QueueFilter.NeedsAttention;
    });

    private IRelayCommand<QueueFilter>? _setFilter;
    private IRelayCommand<QueueSort>? _setSort;
    private IRelayCommand? _toggleGroup;
    private IRelayCommand? _clearFilters;

    partial void OnSearchChanged(string value) => ApplyView();
    partial void OnFilterChanged(QueueFilter value) => ApplyView();
    partial void OnSortChanged(QueueSort value) => ApplyView();
    partial void OnProjectFilterChanged(string? value) => ApplyView();
    partial void OnAgentFilterChanged(string? value) => ApplyView();
    partial void OnGroupByProjectChanged(bool value) => ApplyView();

    /// <summary>
    /// Narrows the whole queue down to what is on screen. Search matches title, id and work branch —
    /// the three things someone actually has to hand when looking for an item they remember.
    /// </summary>
    private void ApplyView()
    {
        IEnumerable<WorkItemRow> view = _all;

        view = Filter switch
        {
            QueueFilter.NeedsAttention => view.Where(i => i.NeedsAttention),
            QueueFilter.Active => view.Where(i => i.IsActive),
            QueueFilter.Done => view.Where(i => i.State == "Done"),
            QueueFilter.Cancelled => view.Where(i => i.State == "Cancelled"),
            _ => view,
        };

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
            view = view.Where(i =>
                i.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (i.WorkBranch?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        view = Sort switch
        {
            // Highest priority first: the orchestrator works the queue in that order, so it is the
            // ordering that predicts what happens next rather than merely describing what happened.
            QueueSort.Priority => view.OrderByDescending(i => i.Priority).ThenBy(i => i.QueuePosition),
            QueueSort.Recent => view.OrderByDescending(i => i.UpdatedAt),
            QueueSort.Oldest => view.OrderBy(i => i.CreatedAt),
            _ => view.OrderByDescending(i => i.UsageTotal?.CostUsd ?? 0m),
        };

        var ordered = view.ToList();

        // Reconciled, not rebuilt: an unchanged row keeps its container, so the list stays readable
        // while it updates instead of jumping back to the top. See Reconcile.
        Reconcile.Apply(Items, ordered, i => i.Id);

        if (GroupByProject)
        {
            // Existing groups are updated rather than recreated, so an expanded project stays expanded
            // and keeps its scroll position across a refresh.
            var desired = new List<WorkItemGroup>();
            foreach (var group in ordered.GroupBy(i => i.ProjectId ?? "(no project)").OrderBy(g => g.Key))
            {
                var existing = Groups.FirstOrDefault(g => g.Project == group.Key);
                if (existing is not null)
                {
                    existing.Update([.. group]);
                    desired.Add(existing);
                }
                else
                {
                    desired.Add(new WorkItemGroup(group.Key, [.. group]));
                }
            }

            Reconcile.Apply(Groups, desired, g => g.Project);
        }
        else if (Groups.Count > 0)
        {
            Groups.Clear();
        }

        OnPropertyChanged(nameof(ViewSummary));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HasAttention));
        foreach (var name in new[] { nameof(IsFilterNeedsAttention), nameof(IsFilterActive),
                                     nameof(IsFilterDone), nameof(IsFilterCancelled), nameof(IsFilterAll) })
        {
            OnPropertyChanged(name);
        }
    }

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
    public bool ShowStory => !IsDetailVisible && !IsTimelineVisible;

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
    public string? FailingPhase => Runs.FirstOrDefault(r => r.Failed)?.PhaseLabel;

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
            var phases = await _client.GetPhaseSummaryAsync(workItemId, _cts.Token).ConfigureAwait(false);
            var audits = await _client.GetAuditIterationsAsync(workItemId, _cts.Token).ConfigureAwait(false);

            await _toUi(() =>
            {
                // Reconciled so a re-read appends the new run and leaves the rest of the history alone.
                // A seventy-row history that rebuilds itself is unreadable: the row being read is torn
                // down and the list snaps back to the top, on every update.
                Reconcile.Apply(Runs, [.. runs.OrderByDescending(r => r.StartedAt)], r => r.Id);
                Reconcile.Apply(Phases, [.. phases], p => p.Phase);
                Reconcile.Apply(
                    AuditIterations,
                    [.. audits.OrderByDescending(i => i.Iteration)],
                    i => (i.Target, i.Iteration));

                TimelineEmpty = Runs.Count == 0;
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

    /// <summary>The selected item's detail, gathered from the orchestrator's per-item endpoints.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    public ICommand ShowOutputCommand => _showOutput ??= new RelayCommand(() =>
    {
        IsDetailVisible = false;
        IsTimelineVisible = false;
    });

    public ICommand ShowDetailCommand => _showDetail ??= new RelayCommand(() =>
    {
        IsDetailVisible = true;
        IsTimelineVisible = false;
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
                ("diff", await _client.GetDiffAsync(workItemId, _cts.Token).ConfigureAwait(false)),
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

    partial void OnNewProjectChanged(ProjectChoice? value) => OnPropertyChanged(nameof(NewProjectAgent));

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
            var id = await _client.CreateWorkItemAsync(
                new NewWorkItem(project.Id, NewTitle.Trim(), NewPrompt.Trim()), _cts.Token).ConfigureAwait(false);
            await _toUi(() =>
            {
                Status = id is null ? "Created." : $"Created {id[..Math.Min(8, id.Length)]}.";
                NewTitle = string.Empty;
                NewPrompt = string.Empty;
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

            await _toUi(() =>
            {
                var keep = Selected?.Id;

                Reconcile.Apply(
                    Projects,
                    [.. projects.Select(p => new ProjectChoice(p.Id, p.DisplayName, p.DefaultAgent))],
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
            Phases.Clear();
            AuditIterations.Clear();
            TimelineEmpty = false;
            OnPropertyChanged(nameof(HasRuns));
            OnPropertyChanged(nameof(HasAuditIterations));
            AnsweringQuestion = null;
            OnPropertyChanged(nameof(HasOpenQuestions));
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
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
