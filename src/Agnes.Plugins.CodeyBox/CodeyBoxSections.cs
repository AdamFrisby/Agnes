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
                                     nameof(IsDiagnostics) })
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
                    await _toUi(() => SectionStatus = $"{suggestions?.Total ?? 0} suggestion(s)").ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Releases:
                    await Fill(Releases, await _client.GetReleasesAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    await Fill(Templates, await _client.GetTemplatesAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    break;

                case CodeyBoxSection.Projects:
                    await Fill(Projects, await _client.GetProjectsAsync().ConfigureAwait(false)).ConfigureAwait(false);
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
