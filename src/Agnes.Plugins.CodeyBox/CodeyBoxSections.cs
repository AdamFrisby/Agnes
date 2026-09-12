using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// The sections of the CodeyBox tab, mirroring the orchestrator's own admin navigation — that nav is the
/// product's considered answer to "what does an operator need", so following it keeps the two in step
/// rather than inventing a second information architecture.
/// </summary>
public enum CodeyBoxSection
{
    /// <summary>Where the tab opens. First because the questions it answers — is it running, can anything
    /// start, does anything need me — precede every question the queue answers.</summary>
    Dashboard,

    /// <summary>The wall: what the fleet is doing this second, drawn to be watched rather than read.
    /// Beside the overview because it is built from the same gather — a different question asked of the
    /// same facts, not a second copy of them.</summary>
    NowWorking,

    Queue,
    Fleet,
    Supervision,
    Suggestions,
    Releases,
    Projects,
    Testing,
    Setup,
    Diagnostics,
}

/// <summary>
/// Everything the tab shows outside the work queue. Each section loads only when it is first opened and
/// then on demand: the orchestrator has around a hundred endpoints, and eagerly polling all of them to
/// render one visible panel would put more load on it than the operator watching it does.
/// </summary>
public sealed partial class CodeyBoxSectionsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CodeyBoxClient _client;
    private readonly Func<Action, Task> _toUi;
    private readonly HashSet<CodeyBoxSection> _loaded = [];

    private readonly Confirmation _confirmation;

    /// <summary>
    /// Shows a work item in the queue. Supplied by the owner rather than reached for, because the sections
    /// deliberately do not know what contains them — the overview needs to hand an item over, not to hold
    /// the queue.
    /// </summary>
    private readonly Action<string>? _openItem;

    /// <summary>
    /// Hands a suggestion to the composer instead of promoting it outright. Supplied the same way
    /// <see cref="_openItem"/> is, and for the same reason: the sections do not know what contains them.
    /// Null falls back to the orchestrator's own one-shot promote, which is what a sections view model
    /// built on its own — in a test, or a future screen — should still do.
    /// </summary>
    private readonly Action<Suggestion>? _promote;

    /// <summary>
    /// The tab's current runway. Supplied as a function for the same reason <see cref="_openItem"/> is:
    /// the sections do not know what contains them, and the wall needs the board the queue already
    /// built rather than a second one of its own.
    /// </summary>
    private readonly Func<Board?> _board;

    /// <summary>The work-item list the last overview gather read, so the wall can total a day's cost
    /// without asking for the list again.</summary>
    private IReadOnlyList<WorkItemRow> _lastItems = [];

    public CodeyBoxSectionsViewModel(
        CodeyBoxClient client,
        Func<Action, Task> toUi,
        Confirmation? confirmation = null,
        Action<string>? openItem = null,
        OverviewHistory? history = null,
        Action<Suggestion>? promote = null,
        Func<Board?>? board = null,
        IWallClock? clock = null)
    {
        _client = client;
        _toUi = toUi;
        _confirmation = confirmation ?? new Confirmation();
        _openItem = openItem;
        _promote = promote;
        _history = history ?? new OverviewHistory();
        OpenItemCommand = new RelayCommand<ItemTrace>(OpenItem);
        ExtendCeilingCommand = new AsyncRelayCommand<ItemTrace>(ExtendCeilingAsync);
        InjectCommand = new AsyncRelayCommand(InjectAsync, () => CanInject);
        PromoteSuggestionCommand = new AsyncRelayCommand<Suggestion>(PromoteSuggestionAsync);
        ResumeAgentCommand = new AsyncRelayCommand<AgentPause>(ResumeAgentAsync);
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync(Section, force: true));
        PauseAgentCommand = new AsyncRelayCommand<AgentPause>(p => AgentAction(p, true));
        DismissSuggestionCommand = new AsyncRelayCommand<Suggestion>(DismissSuggestionAsync);
        CloseReleaseCommand = new AsyncRelayCommand<Release>(r => ReleaseAction(r, _client.CloseReleaseAsync));
        ReopenReleaseCommand = new AsyncRelayCommand<Release>(r => ReleaseAction(r, _client.ReopenReleaseAsync));
        AbandonReleaseCommand = new AsyncRelayCommand<Release>(r =>
        {
            if (r is not null)
            {
                _confirmation.Ask("Abandon release", r.Title ?? r.Id, () => ReleaseAction(r, _client.AbandonReleaseAsync));
            }

            return Task.CompletedTask;
        });
        ShipReleaseCommand = new AsyncRelayCommand<Release>(r => ReleaseAction(r, _client.ShipReleaseAsync));
        QueueTemplateCommand = new AsyncRelayCommand<TaskTemplate>(QueueTemplateAsync);
        _board = board ?? (() => null);

        NowWorking = new NowWorkingViewModel(
            _board,
            () => Overview,
            () => _lastItems,
            toUi,
            client.GetStdoutTailAsync,
            clock: clock);

        // The wall's "fill the tab" switch reaches out of this view model, because what it hides — the
        // rail and the pane header — belongs to the tab and not to the section.
        NowWorking.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NowWorkingViewModel.IsWall))
            {
                OnPropertyChanged(nameof(IsChromeVisible));
            }
        };
    }

    /// <summary>The wall — see <see cref="NowWorkingViewModel"/>. Built up front rather than on first
    /// visit because it has to be able to take feed events from the moment the tab connects, or its log
    /// would start empty every time the section is opened.</summary>
    public NowWorkingViewModel NowWorking { get; }

    /// <summary>
    /// Whether the tab draws its own chrome — the rail of sections and the pane header.
    /// </summary>
    /// <remarks>
    /// False only while the wall is in its full-tab mode. The Agnes tab strip is untouched either way:
    /// a screen you cannot get out of is not a mode, it is a trap.
    /// </remarks>
    public bool IsChromeVisible => !(IsNowWorking && NowWorking.IsWall);

    public ObservableCollection<FleetProject> Fleet { get; } = [];
    public ObservableCollection<AgentPause> PausedAgents { get; } = [];
    public ObservableCollection<SupervisionSession> Sessions { get; } = [];
    /// <summary>The slice on screen. Held separately from <see cref="_allSuggestions"/> so narrowing costs
    /// no round trip — all 162 arrive in one response.</summary>
    public ObservableCollection<Suggestion> Suggestions { get; } = [];

    private readonly List<Suggestion> _allSuggestions = [];

    public ObservableCollection<string> SuggestionCategories { get; } = [];

    [ObservableProperty]
    private SuggestionFilter _suggestionFilter = SuggestionFilter.Important;

    [ObservableProperty]
    private SuggestionSort _suggestionSort = SuggestionSort.Severity;

    [ObservableProperty]
    private string _suggestionSearch = string.Empty;

    [ObservableProperty]
    private string? _suggestionCategory;

    partial void OnSuggestionFilterChanged(SuggestionFilter value) => ApplySuggestions();
    partial void OnSuggestionSortChanged(SuggestionSort value) => ApplySuggestions();
    partial void OnSuggestionSearchChanged(string value) => ApplySuggestions();
    partial void OnSuggestionCategoryChanged(string? value) => ApplySuggestions();

    public bool IsSuggestionFilterImportant => SuggestionFilter == SuggestionFilter.Important;
    public bool IsSuggestionFilterQuickWins => SuggestionFilter == SuggestionFilter.QuickWins;
    public bool IsSuggestionFilterAll => SuggestionFilter == SuggestionFilter.All;
    public bool IsSuggestionSortSeverity => SuggestionSort == SuggestionSort.Severity;
    public bool IsSuggestionSortEffort => SuggestionSort == SuggestionSort.Effort;
    public bool IsSuggestionSortNewest => SuggestionSort == SuggestionSort.Newest;

    public IRelayCommand<SuggestionFilter> SetSuggestionFilterCommand =>
        _setSuggestionFilter ??= new RelayCommand<SuggestionFilter>(f => SuggestionFilter = f);

    private IRelayCommand<SuggestionFilter>? _setSuggestionFilter;

    public IRelayCommand<SuggestionSort> SetSuggestionSortCommand =>
        _setSuggestionSort ??= new RelayCommand<SuggestionSort>(x => SuggestionSort = x);

    private IRelayCommand<SuggestionSort>? _setSuggestionSort;

    public IRelayCommand ClearSuggestionFiltersCommand => _clearSuggestionFilters ??= new RelayCommand(() =>
    {
        SuggestionSearch = string.Empty;
        SuggestionCategory = null;
        SuggestionFilter = SuggestionFilter.All;
    });

    private IRelayCommand? _clearSuggestionFilters;

    /// <summary>What the current slice shows against the whole, so a filter can never silently hide the
    /// rest of the backlog.</summary>
    public string SuggestionSummary => _allSuggestions.Count == 0
        ? string.Empty
        : $"{Suggestions.Count} of {_allSuggestions.Count}" +
          (ImportantSuggestionCount > 0 ? $"  ·  {ImportantSuggestionCount} important" : string.Empty);

    public int ImportantSuggestionCount => _allSuggestions.Count(s => s.IsImportant);

    private void ApplySuggestions()
    {
        var view = SuggestionView.Apply(
            _allSuggestions, SuggestionFilter, SuggestionSort, SuggestionSearch, SuggestionCategory);

        Reconcile.Apply(Suggestions, view, s => s.Id);

        foreach (var name in new[]
                 {
                     nameof(SuggestionSummary), nameof(ImportantSuggestionCount),
                     nameof(IsSuggestionFilterImportant), nameof(IsSuggestionFilterQuickWins),
                     nameof(IsSuggestionFilterAll), nameof(IsSuggestionSortSeverity),
                     nameof(IsSuggestionSortEffort), nameof(IsSuggestionSortNewest),
                 })
        {
            OnPropertyChanged(name);
        }
    }
    public ObservableCollection<Release> Releases { get; } = [];
    public ObservableCollection<TaskTemplate> Templates { get; } = [];
    public ObservableCollection<Project> Projects { get; } = [];
    public ObservableCollection<OrchestratorPlugin> Plugins { get; } = [];

    /// <summary>
    /// What each destination has behind it, shown in the rail.
    ///
    /// <para>The rail sells nine destinations as equals. On this instance four of them have nothing at
    /// all: supervision is switched off at the orchestrator, there are no releases, no test cases and no
    /// e2e runs. Without a badge the operator pays a click and a load to find that out, and pays it again
    /// next week because there was nothing to remember. A count is cheap; a wasted click is not.</para>
    ///
    /// <para>These are populated as each section loads, so a badge means "when last looked at" rather than
    /// "right now" — which is why an unvisited section shows nothing rather than a zero it cannot justify.</para>
    /// </summary>
    private readonly Dictionary<CodeyBoxSection, string> _badges = [];

    public string SuggestionsBadge => Badge(CodeyBoxSection.Suggestions);
    public string FleetBadge => Badge(CodeyBoxSection.Fleet);
    public string SupervisionBadge => Badge(CodeyBoxSection.Supervision);
    public string ProjectsBadge => Badge(CodeyBoxSection.Projects);
    public string ReleasesBadge => Badge(CodeyBoxSection.Releases);
    public string TestingBadge => Badge(CodeyBoxSection.Testing);

    private string Badge(CodeyBoxSection section) => _badges.TryGetValue(section, out var b) ? b : string.Empty;

    private void SetBadge(CodeyBoxSection section, string text)
    {
        _badges[section] = text;
        OnPropertyChanged(nameof(SuggestionsBadge));
        OnPropertyChanged(nameof(FleetBadge));
        OnPropertyChanged(nameof(SupervisionBadge));
        OnPropertyChanged(nameof(ProjectsBadge));
        OnPropertyChanged(nameof(ReleasesBadge));
        OnPropertyChanged(nameof(TestingBadge));
    }

    /// <summary>"empty" and "off" are different answers and are worth distinguishing: one may fill up
    /// tomorrow, the other needs an orchestrator setting changed.</summary>
    private static string Count(int n) => n == 0 ? "empty" : n.ToString(System.Globalization.CultureInfo.CurrentCulture);

    [ObservableProperty]
    private CodeyBoxSection _section = CodeyBoxSection.Dashboard;

    [ObservableProperty]
    private SupervisionSession? _selectedSession;

    [ObservableProperty]
    private string _injectMessage = string.Empty;

    [ObservableProperty]
    private string _sectionStatus = string.Empty;

    [ObservableProperty]
    private string _diagnostics = string.Empty;

    /// <summary>
    /// Whether supervision is switched on at the orchestrator. Off is the ordinary case on many instances —
    /// this host reports <c>enabled=false</c> — so the panel says so rather than showing an empty list that
    /// looks like "no agents running".
    /// </summary>
    [ObservableProperty]
    private bool _supervisionEnabled = true;

    /// <summary>The current section's name, used as the pane title so the operator can always read where
    /// they are rather than having to spot which of nine buttons looks pressed.</summary>
    public string SectionTitle => Section switch
    {
        CodeyBoxSection.Dashboard => "Overview",
        CodeyBoxSection.NowWorking => "Now working",
        CodeyBoxSection.Queue => "Work queue",
        CodeyBoxSection.Suggestions => "Suggestions",
        CodeyBoxSection.Fleet => "Fleet",
        CodeyBoxSection.Supervision => "Supervision",
        CodeyBoxSection.Releases => "Releases",
        CodeyBoxSection.Projects => "Projects",
        CodeyBoxSection.Testing => "Testing",
        CodeyBoxSection.Setup => "Setup",
        _ => "Diagnostics",
    };

    public bool IsDashboard => Section == CodeyBoxSection.Dashboard;
    public bool IsNowWorking => Section == CodeyBoxSection.NowWorking;
    public bool IsQueue => Section == CodeyBoxSection.Queue;
    public bool IsFleet => Section == CodeyBoxSection.Fleet;
    public bool IsSupervision => Section == CodeyBoxSection.Supervision;
    public bool IsSuggestions => Section == CodeyBoxSection.Suggestions;
    public bool IsReleases => Section == CodeyBoxSection.Releases;
    public bool IsProjects => Section == CodeyBoxSection.Projects;
    public bool IsTesting => Section == CodeyBoxSection.Testing;
    public bool IsSetup => Section == CodeyBoxSection.Setup;
    public bool IsDiagnostics => Section == CodeyBoxSection.Diagnostics;

    public bool CanInject => SelectedSession is not null && !string.IsNullOrWhiteSpace(InjectMessage);

    public IAsyncRelayCommand InjectCommand { get; }
    public IAsyncRelayCommand<Suggestion> PromoteSuggestionCommand { get; }
    public IAsyncRelayCommand<AgentPause> ResumeAgentCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }
    public IAsyncRelayCommand<AgentPause> PauseAgentCommand { get; }
    public IAsyncRelayCommand<Suggestion> DismissSuggestionCommand { get; }
    public IAsyncRelayCommand<Release> CloseReleaseCommand { get; }
    public IAsyncRelayCommand<Release> ReopenReleaseCommand { get; }
    public IAsyncRelayCommand<Release> AbandonReleaseCommand { get; }
    public IAsyncRelayCommand<Release> ShipReleaseCommand { get; }
    public IAsyncRelayCommand<TaskTemplate> QueueTemplateCommand { get; }

    /// <summary>The JSON detail of whichever release, project or suggestion was last opened.</summary>
    [ObservableProperty]
    private string _rowDetail = string.Empty;

    /// <summary>How many suggestions the orchestrator counts, which is cheaper than paging them all.</summary>
    [ObservableProperty]
    private int _suggestionCount;

    public IAsyncRelayCommand<Release> OpenReleaseCommand => _openRelease ??= new AsyncRelayCommand<Release>(async r =>
    {
        if (r is null) { return; }
        await Show("release", [
            ("release", await _client.GetReleaseAsync(r.Id).ConfigureAwait(false)),
            ("audit iterations", await _client.GetReleaseAuditIterationsAsync(r.Id).ConfigureAwait(false)),
        ]).ConfigureAwait(false);

        var items = await _client.GetReleaseWorkItemsAsync(r.Id).ConfigureAwait(false);
        await _toUi(() => RowDetail += $"{Environment.NewLine}{Environment.NewLine}── work items: {items.Count}" +
            string.Concat(items.Select(i => $"{Environment.NewLine}   {i.ShortId}  {i.State,-12} {i.Title}")))
            .ConfigureAwait(false);
    });

    public IAsyncRelayCommand<Project> OpenProjectCommand => _openProject ??= new AsyncRelayCommand<Project>(async p =>
    {
        if (p is null) { return; }
        await Show("project", [
            ("project", await _client.GetProjectAsync(p.Id).ConfigureAwait(false)),
            ("budget", await _client.GetProjectBudgetAsync(p.Id).ConfigureAwait(false)),
            ("budget usage", await _client.GetProjectBudgetUsageAsync(p.Id).ConfigureAwait(false)),
        ]).ConfigureAwait(false);
    });

    public IAsyncRelayCommand<Suggestion> OpenSuggestionCommand => _openSuggestion ??= new AsyncRelayCommand<Suggestion>(async s =>
    {
        if (s is null) { return; }
        await Show("suggestion", [("suggestion", await _client.GetSuggestionAsync(s.Id).ConfigureAwait(false))])
            .ConfigureAwait(false);
    });

    /// <summary>Creates a release, from a JSON body the operator supplies — the request shape is the
    /// orchestrator's and not one this plugin models.</summary>
    public IAsyncRelayCommand CreateReleaseCommand => _createRelease ??= new AsyncRelayCommand(
        () => Create("release", body => _client.CreateReleaseAsync(body), useSetupBody: true));

    public IAsyncRelayCommand<Project> CreateProjectReleaseCommand => _createProjectRelease ??=
        new AsyncRelayCommand<Project>(async p =>
        {
            if (p is null || string.IsNullOrWhiteSpace(SetupBody)) { return; }
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(SetupBody);
                var result = await _client.CreateProjectReleaseAsync(p.Id, document.RootElement.Clone()).ConfigureAwait(false);
                await _toUi(() => RowDetail = Describe("project release", result)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Diagnostic.Report("project release", ex);
                await _toUi(() => SectionStatus = $"Couldn't cut the release — {ex.Message}").ConfigureAwait(false);
            }
        });

    private IAsyncRelayCommand<Release>? _openRelease;
    private IAsyncRelayCommand<Project>? _openProject;
    private IAsyncRelayCommand<Suggestion>? _openSuggestion;
    private IAsyncRelayCommand? _createRelease;
    private IAsyncRelayCommand<Project>? _createProjectRelease;

    public IRelayCommand CloseRowDetailCommand =>
        _closeRowDetail ??= new RelayCommand(() => RowDetail = string.Empty);

    private IRelayCommand? _closeRowDetail;

    private Task Show(string label, (string Label, RawJson? Value)[] parts)
        => _toUi(() => RowDetail = Combine(parts));

    // ---- supervision: follow one session, or the whole fleet ----

    public IAsyncRelayCommand FollowAllSupervisionCommand => _followAll ??= new AsyncRelayCommand(async () =>
    {
        try
        {
            await _client.FollowAllSupervisionAsync().ConfigureAwait(false);
            await _toUi(() => SectionStatus = "Following every supervision session.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("follow all supervision", ex);
            await _toUi(() => SectionStatus = $"Couldn't follow — {ex.Message}").ConfigureAwait(false);
        }
    });

    public IAsyncRelayCommand<SupervisionSession> FollowSessionCommand => _followSession ??=
        new AsyncRelayCommand<SupervisionSession>(async session =>
        {
            if (session is null) { return; }
            try
            {
                await _client.FollowSupervisionSessionAsync(session.SessionId).ConfigureAwait(false);
                await _toUi(() => SectionStatus = $"Following {session.SessionId}.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Diagnostic.Report("follow session", ex);
                await _toUi(() => SectionStatus = $"Couldn't follow — {ex.Message}").ConfigureAwait(false);
            }
        });

    private IAsyncRelayCommand? _followAll;
    private IAsyncRelayCommand<SupervisionSession>? _followSession;

    private async Task ReleaseAction(Release? release, Func<string, CancellationToken, Task> action)
    {
        if (release is null)
        {
            return;
        }

        try
        {
            await action(release.Id, CancellationToken.None).ConfigureAwait(false);
            await LoadAsync(CodeyBoxSection.Releases, force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("release action", ex);
            await _toUi(() => SectionStatus = $"Couldn't act on the release — {ex.Message}").ConfigureAwait(false);
        }
    }

    public IAsyncRelayCommand<Project> PauseProjectCommand => _pauseProject ??=
        new AsyncRelayCommand<Project>(p => ProjectQueue(p, pause: true));

    public IAsyncRelayCommand<Project> ResumeProjectCommand => _resumeProject ??=
        new AsyncRelayCommand<Project>(p => ProjectQueue(p, pause: false));

    private IAsyncRelayCommand<Project>? _pauseProject;
    private IAsyncRelayCommand<Project>? _resumeProject;

    private async Task ProjectQueue(Project? project, bool pause)
    {
        if (project is null)
        {
            return;
        }

        try
        {
            await (pause
                ? _client.PauseProjectQueueAsync(project.Id, "paused from Agnes")
                : _client.ResumeProjectQueueAsync(project.Id)).ConfigureAwait(false);
            var budget = await _client.GetProjectBudgetAsync(project.Id).ConfigureAwait(false);
            await _toUi(() => SectionStatus =
                $"{project.DisplayName}: queue {(pause ? "paused" : "resumed")}" +
                (budget is null ? string.Empty : " · budget read")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("project queue", ex);
            await _toUi(() => SectionStatus = $"Couldn't change {project.DisplayName} — {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task QueueTemplateAsync(TaskTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        try
        {
            await _client.QueueTemplateAsync(template.Name).ConfigureAwait(false);
            await _toUi(() => SectionStatus = $"Queued “{template.Name}”.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("queue template", ex);
            await _toUi(() => SectionStatus = $"Couldn't queue {template.Name} — {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task DismissSuggestionAsync(Suggestion? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        try
        {
            await _client.DismissSuggestionAsync(suggestion.Id, "dismissed from Agnes").ConfigureAwait(false);
            await LoadAsync(CodeyBoxSection.Suggestions, force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("dismiss suggestion", ex);
            await _toUi(() => SectionStatus = $"Couldn't dismiss — {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Pauses or resumes an agent, at whichever granularity the row describes.</summary>
    private async Task AgentAction(AgentPause? pause, bool pausing)
    {
        if (pause is null)
        {
            return;
        }

        try
        {
            const string Reason = "paused from Agnes";
            if (pause.AgentInstanceId is { Length: > 0 } instance)
            {
                await (pausing
                    ? _client.PauseAgentInstanceAsync(pause.Agent, instance, Reason)
                    : _client.ResumeAgentInstanceAsync(pause.Agent, instance)).ConfigureAwait(false);
            }
            else
            {
                await (pausing
                    ? _client.PauseAgentAsync(pause.Agent, Reason)
                    : _client.ResumeAgentAsync(pause.Agent)).ConfigureAwait(false);
            }

            await LoadAsync(CodeyBoxSection.Fleet, force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"{(pausing ? "pause" : "resume")} agent", ex);
            await _toUi(() => SectionStatus = $"Couldn't change {pause.Agent} — {ex.Message}").ConfigureAwait(false);
        }
    }

    public IRelayCommand<CodeyBoxSection> ShowCommand => _show ??=
        new RelayCommand<CodeyBoxSection>(s => { Section = s; _ = LoadAsync(s); });

    private IRelayCommand<CodeyBoxSection>? _show;

    partial void OnSectionChanged(CodeyBoxSection value)
    {
        foreach (var name in new[] { nameof(IsDashboard), nameof(IsNowWorking), nameof(IsQueue),
                                     nameof(IsFleet), nameof(IsSupervision), nameof(IsSuggestions),
                                     nameof(IsReleases), nameof(IsProjects), nameof(IsTesting),
                                     nameof(IsSetup), nameof(IsDiagnostics), nameof(SectionTitle),
                                     nameof(IsChromeVisible) })
        {
            OnPropertyChanged(name);
        }

        // Every timer the wall owns stops the moment it is not the section on screen. A screensaver for
        // a screen nobody is looking at costs this machine a frame loop and the orchestrator a poll.
        if (value == CodeyBoxSection.NowWorking)
        {
            NowWorking.Start();
        }
        else
        {
            NowWorking.Stop();
        }
    }

    partial void OnInjectMessageChanged(string value)
    {
        OnPropertyChanged(nameof(CanInject));
        InjectCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSessionChanged(SupervisionSession? value)
    {
        OnPropertyChanged(nameof(CanInject));
        InjectCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadAsync(CodeyBoxSection section, bool force = false)
    {
        if (!force && !_loaded.Add(section))
        {
            return;
        }

        _loaded.Add(section);
        try
        {
            switch (section)
            {
                case CodeyBoxSection.Dashboard:
                case CodeyBoxSection.NowWorking:
                    // One gather, two screens. The wall shows a different slice of the same facts, so
                    // giving it a gather of its own would double the load on the orchestrator and let
                    // the two screens disagree about the same second.
                    await LoadOverviewAsync().ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Fleet:
                    var fleet = await _client.GetFleetAsync().ConfigureAwait(false);
                    var paused = await _client.GetPausedAgentsAsync().ConfigureAwait(false);
                    await Fill(Fleet, fleet).ConfigureAwait(false);
                    await Fill(PausedAgents, paused).ConfigureAwait(false);
                    await _toUi(() => SetBadge(CodeyBoxSection.Fleet, Count(fleet.Count))).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Supervision:
                    var sessions = await _client.GetSupervisionSessionsAsync().ConfigureAwait(false);
                    await Fill(Sessions, sessions?.Sessions ?? []).ConfigureAwait(false);
                    await _toUi(() =>
                    {
                        SupervisionEnabled = sessions?.Enabled ?? false;
                        SetBadge(CodeyBoxSection.Supervision,
                            SupervisionEnabled ? Count(sessions?.Sessions.Count ?? 0) : "off");
                    }).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Suggestions:
                    var suggestions = await _client.GetSuggestionsAsync().ConfigureAwait(false);
                    var count = await _client.GetSuggestionCountAsync().ConfigureAwait(false);
                    await _toUi(() =>
                    {
                        _allSuggestions.Clear();
                        _allSuggestions.AddRange(suggestions?.Items ?? []);
                        Reconcile.Apply(SuggestionCategories, SuggestionView.Categories(_allSuggestions), c => c);
                        ApplySuggestions();
                        SuggestionCount = count;
                        // The important count, not the total: 162 is a number, 13 is a decision.
                        SetBadge(CodeyBoxSection.Suggestions,
                            ImportantSuggestionCount > 0
                                ? $"{ImportantSuggestionCount}!"
                                : Count(_allSuggestions.Count));
                        SectionStatus = $"{suggestions?.Total ?? count} suggestion(s)";
                    }).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Releases:
                    var releases = await _client.GetReleasesAsync().ConfigureAwait(false);
                    await Fill(Releases, releases).ConfigureAwait(false);
                    await Fill(Templates, await _client.GetTemplatesAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    await _toUi(() => SetBadge(CodeyBoxSection.Releases, Count(releases.Count))).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Projects:
                    var projectList = await _client.GetProjectsAsync().ConfigureAwait(false);
                    await Fill(Projects, projectList).ConfigureAwait(false);
                    await _toUi(() => SetBadge(CodeyBoxSection.Projects, Count(projectList.Count))).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Testing:
                    await LoadTestingAsync().ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Setup:
                    await LoadSetupAsync().ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Diagnostics:
                    await LoadDiagnosticsAsync().ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"load {section}", ex);
            await _toUi(() => SectionStatus = $"Couldn't load — {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The overview, ready to draw — or null before the first load has landed, which is a real state and
    /// the reason <see cref="HasOverview"/> exists rather than the view binding into an empty record.
    /// </summary>
    [ObservableProperty]
    private Overview? _overview;

    public bool HasOverview => Overview is not null;

    partial void OnOverviewChanged(Overview? value) => OnPropertyChanged(nameof(HasOverview));

    /// <summary>Audit progress by work-item id, keyed on the item's <c>UpdatedAt</c>.</summary>
    /// <remarks>
    /// The heaviest items answer <c>/audit-progress</c> with 1.2–1.8 MB, and the overview re-reads on every
    /// transition anywhere in the fleet. Without this, one item finishing would re-download the audit
    /// history of every other live item — so a row is refetched only when the item itself has moved, which
    /// is exactly when its trace can have changed.
    /// </remarks>
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<AuditProgressRow> Rows)> _traces = [];

    /// <summary>How many audit-progress reads may be in flight at once. The orchestrator serves a single
    /// fleet; a client that fans out over every live item at once is a load spike, not a fast refresh.</summary>
    private const int TraceParallelism = 4;

    /// <summary>How far back the quota series is asked for. A week covers the longest window a provider
    /// publishes (<c>seven_day</c>), so a burn-down never starts mid-window with no history behind it.</summary>
    private static readonly TimeSpan QuotaWindow = TimeSpan.FromDays(7);

    private readonly OverviewHistory _history;

    /// <summary>
    /// Everything the overview needs, gathered in one pass and handed to the pure model.
    /// </summary>
    /// <remarks>
    /// <para>The gather is the only part that touches the network, and every surface in it is optional
    /// except the work-item list: quota history, transition health and concurrency are all switched off on
    /// some hosts, so each degrades to null/empty and the model says so rather than inventing a number.</para>
    ///
    /// <para>Audit progress is read for the live items and the failed family only — a decided item's trace
    /// cannot change, and reading 325 finished items would cost more than the whole rest of the overview
    /// put together.</para>
    /// </remarks>
    private async Task LoadOverviewAsync()
    {
        // One gather at a time. Two can be asked for at once — a feed burst and the idle tick, or a manual
        // reload over either — and they share the trace cache, so overlapping them would both double the
        // load on the orchestrator and race the dictionary they are trying to save it with.
        await _gathering.WaitAsync().ConfigureAwait(false);
        try
        {
            await GatherOverviewAsync().ConfigureAwait(false);
        }
        finally
        {
            _gathering.Release();
        }
    }

    private readonly SemaphoreSlim _gathering = new(1, 1);

    private async Task GatherOverviewAsync()
    {
        var items = await _client.ListWorkItemsAsync().ConfigureAwait(false);
        var queue = await _client.GetQueueStatusAsync().ConfigureAwait(false);
        var concurrency = await _client.GetConcurrencyAsync().ConfigureAwait(false);
        var probes = await _client.GetQuotaProbesAsync().ConfigureAwait(false);
        var health = await _client.GetTransitionHealthAsync().ConfigureAwait(false);
        var projects = await _client.GetProjectsAsync().ConfigureAwait(false);

        // A failed item is terminal but still the operator's problem, so its trace is what explains why.
        var traced = items.Where(i => !i.IsTerminal || i.IsFailed).ToList();
        var progress = await TracesAsync(traced).ConfigureAwait(false);
        var questions = await QuestionsAsync(items).ConfigureAwait(false);
        var quota = await QuotaBurnAsync(probes).ConfigureAwait(false);
        var effort = await EffortAsync(items).ConfigureAwait(false);
        var ceilings = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var project in projects.Where(p => p.AuditMaxIterations > 0))
        {
            ceilings[project.Id] = project.AuditMaxIterations;
        }

        var inputs = new OverviewInputs(
            DateTimeOffset.Now,
            items,
            progress,
            questions,
            queue,
            concurrency,
            probes,
            quota,
            health,
            _history.Read(),
            ceilings)
        {
            Effort = effort,
        };
        var built = OverviewModel.Build(inputs);

        await _toUi(() =>
        {
            _lastItems = items;
            Overview = built;
            Reconcile.Apply(Quota, [.. probes.Where(p => p.IsKnown).OrderBy(p => p.Available)], p => p.Label);
            Concurrency = concurrency;
            OnPropertyChanged(nameof(HasQuota));
            SectionStatus = string.Empty;
        }).ConfigureAwait(false);

        // Off the UI thread on purpose: this writes a file, and it is the sample the NEXT build reads, so
        // nothing on screen is waiting for it.
        _history.Append(built.Sample);
    }

    /// <summary>
    /// Active agent time per item, for the drain estimate: the last <see cref="OverviewModel.BurnSample"/>
    /// landed items (fetched once each — a finished item's runs do not change) and everything not yet
    /// terminal (re-read while it moves, cached against its UpdatedAt like the traces).
    /// </summary>
    private async Task<IReadOnlyList<ItemEffort>> EffortAsync(IReadOnlyList<WorkItemRow> items)
    {
        var landed = items.Where(i => i.State == "Done").OrderByDescending(i => i.UpdatedAt).Take(OverviewModel.BurnSample).ToList();
        var live = items.Where(i => !i.IsTerminal).ToList();
        var stale = landed.Concat(live)
            .Where(i => !(_effort.TryGetValue(i.Id, out var cached) && cached.At == i.UpdatedAt))
            .ToList();
        var fetched = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<AgentRun>>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            stale,
            new ParallelOptions { MaxDegreeOfParallelism = TraceParallelism },
            async (item, token) =>
            {
                try
                {
                    fetched[item.Id] = await _client.GetAgentRunsAsync(item.Id, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Diagnostic.Report($"agent-history {item.ShortId}", ex);
                }
            }).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var effort = new List<ItemEffort>(landed.Count + live.Count);
        foreach (var item in landed.Concat(live))
        {
            if (fetched.TryGetValue(item.Id, out var runs))
            {
                _effort[item.Id] = (item.UpdatedAt, runs);
            }
            else if (_effort.TryGetValue(item.Id, out var cached))
            {
                runs = cached.Runs;
            }
            else
            {
                continue;
            }
            if (runs.Count > 0)
            {
                effort.Add(new ItemEffort(item.Id, ItemEffort.ActiveTime(runs, now, item.IsActive), item.State == "Done", item.UpdatedAt));
            }
        }
        return effort;
    }

    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<AgentRun> Runs)> _effort = new(StringComparer.Ordinal);

    /// <summary>Audit progress for the items that can still change, capped and cached.</summary>
    private async Task<IReadOnlyList<ItemAuditProgress>> TracesAsync(IReadOnlyList<WorkItemRow> items)
    {
        var fetched = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<AuditProgressRow>>(
            StringComparer.Ordinal);

        var stale = items.Where(i => !(_traces.TryGetValue(i.Id, out var cached) && cached.At == i.UpdatedAt)).ToList();

        await Parallel.ForEachAsync(
            stale,
            new ParallelOptions { MaxDegreeOfParallelism = TraceParallelism },
            async (item, token) =>
            {
                try
                {
                    fetched[item.Id] = await _client.GetAuditProgressAsync(item.Id, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One item's history failing must not cost the other forty their traces.
                    Diagnostic.Report($"audit-progress {item.ShortId}", ex);
                }
            }).ConfigureAwait(false);

        var traces = new List<ItemAuditProgress>(items.Count);
        foreach (var item in items)
        {
            if (fetched.TryGetValue(item.Id, out var rows))
            {
                _traces[item.Id] = (item.UpdatedAt, rows);
            }
            else if (_traces.TryGetValue(item.Id, out var cached))
            {
                rows = cached.Rows;
            }
            else
            {
                continue;
            }

            if (rows.Count > 0)
            {
                traces.Add(new ItemAuditProgress(item.Id, rows));
            }
        }

        // Items that have left the live set never come back to it; keeping their traces would grow the
        // cache by the size of the whole queue's history over a long-running session.
        var live = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _traces.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _traces.Remove(gone);
        }

        return traces;
    }

    /// <summary>
    /// Open-question counts, for the items that say they are waiting on one. Read only for
    /// <c>NeedsOperatorInput</c>: the endpoint is per item, and the state is the orchestrator's own claim
    /// that there is something to find.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> QuestionsAsync(IReadOnlyList<WorkItemRow> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in items.Where(i => i.State.Equals("NeedsOperatorInput", StringComparison.Ordinal)))
        {
            try
            {
                var questions = await _client.GetQuestionsAsync(item.Id).ConfigureAwait(false);
                var open = questions.Count(q => q.IsOpen);
                if (open > 0)
                {
                    counts[item.Id] = open;
                }
            }
            catch (Exception ex)
            {
                Diagnostic.Report($"questions {item.ShortId}", ex);
            }
        }

        return counts;
    }

    /// <summary>
    /// The burn-downs, one series request per agent that actually reported a reading. Absent everywhere on
    /// a host without the statistics plugin, which is why this returns empty rather than failing the load.
    /// </summary>
    private async Task<IReadOnlyList<QuotaBurn>> QuotaBurnAsync(IReadOnlyList<QuotaProbe> probes)
    {
        // A week of samples is 31 753 rows — about 8 MB — across the four agents this instance probes, and
        // a busy fleet can ask for a gather every five seconds. The series moves on the sampler's clock,
        // not on work-item transitions, so re-reading it faster than it is written buys nothing and costs
        // that 8 MB each time.
        if (_quotaBurns is { } cached && DateTimeOffset.UtcNow - _quotaBurnsAt < QuotaBurnMaxAge)
        {
            return cached;
        }

        var since = DateTimeOffset.UtcNow - QuotaWindow;
        var rows = new List<QuotaHistoryRow>();

        foreach (var agent in probes.Where(p => p.IsKnown).Select(p => p.Agent)
                     .Where(a => !string.IsNullOrWhiteSpace(a))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                rows.AddRange(await _client.GetQuotaHistoryAsync(agent, since).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Diagnostic.Report($"quota-history {agent}", ex);
            }
        }

        _quotaBurns = QuotaHistoryMap.ToBurnDown(rows, probes, DateTimeOffset.UtcNow);
        _quotaBurnsAt = DateTimeOffset.UtcNow;
        return _quotaBurns;
    }

    /// <summary>How stale a burn-down may be. Matches the idle refresh, so the series is re-read on the
    /// timer and not on every transition.</summary>
    private static readonly TimeSpan QuotaBurnMaxAge = TimeSpan.FromSeconds(60);

    private IReadOnlyList<QuotaBurn>? _quotaBurns;
    private DateTimeOffset _quotaBurnsAt;

    /// <summary>Opens the item this row is about in the work queue.</summary>
    /// <remarks>The overview's job is to find the row worth looking at; the queue's is to show it. Sending
    /// the operator to the pane that already follows an item's output beats growing a second one here.</remarks>
    public IRelayCommand<ItemTrace> OpenItemCommand { get; }

    private void OpenItem(ItemTrace? trace)
    {
        if (trace is null)
        {
            return;
        }

        _openItem?.Invoke(trace.Item.Id);
        Section = CodeyBoxSection.Queue;
    }

    /// <summary>
    /// Gives a converging item more audit iterations rather than letting it hit its ceiling and be thrown
    /// away.
    /// </summary>
    /// <remarks>
    /// <para>The one action the overview offers that the queue does not, because it is the one the overview
    /// is uniquely able to justify: near-ceiling-while-still-converging is a shape, not a field, and it is
    /// the case where five more iterations preserve work that would otherwise be discarded.</para>
    ///
    /// <para><c>auditMaxIterations</c> is patchable on <c>PATCH /workitems/{id}</c> for any non-terminal
    /// item — the audit-budget fields are explicitly exempted from the Queued-only rule the other editable
    /// fields follow.</para>
    /// </remarks>
    public IAsyncRelayCommand<ItemTrace> ExtendCeilingCommand { get; }

    /// <summary>How much headroom one press buys. Small enough to be a nudge rather than a decision to
    /// stop measuring, which is what removing the ceiling would be.</summary>
    private const int CeilingStep = 5;

    private async Task ExtendCeilingAsync(ItemTrace? trace)
    {
        if (trace is null || trace.Ceiling <= 0)
        {
            return;
        }

        try
        {
            await _client.PatchWorkItemAsync(
                trace.Item.Id,
                new { auditMaxIterations = trace.Ceiling + CeilingStep }).ConfigureAwait(false);
            await LoadAsync(CodeyBoxSection.Dashboard, force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"extend ceiling {trace.Item.ShortId}", ex);
            await _toUi(() => SectionStatus = $"Couldn't extend {trace.Item.ShortId} — {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    // ---- keeping the overview current -------------------------------------------------------------
    // Two triggers, one loop, and both of them gated on the overview being the section on screen. The
    // reasoning is the same one that makes every other section load lazily: this gather is a dozen
    // requests plus one per live item, and running it for a panel nobody is looking at is pure load on an
    // orchestrator that is busy doing the actual work.

    /// <summary>How long to keep collecting transitions before re-reading. A single item moving emits
    /// several events and a busy fleet moves constantly, so the window folds a burst into one gather.</summary>
    private static readonly TimeSpan OverviewDebounce = TimeSpan.FromSeconds(5);

    /// <summary>How long the overview may sit untouched before it is re-read anyway. Some of what it shows
    /// — a quota window reopening, a projection running down — moves without any item transitioning.</summary>
    private static readonly TimeSpan OverviewIdleRefresh = TimeSpan.FromSeconds(60);

    /// <summary>Which sections are built out of the overview gather, and therefore keep it current.
    /// Two now: the overview itself and the wall.</summary>
    private bool NeedsOverview => Section is CodeyBoxSection.Dashboard or CodeyBoxSection.NowWorking;

    private readonly CancellationTokenSource _refresh = new();
    private readonly SemaphoreSlim _nudged = new(0, 1);
    private int _nudgePending;
    private Task? _refreshLoop;

    /// <summary>
    /// Starts keeping the overview fresh. Safe to call more than once; does nothing until the overview is
    /// the visible section.
    /// </summary>
    public void StartOverviewRefresh()
    {
        if (_refreshLoop is not null)
        {
            return;
        }

        _refreshLoop = Task.Run(RefreshOverviewLoopAsync);
    }

    /// <summary>
    /// Told by the owner that the fleet moved. Coalesced rather than acted on: this is called once per feed
    /// event and a transition emits several.
    /// </summary>
    public void NoteWorkItemsChanged()
    {
        if (!NeedsOverview)
        {
            return;
        }

        if (Interlocked.Exchange(ref _nudgePending, 1) == 0)
        {
            _nudged.Release();
        }
    }

    /// <summary>
    /// Hands one feed event to the wall.
    /// </summary>
    /// <remarks>
    /// Every event, not the coalesced "something changed" nudge above it: the wall's log names what
    /// moved and its heartbeat counts how often, and both of those are lost the moment a burst is folded
    /// into one signal. The wall drops the lot when it is not running, so this costs nothing while any
    /// other section is on screen.
    /// </remarks>
    internal void NoteEvent(CodeyBoxEvent evt) => NowWorking.Note(evt);

    private async Task RefreshOverviewLoopAsync()
    {
        while (!_refresh.IsCancellationRequested)
        {
            try
            {
                // Either a transition arrives or the idle period elapses; both end in the same read.
                if (await _nudged.WaitAsync(OverviewIdleRefresh, _refresh.Token).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref _nudgePending, 0);
                    await Task.Delay(OverviewDebounce, _refresh.Token).ConfigureAwait(false);

                    // Drain whatever landed during the window, so the burst costs one gather and not two.
                    while (await _nudged.WaitAsync(TimeSpan.Zero, _refresh.Token).ConfigureAwait(false))
                    {
                        Interlocked.Exchange(ref _nudgePending, 0);
                    }
                }

                if (NeedsOverview)
                {
                    await LoadAsync(Section, force: true).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Diagnostic.Report("overview-refresh", ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        NowWorking.Dispose();
        await _refresh.CancelAsync().ConfigureAwait(false);
        _refresh.Dispose();
        _nudged.Dispose();
        _gathering.Dispose();
    }

    /// <summary>Per-agent quota headroom — the first thing to look at when the queue stops moving.</summary>
    public ObservableCollection<QuotaProbe> Quota { get; } = [];

    public bool HasQuota => Quota.Count > 0;

    [ObservableProperty]
    private Concurrency? _concurrency;

    public bool HasConcurrency => Concurrency is not null;

    partial void OnConcurrencyChanged(Concurrency? value) => OnPropertyChanged(nameof(HasConcurrency));

    private async Task LoadDiagnosticsAsync()
    {
        var workers = await _client.GetWorkerStatusAsync().ConfigureAwait(false);

        // Two surfaces are lifted out of the raw dump and given a shape, because they answer the question
        // the section is actually opened for — "why is nothing dispatching" — and reading that answer out
        // of the eleventh of twenty JSON blobs is not reading it. Only probes that reported a number are
        // shown: the rest would draw at 0% and read as exhausted rather than unmeasured.
        var probes = await _client.GetQuotaProbesAsync().ConfigureAwait(false);
        var concurrency = await _client.GetConcurrencyAsync().ConfigureAwait(false);
        await _toUi(() =>
        {
            Reconcile.Apply(
                Quota,
                [.. probes.Where(p => p.IsKnown).OrderBy(p => p.Available)],
                p => p.Label);
            Concurrency = concurrency;
            OnPropertyChanged(nameof(HasQuota));
        }).ConfigureAwait(false);

        // Every remaining read-only surface the orchestrator offers, gathered in one place rather than
        // given a screen each: they are diagnostics, consulted when something is wrong, and most of them
        // are optional on any given instance.
        var parts = new (string Label, RawJson? Value)[]
        {
            ("fleet transition health", await _client.GetFleetTransitionHealthAsync().ConfigureAwait(false)),
            ("capacity", await _client.GetCapacityAsync().ConfigureAwait(false)),
            ("quota history", await _client.GetQuotaHistoryAsync().ConfigureAwait(false)),
            ("quota reset advice", await _client.GetQuotaResetAdviceAsync().ConfigureAwait(false)),
            ("quota reset credits", await _client.GetQuotaResetCreditsAsync().ConfigureAwait(false)),
            ("quota retry status", await _client.GetQuotaRetryStatusAsync().ConfigureAwait(false)),
            ("agent pricing", await _client.GetAgentPricingAsync().ConfigureAwait(false)),
            ("sandbox leaks", await _client.GetSandboxLeaksAsync().ConfigureAwait(false)),
            ("leaked sandboxes", await _client.GetLeakedSandboxesAsync().ConfigureAwait(false)),
            ("sandbox resource usage", await _client.GetSandboxResourceUsageAsync().ConfigureAwait(false)),
            ("orchestrator plugins", await _client.GetPluginsRawAsync().ConfigureAwait(false)),
            ("workers", await _client.GetWorkersAsync().ConfigureAwait(false)),
            ("failure events", await _client.GetFailureEventsAsync().ConfigureAwait(false)),
            ("aggregate timings", await _client.GetAggregateTimingsAsync().ConfigureAwait(false)),
            ("aggregate agent streams", await _client.GetAggregateAgentStreamsAsync().ConfigureAwait(false)),
            ("baselines", await _client.GetBaselinesAsync().ConfigureAwait(false)),
            ("baseline images", await _client.GetBaselineImagesAsync().ConfigureAwait(false)),
            ("e2e runs", await _client.GetE2eRunsAsync().ConfigureAwait(false)),
            ("test cases", await _client.GetTestCasesAsync().ConfigureAwait(false)),
            ("GitHub App", await _client.GetGitHubAppStatusAsync().ConfigureAwait(false)),
        };

        var text = string.Join(Environment.NewLine + Environment.NewLine,
        [
            workers is null
                ? "workers: unavailable"
                : $"workers: {workers.CurrentlyRunning}/{workers.MaxConcurrent} running · {workers.QueuedCount} queued",
            .. parts.Select(p => Describe(p.Label, p.Value)),
        ]);

        await _toUi(() => Diagnostics = text).ConfigureAwait(false);
    }

    // ---- testing: end-to-end runs and the cases behind them ----

    /// <summary>The e2e runs and test cases, and the JSON detail of whichever is selected.</summary>
    [ObservableProperty]
    private string _testing = string.Empty;

    /// <summary>Ids the operator types to act on one run, batch or case. A plain field rather than a
    /// selection, because these surfaces are id-addressed and mostly empty on a given instance — a list to
    /// click is worth building only where there is usually something in it.</summary>
    [ObservableProperty]
    private string _testingId = string.Empty;

    /// <summary>The JSON body for creating a run or a case, typed by the operator. These take shapes this
    /// plugin does not model — see <see cref="RawJson"/> — so it accepts them verbatim rather than
    /// pretending to a form it cannot validate.</summary>
    [ObservableProperty]
    private string _testingBody = string.Empty;

    public IAsyncRelayCommand ShowE2eRunCommand => _showRun ??= new AsyncRelayCommand(
        () => Detail("e2e run", id => _client.GetE2eRunAsync(id)));

    public IAsyncRelayCommand ShowE2eBatchCommand => _showBatch ??= new AsyncRelayCommand(
        () => Compose("e2e batch", async id =>
        [
            ("batch", await _client.GetE2eBatchAsync(id).ConfigureAwait(false)),
            ("runs", await _client.GetE2eBatchRunsAsync(id).ConfigureAwait(false)),
        ]));

    public IAsyncRelayCommand CancelE2eRunCommand => _cancelRun ??= new AsyncRelayCommand(
        () => Act("cancel e2e run", id => _client.CancelE2eRunAsync(id)));

    public IAsyncRelayCommand CreateE2eRunCommand => _createRun ??= new AsyncRelayCommand(
        () => Create("e2e run", body => _client.CreateE2eRunAsync(body)));

    public IAsyncRelayCommand CreateE2eRunsCommand => _createRuns ??= new AsyncRelayCommand(
        () => Create("e2e runs", body => _client.CreateE2eRunsAsync(body)));

    public IAsyncRelayCommand ShowTestCaseCommand => _showCase ??= new AsyncRelayCommand(
        () => Compose("test case", async id =>
        [
            ("case", await _client.GetTestCaseAsync(id).ConfigureAwait(false)),
            ("runs", await _client.GetTestCaseRunsAsync(id).ConfigureAwait(false)),
        ]));

    public IAsyncRelayCommand ShowWorkItemTestCasesCommand => _showItemCases ??= new AsyncRelayCommand(
        () => Detail("work item test cases", id => _client.GetTestCasesForWorkItemAsync(id)));

    public IAsyncRelayCommand CreateTestCaseCommand => _createCase ??= new AsyncRelayCommand(
        () => Create("test case", body => _client.CreateTestCaseAsync(body)));

    public IAsyncRelayCommand CreateTestCasesCommand => _createCases ??= new AsyncRelayCommand(
        () => Create("test cases", body => _client.CreateTestCasesAsync(body)));

    public IAsyncRelayCommand UpdateTestCaseCommand => _updateCase ??= new AsyncRelayCommand(async () =>
    {
        if (!TryBody(out var body)) { return; }
        await Act("update test case", id => _client.UpdateTestCaseAsync(id, body)).ConfigureAwait(false);
    });

    public IAsyncRelayCommand DeleteTestCaseCommand => _deleteCase ??= new AsyncRelayCommand(() =>
    {
        if (!string.IsNullOrWhiteSpace(TestingId))
        {
            _confirmation.Ask("Delete test case", TestingId.Trim(),
                () => Act("delete test case", id => _client.DeleteTestCaseAsync(id)));
        }

        return Task.CompletedTask;
    });

    private IAsyncRelayCommand? _showRun, _showBatch, _cancelRun, _createRun, _createRuns;
    private IAsyncRelayCommand? _showCase, _showItemCases, _createCase, _createCases, _updateCase, _deleteCase;

    private async Task LoadTestingAsync()
    {
        var runs = await _client.GetE2eRunsAsync().ConfigureAwait(false);
        var cases = await _client.GetTestCasesAsync().ConfigureAwait(false);
        await _toUi(() => Testing = Combine(("e2e runs", runs), ("test cases", cases))).ConfigureAwait(false);
    }

    // ---- setup: enrolment and one-off maintenance ----

    [ObservableProperty]
    private string _setup = string.Empty;

    /// <summary>The JSON body for connecting a GitHub App, or for a bulk template queue.</summary>
    [ObservableProperty]
    private string _setupBody = string.Empty;

    /// <summary>The sandbox name to dispose of, from the leak list shown above it.</summary>
    [ObservableProperty]
    private string _leakedSandboxName = string.Empty;

    public IAsyncRelayCommand StartGitHubConnectCommand => _startGh ??= new AsyncRelayCommand(async () =>
    {
        var started = await _client.StartGitHubAppConnectAsync().ConfigureAwait(false);
        await _toUi(() => Setup = Describe("github-app/start", started)).ConfigureAwait(false);
    });

    public IAsyncRelayCommand ConnectGitHubCommand => _connectGh ??= new AsyncRelayCommand(
        () => Create("github app connect", body => _client.ConnectGitHubAppAsync(body), useSetupBody: true));

    public IAsyncRelayCommand MigrateBaselinesCommand => _migrate ??= new AsyncRelayCommand(async () =>
    {
        try
        {
            await _client.MigrateBaselinesAsync().ConfigureAwait(false);
            await _toUi(() => SectionStatus = "Baseline migration requested.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("migrate baselines", ex);
            await _toUi(() => SectionStatus = $"Couldn't migrate — {ex.Message}").ConfigureAwait(false);
        }
    });

    public IAsyncRelayCommand DisposeSandboxCommand => _disposeSandbox ??= new AsyncRelayCommand(async () =>
    {
        if (string.IsNullOrWhiteSpace(LeakedSandboxName)) { return; }
        var name = LeakedSandboxName.Trim();
        _confirmation.Ask("Dispose sandbox", name, async () =>
        {
            try
            {
                await _client.DisposeLeakedSandboxAsync(name).ConfigureAwait(false);
                await _toUi(() => { SectionStatus = $"Disposed {name}."; LeakedSandboxName = string.Empty; })
                    .ConfigureAwait(false);
                await LoadSetupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Diagnostic.Report("dispose sandbox", ex);
                await _toUi(() => SectionStatus = $"Couldn't dispose — {ex.Message}").ConfigureAwait(false);
            }
        });
        await Task.CompletedTask.ConfigureAwait(false);
    });

    public IAsyncRelayCommand QueueTemplatesCommand => _queueTemplates ??= new AsyncRelayCommand(
        () => Create("template batch", body => _client.QueueTemplatesAsync(body), useSetupBody: true));

    private IAsyncRelayCommand? _startGh, _connectGh, _migrate, _disposeSandbox, _queueTemplates;

    private async Task LoadSetupAsync()
    {
        var status = await _client.GetGitHubAppStatusAsync().ConfigureAwait(false);
        var leaked = await _client.GetLeakedSandboxesAsync().ConfigureAwait(false);
        var images = await _client.GetBaselineImagesAsync().ConfigureAwait(false);
        await Fill(Plugins, await _client.GetPluginsAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await _toUi(() => Setup = Combine(
            ("GitHub App", status), ("leaked sandboxes", leaked), ("baseline images", images))).ConfigureAwait(false);
    }

    // ---- shared helpers for the id-addressed surfaces ----

    private static string Combine(params (string Label, RawJson? Value)[] parts)
        => string.Join(Environment.NewLine + Environment.NewLine, parts.Select(p => Describe(p.Label, p.Value)));

    private bool TryBody(out System.Text.Json.JsonElement body)
    {
        body = default;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(TestingBody) ? SetupBody : TestingBody);
            body = document.RootElement.Clone();
            return true;
        }
        catch (Exception ex)
        {
            _ = _toUi(() => SectionStatus = $"That isn't valid JSON — {ex.Message}");
            return false;
        }
    }

    private async Task Detail(string label, Func<string, Task<RawJson?>> fetch)
    {
        if (string.IsNullOrWhiteSpace(TestingId))
        {
            await _toUi(() => SectionStatus = "Enter an id first.").ConfigureAwait(false);
            return;
        }

        try
        {
            var value = await fetch(TestingId.Trim()).ConfigureAwait(false);
            await _toUi(() => Testing = Describe(label, value)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report(label, ex);
            await _toUi(() => SectionStatus = $"Couldn't load {label} — {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Like <see cref="Detail"/>, for the ids that answer from more than one endpoint.</summary>
    private async Task Compose(string label, Func<string, Task<(string Label, RawJson? Value)[]>> fetch)
    {
        if (string.IsNullOrWhiteSpace(TestingId))
        {
            await _toUi(() => SectionStatus = "Enter an id first.").ConfigureAwait(false);
            return;
        }

        try
        {
            var parts = await fetch(TestingId.Trim()).ConfigureAwait(false);
            await _toUi(() => Testing = Combine(parts)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report(label, ex);
            await _toUi(() => SectionStatus = $"Couldn't load {label} — {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task Act(string label, Func<string, Task> action)
    {
        if (string.IsNullOrWhiteSpace(TestingId))
        {
            await _toUi(() => SectionStatus = "Enter an id first.").ConfigureAwait(false);
            return;
        }

        try
        {
            await action(TestingId.Trim()).ConfigureAwait(false);
            await _toUi(() => SectionStatus = $"{label}: done.").ConfigureAwait(false);
            await LoadTestingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report(label, ex);
            await _toUi(() => SectionStatus = $"Couldn't {label} — {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task Create(string label, Func<object, Task<RawJson?>> create, bool useSetupBody = false)
    {
        var raw = useSetupBody ? SetupBody : TestingBody;
        if (string.IsNullOrWhiteSpace(raw))
        {
            await _toUi(() => SectionStatus = "Paste a JSON body first.").ConfigureAwait(false);
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            var result = await create(document.RootElement.Clone()).ConfigureAwait(false);
            await _toUi(() =>
            {
                SectionStatus = $"{label} created.";
                if (useSetupBody) { Setup = Describe(label, result); } else { Testing = Describe(label, result); }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report(label, ex);
            await _toUi(() => SectionStatus = $"Couldn't create the {label} — {ex.Message}").ConfigureAwait(false);
        }
    }

    private static string Describe(string label, RawJson? value)
        => value is null ? $"{label}: unavailable on this instance" : $"{label}:{Environment.NewLine}{value.Text}";

    private async Task InjectAsync()
    {
        if (SelectedSession is not { } session || string.IsNullOrWhiteSpace(InjectMessage))
        {
            return;
        }

        try
        {
            var receipt = await _client.InjectAsync(session.SessionId, InjectMessage.Trim(), "Agnes").ConfigureAwait(false);
            await _toUi(() =>
            {
                // The orchestrator can legitimately refuse — the session may have moved on — and that is
                // not the same as the call failing, so the receipt is reported rather than assumed.
                SectionStatus = receipt is null ? "No receipt returned."
                    : receipt.Accepted ? $"Injected ({receipt.Status})."
                    : $"Refused: {receipt.Error ?? receipt.Status}";
                if (receipt?.Accepted == true)
                {
                    InjectMessage = string.Empty;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("inject", ex);
            await _toUi(() => SectionStatus = $"Inject failed — {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task PromoteSuggestionAsync(Suggestion? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        // Promoting is usually the moment someone wants to say which project, what it waits on and where
        // in the queue it lands — none of which the orchestrator's one-shot promote can be told. So when
        // there is a composer to open, this opens it seeded from the suggestion and creating stays the
        // operator's own act.
        if (_promote is { } compose)
        {
            await _toUi(() => compose(suggestion)).ConfigureAwait(false);
            return;
        }

        try
        {
            await _client.PromoteSuggestionAsync(suggestion.Id).ConfigureAwait(false);
            await _toUi(() => SectionStatus = $"Promoted “{suggestion.Title}”.").ConfigureAwait(false);
            await LoadAsync(CodeyBoxSection.Suggestions, force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _toUi(() => SectionStatus = $"Couldn't promote — {ex.Message}").ConfigureAwait(false);
        }
    }

    private Task ResumeAgentAsync(AgentPause? pause) => AgentAction(pause, pausing: false);

    private Task Fill<T>(ObservableCollection<T> target, IReadOnlyList<T> items) => _toUi(() =>
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    });
}
