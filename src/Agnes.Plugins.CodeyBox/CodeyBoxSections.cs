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
    }

    public ObservableCollection<FleetProject> Fleet { get; } = [];
    public ObservableCollection<AgentPause> PausedAgents { get; } = [];
    public ObservableCollection<SupervisionSession> Sessions { get; } = [];
    public ObservableCollection<Suggestion> Suggestions { get; } = [];
    public ObservableCollection<Release> Releases { get; } = [];
    public ObservableCollection<TaskTemplate> Templates { get; } = [];

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
    public bool IsDiagnostics => Section == CodeyBoxSection.Diagnostics;

    public bool CanInject => SelectedSession is not null && !string.IsNullOrWhiteSpace(InjectMessage);

    public IAsyncRelayCommand InjectCommand { get; }
    public IAsyncRelayCommand<Suggestion> PromoteSuggestionCommand { get; }
    public IAsyncRelayCommand<AgentPause> ResumeAgentCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }

    public IRelayCommand<CodeyBoxSection> ShowCommand => _show ??=
        new RelayCommand<CodeyBoxSection>(s => { Section = s; _ = LoadAsync(s); });

    private IRelayCommand<CodeyBoxSection>? _show;

    partial void OnSectionChanged(CodeyBoxSection value)
    {
        foreach (var name in new[] { nameof(IsQueue), nameof(IsFleet), nameof(IsSupervision),
                                     nameof(IsSuggestions), nameof(IsReleases), nameof(IsDiagnostics) })
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

                case CodeyBoxSection.Diagnostics:
                    await LoadDiagnosticsAsync().ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
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
        var capacity = await _client.GetCapacityAsync().ConfigureAwait(false);
        var quota = await _client.GetQuotaHistoryAsync().ConfigureAwait(false);
        var retry = await _client.GetQuotaRetryStatusAsync().ConfigureAwait(false);
        var leaks = await _client.GetSandboxLeaksAsync().ConfigureAwait(false);
        var usage = await _client.GetSandboxResourceUsageAsync().ConfigureAwait(false);
        var health = await _client.GetFleetTransitionHealthAsync().ConfigureAwait(false);

        var text = string.Join(Environment.NewLine + Environment.NewLine,
        [
            workers is null
                ? "workers: unavailable"
                : $"workers: {workers.CurrentlyRunning}/{workers.MaxConcurrent} running · {workers.QueuedCount} queued",
            Describe("fleet transition health", health),
            Describe("capacity", capacity),
            Describe("quota history", quota),
            Describe("quota retry status", retry),
            Describe("sandbox leaks", leaks),
            Describe("sandbox resource usage", usage),
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

    private async Task ResumeAgentAsync(AgentPause? pause)
    {
        if (pause is null)
        {
            return;
        }

        try
        {
            if (pause.AgentInstanceId is { Length: > 0 } instance)
            {
                await _client.ResumeAgentInstanceAsync(pause.Agent, instance).ConfigureAwait(false);
            }
            else
            {
                await _client.ResumeAgentAsync(pause.Agent).ConfigureAwait(false);
            }

            await LoadAsync(CodeyBoxSection.Fleet, force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _toUi(() => SectionStatus = $"Couldn't resume {pause.Agent} — {ex.Message}").ConfigureAwait(false);
        }
    }

    private Task Fill<T>(ObservableCollection<T> target, IReadOnlyList<T> items) => _toUi(() =>
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    });
}
