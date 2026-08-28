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
public sealed partial class CodeyBoxSectionsViewModel : ObservableObject
{
    private readonly CodeyBoxClient _client;
    private readonly Func<Action, Task> _toUi;
    private readonly HashSet<CodeyBoxSection> _loaded = [];

    public CodeyBoxSectionsViewModel(CodeyBoxClient client, Func<Action, Task> toUi)
    {
        _client = client;
        _toUi = toUi;
        InjectCommand = new AsyncRelayCommand(InjectAsync, () => CanInject);
        PromoteSuggestionCommand = new AsyncRelayCommand<Suggestion>(PromoteSuggestionAsync);
        ResumeAgentCommand = new AsyncRelayCommand<AgentPause>(ResumeAgentAsync);
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync(Section, force: true));
        PauseAgentCommand = new AsyncRelayCommand<AgentPause>(p => AgentAction(p, true));
        DismissSuggestionCommand = new AsyncRelayCommand<Suggestion>(DismissSuggestionAsync);
        CloseReleaseCommand = new AsyncRelayCommand<Release>(r => ReleaseAction(r, _client.CloseReleaseAsync));
        ReopenReleaseCommand = new AsyncRelayCommand<Release>(r => ReleaseAction(r, _client.ReopenReleaseAsync));
        AbandonReleaseCommand = new AsyncRelayCommand<Release>(r => ReleaseAction(r, _client.AbandonReleaseAsync));
        ShipReleaseCommand = new AsyncRelayCommand<Release>(r => ReleaseAction(r, _client.ShipReleaseAsync));
        QueueTemplateCommand = new AsyncRelayCommand<TaskTemplate>(QueueTemplateAsync);
    }

    public ObservableCollection<FleetProject> Fleet { get; } = [];
    public ObservableCollection<AgentPause> PausedAgents { get; } = [];
    public ObservableCollection<SupervisionSession> Sessions { get; } = [];
    public ObservableCollection<Suggestion> Suggestions { get; } = [];
    public ObservableCollection<Release> Releases { get; } = [];
    public ObservableCollection<TaskTemplate> Templates { get; } = [];
    public ObservableCollection<Project> Projects { get; } = [];
    public ObservableCollection<OrchestratorPlugin> Plugins { get; } = [];

    [ObservableProperty]
    private CodeyBoxSection _section = CodeyBoxSection.Queue;

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
        foreach (var name in new[] { nameof(IsQueue), nameof(IsFleet), nameof(IsSupervision),
                                     nameof(IsSuggestions), nameof(IsReleases), nameof(IsProjects),
                                     nameof(IsTesting), nameof(IsSetup), nameof(IsDiagnostics) })
        {
            OnPropertyChanged(name);
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
                case CodeyBoxSection.Fleet:
                    var fleet = await _client.GetFleetAsync().ConfigureAwait(false);
                    var paused = await _client.GetPausedAgentsAsync().ConfigureAwait(false);
                    await Fill(Fleet, fleet).ConfigureAwait(false);
                    await Fill(PausedAgents, paused).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Supervision:
                    var sessions = await _client.GetSupervisionSessionsAsync().ConfigureAwait(false);
                    await _toUi(() => SupervisionEnabled = sessions?.Enabled ?? false).ConfigureAwait(false);
                    await Fill(Sessions, sessions?.Sessions ?? []).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Suggestions:
                    var suggestions = await _client.GetSuggestionsAsync().ConfigureAwait(false);
                    await Fill(Suggestions, (suggestions?.Items ?? []).Take(200).ToArray()).ConfigureAwait(false);
                    var count = await _client.GetSuggestionCountAsync().ConfigureAwait(false);
                    await _toUi(() =>
                    {
                        SuggestionCount = count;
                        SectionStatus = $"{suggestions?.Total ?? count} suggestion(s)";
                    }).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Releases:
                    await Fill(Releases, await _client.GetReleasesAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    await Fill(Templates, await _client.GetTemplatesAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Projects:
                    await Fill(Projects, await _client.GetProjectsAsync().ConfigureAwait(false)).ConfigureAwait(false);
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
    /// The orchestrator's own health surfaces, gathered into one pane. Each is optional: several answer 503
    /// when their feature is off, and a null reads as "unavailable here" rather than an error, because on a
    /// given instance that is simply the truth.
    /// </summary>
    private async Task LoadDiagnosticsAsync()
    {
        var workers = await _client.GetWorkerStatusAsync().ConfigureAwait(false);

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

    public IAsyncRelayCommand DeleteTestCaseCommand => _deleteCase ??= new AsyncRelayCommand(
        () => Act("delete test case", id => _client.DeleteTestCaseAsync(id)));

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
        try
        {
            await _client.DisposeLeakedSandboxAsync(LeakedSandboxName.Trim()).ConfigureAwait(false);
            await _toUi(() => { SectionStatus = $"Disposed {LeakedSandboxName}."; LeakedSandboxName = string.Empty; })
                .ConfigureAwait(false);
            await LoadSetupAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic.Report("dispose sandbox", ex);
            await _toUi(() => SectionStatus = $"Couldn't dispose — {ex.Message}").ConfigureAwait(false);
        }
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
