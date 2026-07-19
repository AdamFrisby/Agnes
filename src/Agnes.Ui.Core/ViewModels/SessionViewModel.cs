using System.Collections.ObjectModel;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.Mvvm;
using Agnes.Ui.Core.Transcript;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives one live session: the chat transcript (middle column), the contextual left-column lists
/// (plan, files modified, tools run), the right-column preview, plus session-level UX — draft and
/// prompt-history persistence, tool-output collapse, full-screen review, and clear connection/
/// session state banners (offline / reconnecting / interrupted / stale).
/// </summary>
public sealed class SessionViewModel : ObservableObject
{
    private readonly IAgnesHost _host;
    private readonly SessionView _view;
    private readonly IUiDispatcher _dispatcher;
    private readonly IPromptStore _prompts;
    private readonly IPermissionPolicy _policy;
    private readonly TranscriptBuilder _transcript = new();
    private readonly Dictionary<string, ToolEntry> _tools = new();
    private readonly Dictionary<string, string> _permissionTitles = new();
    private readonly List<string> _history;
    private int _historyIndex;
    private string _promptText;
    private string _lastPrompt = string.Empty;
    private PlanItemView? _plan;
    private PreviewViewModel? _selectedPreview;
    private bool _leftHidden;
    private bool _toolsExpanded = true;
    private bool _previewFullScreen;
    private bool _interrupted;
    private bool _stale;
    private bool _turnActive;
    private SessionBanner _banner;
    private string _searchQuery = string.Empty;
    private bool _isSearchOpen;
    private bool _isInspectorOpen;
    private bool _showSlash;
    private string _referenceInput = string.Empty;
    private string? _currentModeId;
    private SandboxStatus? _sandbox;
    private GitStatus? _git;
    private string _commitMessage = string.Empty;
    private int _matchCursor = -1;
    private int _promptCursor = -1;
    private int _changeCursor = -1;

    public SessionViewModel(IAgnesHost host, SessionView view, IUiDispatcher dispatcher, string title, IPromptStore? prompts = null, IPermissionPolicy? policy = null)
    {
        _host = host;
        _view = view;
        _dispatcher = dispatcher;
        _prompts = prompts ?? NullPromptStore.Instance;
        _policy = policy ?? NullPermissionPolicy.Instance;
        Title = title;

        _promptText = _prompts.LoadDraft(view.SessionId);
        _history = [.. _prompts.LoadHistory(view.SessionId)];
        _historyIndex = _history.Count;

        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(PromptText));
        SendNowCommand = new AsyncRelayCommand(SendNowAsync, () => !string.IsNullOrWhiteSpace(PromptText));
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsTurnActive);
        RemoveQueuedCommand = new RelayCommand<QueuedPrompt>(RemoveQueued);
        EditQueuedCommand = new RelayCommand<QueuedPrompt>(EditQueued);
        MoveQueuedUpCommand = new RelayCommand<QueuedPrompt>(p => MoveQueued(p, -1));
        MoveQueuedDownCommand = new RelayCommand<QueuedPrompt>(p => MoveQueued(p, +1));
        AllowCommand = new AsyncRelayCommand(() => RespondAsync(allow: true));
        DenyCommand = new AsyncRelayCommand(() => RespondAsync(allow: false));
        RespondWithCommand = new RelayCommand<PermissionOption>(o => { if (o is not null) { _ = RespondWithAsync(o); } });
        ToggleLeftCommand = new RelayCommand(() => { _leftHidden = !_leftHidden; Raise(nameof(ShowLeftPanel)); });
        ToggleToolsCommand = new RelayCommand(() => ToolsExpanded = !ToolsExpanded);
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorOpen = !IsInspectorOpen);
        SetModeCommand = new RelayCommand<SessionMode>(m => { if (m is not null) { _ = SetModeAsync(m); } });
        RefreshGitCommand = new AsyncRelayCommand(RefreshGitAsync);
        RewindToCommand = new RelayCommand<TranscriptItem>(RewindTo);
        ResumeCommand = new RelayCommand(Resume);
        ScheduleCommand = new AsyncRelayCommand(ScheduleAsync, () => !string.IsNullOrWhiteSpace(PromptText) && _view.Info is not null);
        CommitCommand = new AsyncRelayCommand(CommitAsync, () => !string.IsNullOrWhiteSpace(CommitMessage) && GitDirty);
        foreach (var mode in view.Info?.Modes ?? [])
        {
            Modes.Add(mode);
        }

        _currentModeId = view.Info?.CurrentModeId;
        _sandbox = view.Info?.Sandbox;
        PauseSandboxCommand = new AsyncRelayCommand(PauseSandboxAsync, () => HasSandbox && !SandboxPaused);
        ResumeSandboxCommand = new AsyncRelayCommand(ResumeSandboxAsync, () => HasSandbox && SandboxPaused);
        DeleteSandboxCommand = new AsyncRelayCommand(DeleteSandboxAsync, () => HasSandbox);
        AddReferenceCommand = new RelayCommand(AddReference);
        RemoveAttachmentCommand = new RelayCommand<PromptAttachment>(a => { if (a is not null) { Attachments.Remove(a); Raise(nameof(HasAttachments)); } });
        ApplySlashCommand = new RelayCommand<SlashCommand>(ApplySlash);
        ToggleFullScreenCommand = new RelayCommand(() => IsPreviewFullScreen = !IsPreviewFullScreen);
        RecallPreviousCommand = new RelayCommand(RecallPrevious);
        RecallNextCommand = new RelayCommand(RecallNext);
        DismissBannerCommand = new RelayCommand(DismissBanner);
        RetryCommand = new AsyncRelayCommand(RetryAsync);
        OpenSearchCommand = new RelayCommand(() => IsSearchOpen = true);
        CloseSearchCommand = new RelayCommand(() => { IsSearchOpen = false; SearchQuery = string.Empty; });
        NextMatchCommand = new RelayCommand(() => StepMatch(+1));
        PrevMatchCommand = new RelayCommand(() => StepMatch(-1));
        SelectHitCommand = new RelayCommand<SearchHit>(SelectHit);
        NextPromptCommand = new RelayCommand(() => NavigateKind(IsPrompt, +1, ref _promptCursor));
        PrevPromptCommand = new RelayCommand(() => NavigateKind(IsPrompt, -1, ref _promptCursor));
        NextChangeCommand = new RelayCommand(() => NavigateKind(IsChange, +1, ref _changeCursor));
        PrevChangeCommand = new RelayCommand(() => NavigateKind(IsChange, -1, ref _changeCursor));
        ClosePreviewCommand = new RelayCommand(() => SelectedPreview = null);
        ShowToolPreviewCommand = new RelayCommand<ToolCallItem>(t => { if (t is not null) { Preview(t.Header, t.Detail); } });
        ShowFilePreviewCommand = new RelayCommand<ToolEntry>(f => { if (f is not null) { Preview(f.Name, f.Detail); } });
        ShowMessagePreviewCommand = new RelayCommand<MessageBubbleItem>(m =>
        {
            if (m is { IsLong: true })
            {
                Preview($"{m.Speaker} message", m.Text);
            }
        });

        _transcript.PendingPermissionChanged += () => { Raise(nameof(PendingPermission)); RaiseActivity(); };

        _mainAgentNode = new AgentNode(null, title, isMain: true, SelectAgent) { IsSelected = true };
        AgentTree.Add(_mainAgentNode);
        _transcript.SubagentAdded += AddSubagent;

        foreach (var @event in _view.Events)
        {
            Apply(@event);
        }

        _view.EventAppended += OnEvent;
        _host.StateChanged += OnHostStateChanged;
        UpdateBanner();
        _ = RefreshGitAsync();
    }

    public string Title { get; }
    public string SessionId => _view.SessionId;

    private int _rewindIndex = -1;
    private string? _selectedAgentId;

    /// <summary>Whether the transcript is showing an earlier point in history (read-only).</summary>
    public bool IsRewound => _rewindIndex >= 0;

    /// <summary>
    /// The transcript to display — the full live list, optionally rewound to a point and/or
    /// filtered to the selected subagent's sub-conversation.
    /// </summary>
    public IEnumerable<TranscriptItem> DisplayItems
    {
        get
        {
            IEnumerable<TranscriptItem> basis = IsRewound ? Items.Take(_rewindIndex + 1) : Items;
            if (_selectedAgentId is null)
            {
                return IsRewound ? basis.ToList() : Items;
            }

            return basis.Where(i => i.AgentId == _selectedAgentId).ToList();
        }
    }

    // ---- agent / subagent tree ----

    /// <summary>The session's agents: the main agent with any subagents nested beneath it.</summary>
    public ObservableCollection<AgentNode> AgentTree { get; } = [];

    public bool HasSubagents => _mainAgentNode.Children.Count > 0;

    public string? SelectedAgentId => _selectedAgentId;

    private AgentNode _mainAgentNode = null!;
    private readonly Dictionary<string, AgentNode> _agentNodes = new();

    private void AddSubagent(SubagentStartedEvent sub)
    {
        if (_agentNodes.ContainsKey(sub.SubagentId))
        {
            return;
        }

        var node = new AgentNode(sub.SubagentId, sub.Name, isMain: false, SelectAgent);
        var parent = sub.ParentAgentId is { } pid && _agentNodes.TryGetValue(pid, out var p) ? p : _mainAgentNode;
        parent.Children.Add(node);
        _agentNodes[sub.SubagentId] = node;
        Raise(nameof(HasSubagents));
        RaisePanels();
    }

    private void SelectAgent(string? agentId)
    {
        _selectedAgentId = agentId;
        _mainAgentNode.IsSelected = agentId is null;
        foreach (var node in _agentNodes.Values)
        {
            node.IsSelected = node.Id == agentId;
        }

        Raise(nameof(DisplayItems));
    }

    /// <summary>
    /// A shareable reference to this session for cross-device handoff. Any Agnes client can
    /// reconnect to the same live session by subscribing to (host, sessionId) — the event-sourced
    /// snapshot+tail replays full history, so a phone can pick up exactly where the desktop left off.
    /// </summary>
    public string HandoffReference => $"{_host.HostUrl}#{SessionId}";
    public ObservableCollection<TranscriptItem> Items => _transcript.Items;
    public PermissionItem? PendingPermission => _transcript.PendingPermission;

    // Left column
    public ObservableCollection<ToolEntry> ModifiedFiles { get; } = [];
    public ObservableCollection<ToolEntry> ToolActivity { get; } = [];

    /// <summary>Audit trail: every permission granted or denied this session.</summary>
    public ObservableCollection<PermissionAuditEntry> Approvals { get; } = [];

    public PlanItemView? Plan
    {
        get => _plan;
        private set { if (Set(ref _plan, value)) { RaisePanels(); } }
    }

    // Right column
    public PreviewViewModel? SelectedPreview
    {
        get => _selectedPreview;
        private set { if (Set(ref _selectedPreview, value)) { Raise(nameof(ShowRightPanel)); } }
    }

    public bool HasFiles => ModifiedFiles.Count > 0;
    public bool HasTools => ToolActivity.Count > 0;
    public bool HasApprovals => Approvals.Count > 0;
    public bool HasSidebarContent => Plan is not null || HasFiles || HasTools || HasApprovals || HasSubagents;
    public bool ShowLeftPanel => HasSidebarContent && !_leftHidden && !IsPreviewFullScreen;
    public bool ShowRightPanel => SelectedPreview is not null;

    public bool ToolsExpanded
    {
        get => _toolsExpanded;
        set => Set(ref _toolsExpanded, value);
    }

    public bool IsPreviewFullScreen
    {
        get => _previewFullScreen;
        set { if (Set(ref _previewFullScreen, value)) { Raise(nameof(ShowLeftPanel)); Raise(nameof(ShowChat)); } }
    }

    /// <summary>Chat (and left panel) are hidden while reviewing a preview full-screen.</summary>
    public bool ShowChat => !(IsPreviewFullScreen && ShowRightPanel);

    // Connection / session state
    public SessionBanner Banner
    {
        get => _banner;
        private set
        {
            if (Set(ref _banner, value))
            {
                Raise(nameof(ShowBanner));
                Raise(nameof(BannerText));
                Raise(nameof(CanRetry));
            }
        }
    }

    public bool ShowBanner => Banner != SessionBanner.None;
    public bool CanRetry => Banner is SessionBanner.Offline or SessionBanner.Interrupted or SessionBanner.Stale;

    /// <summary>Whether a turn is currently running — drives the Stop button and the "Running" state.</summary>
    public bool IsTurnActive
    {
        get => _turnActive;
        private set
        {
            if (Set(ref _turnActive, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
                RaiseActivity();
            }
        }
    }

    /// <summary>High-level session state, derived from what's in flight.</summary>
    public SessionActivity Activity =>
        _interrupted ? SessionActivity.Error
        : PendingPermission is not null ? SessionActivity.NeedsInput
        : IsTurnActive ? SessionActivity.Running
        : HasFiles ? SessionActivity.ReadyForReview
        : SessionActivity.Idle;

    /// <summary>Whether the session is waiting on the user (permission or error).</summary>
    public bool NeedsAttention => Activity is SessionActivity.NeedsInput or SessionActivity.Error;

    public string ActivityText => Activity switch
    {
        SessionActivity.Running => "Running",
        SessionActivity.NeedsInput => "Needs input",
        SessionActivity.ReadyForReview => "Ready for review",
        SessionActivity.Error => "Error",
        _ => "Idle",
    };

    private void RaiseActivity()
    {
        Raise(nameof(Activity));
        Raise(nameof(NeedsAttention));
        Raise(nameof(ActivityText));
    }

    /// <summary>
    /// Real token/cost usage the agent reported this session, or null until it reports any. Built
    /// only from <see cref="UsageReportedEvent"/> — never estimated, so it stays null (and the UI
    /// shows nothing) for agents that don't report usage.
    /// </summary>
    public UsageInfo? Usage
    {
        get => _usage;
        private set
        {
            if (Set(ref _usage, value))
            {
                Raise(nameof(UsageSummary));
            }
        }
    }

    /// <summary>A compact status caption (reported cost), or null when there's nothing real to show.</summary>
    public string? UsageSummary => _usage?.Summary;

    // Running usage figures, each updated only when the agent reports that field (partial events merge).
    private UsageInfo? _usage;
    private long? _ctxUsed;
    private long? _ctxWindow;
    private long? _outputTokens;
    private double? _costUsd;

    public string BannerText => Banner switch
    {
        SessionBanner.Offline => "Offline — the host is unreachable.",
        SessionBanner.Reconnecting => "Reconnecting…",
        SessionBanner.Interrupted => "The last turn was interrupted.",
        SessionBanner.Stale => "This session is stale and may be out of date.",
        _ => string.Empty,
    };

    // Raw event / debug inspector (the underlying SessionEvent log).
    public ObservableCollection<RawEventRow> RawEvents { get; } = [];

    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set => Set(ref _isInspectorOpen, value);
    }

    // Search within the session (deep-links each hit by anchor).
    public ObservableCollection<SearchHit> Matches { get; } = [];

    /// <summary>Raised with a transcript item's <c>AnchorId</c> when the view should scroll to it.</summary>
    public event Action<string>? ScrollToRequested;

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set => Set(ref _isSearchOpen, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set { if (Set(ref _searchQuery, value)) { RunSearch(); } }
    }

    public string MatchSummary => Matches.Count > 0
        ? $"{_matchCursor + 1} / {Matches.Count}"
        : string.IsNullOrWhiteSpace(SearchQuery) ? string.Empty : "No matches";

    public string PromptText
    {
        get => _promptText;
        set
        {
            if (Set(ref _promptText, value))
            {
                SendCommand.RaiseCanExecuteChanged();
                SendNowCommand.RaiseCanExecuteChanged();
                ScheduleCommand.RaiseCanExecuteChanged();
                _prompts.SaveDraft(SessionId, value);
                UpdateSlash();
            }
        }
    }

    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand SendNowCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public ICommand RemoveQueuedCommand { get; }
    public ICommand EditQueuedCommand { get; }
    public ICommand MoveQueuedUpCommand { get; }
    public ICommand MoveQueuedDownCommand { get; }
    public AsyncRelayCommand AllowCommand { get; }

    /// <summary>Prompts queued while a turn is running; sent in order as turns end.</summary>
    public ObservableCollection<QueuedPrompt> PendingPrompts { get; } = [];

    public bool HasQueue => PendingPrompts.Count > 0;

    // ---- composer context: attachments + slash commands ----

    /// <summary>Context attached to the next prompt (references / images), shown as chips.</summary>
    public ObservableCollection<PromptAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Slash-command suggestions matching what's typed after "/".</summary>
    public ObservableCollection<SlashCommand> SlashSuggestions { get; } = [];

    public bool ShowSlash
    {
        get => _showSlash;
        private set => Set(ref _showSlash, value);
    }

    public string ReferenceInput
    {
        get => _referenceInput;
        set => Set(ref _referenceInput, value);
    }

    public ICommand AddReferenceCommand { get; }
    public ICommand RemoveAttachmentCommand { get; }
    public ICommand ApplySlashCommand { get; }
    public ICommand SetModeCommand { get; }

    // ---- session modes (Ask / Code / …) ----

    /// <summary>Modes the agent offers for this session; empty if none.</summary>
    public ObservableCollection<SessionMode> Modes { get; } = [];

    public bool HasModes => Modes.Count > 0;

    public string? CurrentModeId
    {
        get => _currentModeId;
        private set { if (Set(ref _currentModeId, value)) { Raise(nameof(CurrentModeName)); } }
    }

    public string CurrentModeName => Modes.FirstOrDefault(m => m.Id == _currentModeId)?.Name ?? _currentModeId ?? string.Empty;

    private async Task SetModeAsync(SessionMode mode)
    {
        CurrentModeId = mode.Id; // optimistic; ModeChangedEvent will confirm
        await _host.SetModeAsync(SessionId, mode.Id);
    }

    // ---- conversation rewind (read-only history) ----

    public ICommand RewindToCommand { get; }
    public ICommand ResumeCommand { get; }
    public AsyncRelayCommand ScheduleCommand { get; }

    /// <summary>Schedules the composer's current prompt to run in the background every 5 minutes.</summary>
    private async Task ScheduleAsync()
    {
        var prompt = PromptText;
        if (string.IsNullOrWhiteSpace(prompt) || _view.Info is not { } info)
        {
            return;
        }

        PromptText = string.Empty;
        await _host.ScheduleTaskAsync(new ScheduleTaskRequest(info.AdapterId, info.WorkingDirectory, prompt, 300));
    }

    private void RewindTo(TranscriptItem? item)
    {
        if (item is null)
        {
            return;
        }

        var index = Items.IndexOf(item);
        if (index >= 0)
        {
            _rewindIndex = index;
            Raise(nameof(IsRewound));
            Raise(nameof(DisplayItems));
        }
    }

    private void Resume()
    {
        _rewindIndex = -1;
        Raise(nameof(IsRewound));
        Raise(nameof(DisplayItems));
    }

    // ---- git (host working directory) ----

    public ObservableCollection<GitFileChange> GitChanges { get; } = [];
    public ICommand RefreshGitCommand { get; }
    public AsyncRelayCommand CommitCommand { get; }

    public GitStatus? Git
    {
        get => _git;
        private set
        {
            if (Set(ref _git, value))
            {
                Raise(nameof(HasGit));
                Raise(nameof(GitBranch));
                Raise(nameof(GitDirty));
                Raise(nameof(GitSummary));
                CommitCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand PauseSandboxCommand { get; }
    public AsyncRelayCommand ResumeSandboxCommand { get; }
    public AsyncRelayCommand DeleteSandboxCommand { get; }

    public SandboxStatus? Sandbox
    {
        get => _sandbox;
        private set
        {
            if (Set(ref _sandbox, value))
            {
                Raise(nameof(HasSandbox));
                Raise(nameof(SandboxPaused));
                Raise(nameof(SandboxSummary));
                PauseSandboxCommand.RaiseCanExecuteChanged();
                ResumeSandboxCommand.RaiseCanExecuteChanged();
                DeleteSandboxCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether this session runs autonomously (tool calls without asking). Shown as a chip.</summary>
    public bool IsAutonomous => _view.Info?.SkipPermissions == true;

    public bool HasSandbox => _sandbox is not null;
    public bool SandboxPaused => string.Equals(_sandbox?.State, "Paused", StringComparison.OrdinalIgnoreCase);
    public string SandboxSummary => _sandbox is null ? string.Empty : $"🛡 sandbox · {_sandbox.State.ToLowerInvariant()}";

    private async Task PauseSandboxAsync()
    {
        await _host.PauseSandboxAsync(SessionId);
        await RefreshSandboxAsync();
    }

    private async Task ResumeSandboxAsync()
    {
        await _host.ResumeSandboxAsync(SessionId);
        await RefreshSandboxAsync();
    }

    private async Task DeleteSandboxAsync()
    {
        await _host.DeleteSandboxAsync(SessionId);
        _dispatcher.Post(() => Sandbox = null);
    }

    private async Task RefreshSandboxAsync()
    {
        var status = await _host.GetSandboxStatusAsync(SessionId);
        _dispatcher.Post(() => Sandbox = status);
    }

    public bool HasGit => _git?.IsRepository == true;
    public string GitBranch => _git?.Branch ?? string.Empty;
    public bool GitDirty => _git?.IsDirty == true;
    public string GitSummary => HasGit ? (GitDirty ? $"{GitChanges.Count} change(s)" : "clean") : string.Empty;

    public string CommitMessage
    {
        get => _commitMessage;
        set { if (Set(ref _commitMessage, value)) { CommitCommand.RaiseCanExecuteChanged(); } }
    }

    private async Task RefreshGitAsync()
    {
        var status = await _host.GetGitStatusAsync(SessionId);
        _dispatcher.Post(() =>
        {
            GitChanges.Clear();
            foreach (var change in status.Changes)
            {
                GitChanges.Add(change);
            }

            Git = status;
        });
    }

    private async Task CommitAsync()
    {
        var message = CommitMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await _host.GitCommitAsync(SessionId, message);
        _dispatcher.Post(() => CommitMessage = string.Empty);
        await RefreshGitAsync();
    }

    /// <summary>Attaches an arbitrary content block (e.g. an image from the shell's file picker).</summary>
    public void Attach(PromptAttachment attachment)
    {
        Attachments.Add(attachment);
        Raise(nameof(HasAttachments));
    }

    private void AddReference()
    {
        var reference = ReferenceInput.Trim().TrimStart('@');
        if (reference.Length > 0)
        {
            Attach(PromptAttachment.Reference(reference));
        }

        ReferenceInput = string.Empty;
    }

    private void ApplySlash(SlashCommand? command)
    {
        if (command is not null)
        {
            PromptText = command.Expansion;
        }
    }

    private void UpdateSlash()
    {
        SlashSuggestions.Clear();
        var text = PromptText;
        if (text.StartsWith('/') && !text.Contains('\n'))
        {
            var query = text[1..];
            foreach (var c in SlashCommand.BuiltIns.Where(c => c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            {
                SlashSuggestions.Add(c);
            }
        }

        ShowSlash = SlashSuggestions.Count > 0;
    }
    public AsyncRelayCommand DenyCommand { get; }
    public ICommand RespondWithCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public ICommand ToggleLeftCommand { get; }
    public ICommand ToggleToolsCommand { get; }
    public ICommand ToggleInspectorCommand { get; }
    public ICommand ToggleFullScreenCommand { get; }
    public ICommand RecallPreviousCommand { get; }
    public ICommand RecallNextCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand OpenSearchCommand { get; }
    public ICommand CloseSearchCommand { get; }
    public ICommand NextMatchCommand { get; }
    public ICommand PrevMatchCommand { get; }
    public ICommand SelectHitCommand { get; }
    public ICommand NextPromptCommand { get; }
    public ICommand PrevPromptCommand { get; }
    public ICommand NextChangeCommand { get; }
    public ICommand PrevChangeCommand { get; }
    public ICommand ClosePreviewCommand { get; }
    public ICommand ShowToolPreviewCommand { get; }
    public ICommand ShowFilePreviewCommand { get; }
    public ICommand ShowMessagePreviewCommand { get; }

    /// <summary>Marks the session stale (e.g. the host lost it after a reconnect).</summary>
    public void MarkStale()
    {
        _stale = true;
        UpdateBanner();
    }

    private void OnEvent(SessionEvent @event) => _dispatcher.Post(() => Apply(@event));

    private void Apply(SessionEvent @event)
    {
        _transcript.Apply(@event);
        UpdateSidebar(@event);
        RawEvents.Add(new RawEventRow(@event));

        switch (@event)
        {
            case PermissionRequestedEvent pr:
                _permissionTitles[pr.RequestId] = pr.Title;
                var toolKind = _transcript.PendingPermission?.ToolKind;
                if (_policy.Decide(_host.HostUrl, toolKind) is bool auto
                    && PickOption(pr.Options, auto) is { } chosen)
                {
                    // A standing trust rule answers this one; the resolution is still audited.
                    _ = _host.RespondPermissionAsync(SessionId, pr.RequestId, chosen.OptionId);
                    break;
                }

                NotificationRaised?.Invoke(new AppNotification("Permission needed", pr.Title, NotificationKind.Blocker, SessionId, pr.ToolCallId));
                break;

            case PermissionResolvedEvent rr:
                var title = _permissionTitles.TryGetValue(rr.RequestId, out var t) ? t : "Permission";
                Approvals.Insert(0, new PermissionAuditEntry(title, rr.Outcome, rr.OptionId, @event.Timestamp));
                Raise(nameof(HasApprovals));
                RaisePanels();
                break;

            case TurnEndedEvent { Reason: not StopReason.Cancelled }:
                IsTurnActive = false;
                NotificationRaised?.Invoke(new AppNotification($"{Title}: response ready", "The agent finished its turn.", NotificationKind.Completion, SessionId, Items.LastOrDefault()?.AnchorId));
                DrainQueue();
                _ = RefreshGitAsync(); // changes likely landed this turn
                break;

            case TurnEndedEvent:
                IsTurnActive = false;
                break;

            case AgentErrorEvent ae:
                IsTurnActive = false;
                _interrupted = true;
                UpdateBanner();
                NotificationRaised?.Invoke(new AppNotification("Agent error", ae.Message, NotificationKind.Error, SessionId, Items.LastOrDefault()?.AnchorId));
                break;

            case ModeChangedEvent mode:
                CurrentModeId = mode.ModeId;
                break;

            case UsageReportedEvent u:
                // Merge: each event may carry only some fields (context from a message, cost from the
                // result), so keep the last real value for any field this event didn't report.
                if (u.ContextTokens is not null) _ctxUsed = u.ContextTokens;
                if (u.ContextWindow is not null) _ctxWindow = u.ContextWindow;
                if (u.OutputTokens is not null) _outputTokens = u.OutputTokens;
                if (u.CostUsd is not null) _costUsd = u.CostUsd;
                Usage = new UsageInfo(_ctxUsed, _ctxWindow, _outputTokens, _costUsd);
                break;
        }

        // While filtered to a subagent, refresh the (snapshot) view as its events arrive.
        if (_selectedAgentId is not null)
        {
            Raise(nameof(DisplayItems));
        }
    }

    /// <summary>Raised when the session wants a notification surfaced (blocker / completion / error).</summary>
    public event Action<AppNotification>? NotificationRaised;

    private void OnHostStateChanged(AgnesConnectionState state) => _dispatcher.Post(UpdateBanner);

    private void UpdateBanner()
    {
        if (_stale)
        {
            Banner = SessionBanner.Stale;
            return;
        }

        Banner = _host.State switch
        {
            AgnesConnectionState.Disconnected => SessionBanner.Offline,
            AgnesConnectionState.Reconnecting => SessionBanner.Reconnecting,
            AgnesConnectionState.Connecting => SessionBanner.Reconnecting,
            _ => _interrupted ? SessionBanner.Interrupted : SessionBanner.None,
        };

        RaiseActivity();
    }

    private void DismissBanner()
    {
        _interrupted = false;
        _stale = false;
        UpdateBanner();
    }

    private async Task RetryAsync()
    {
        if (Banner == SessionBanner.Interrupted && !string.IsNullOrWhiteSpace(_lastPrompt))
        {
            _interrupted = false;
            UpdateBanner();
            await _host.PromptAsync(SessionId, [new TextContent(_lastPrompt)]);
            return;
        }

        try
        {
            await _host.ConnectAsync();
            _stale = false;
            UpdateBanner();
        }
        catch
        {
            // stay offline
        }
    }

    // ---- prompt history ----

    private void RecallPrevious()
    {
        if (_history.Count == 0 || _historyIndex == 0)
        {
            return;
        }

        _historyIndex--;
        PromptText = _history[_historyIndex];
    }

    private void RecallNext()
    {
        if (_historyIndex >= _history.Count - 1)
        {
            _historyIndex = _history.Count;
            PromptText = string.Empty;
            return;
        }

        _historyIndex++;
        PromptText = _history[_historyIndex];
    }

    private void UpdateSidebar(SessionEvent @event)
    {
        switch (@event)
        {
            case PlanEvent p:
                if (Plan is null)
                {
                    Plan = new PlanItemView { Entries = p.Entries };
                }
                else
                {
                    Plan.Entries = p.Entries;
                }

                break;

            case ToolCallEvent tc:
                var entry = new ToolEntry(tc.ToolCallId, tc.Title, tc.Kind.ToString(), tc.Status.ToString(), TextOf(tc.Content));
                _tools[tc.ToolCallId] = entry;
                (IsFileTool(tc.Kind) ? ModifiedFiles : ToolActivity).Add(entry);
                RaisePanels();
                break;

            case ToolCallUpdateEvent u when _tools.TryGetValue(u.ToolCallId, out var tracked):
                if (u.Status is { } status)
                {
                    tracked.StatusText = status.ToString();
                }

                if (u.Content is { } content && TextOf(content) is { Length: > 0 } text)
                {
                    tracked.Detail = text;
                }

                break;
        }
    }

    private void Preview(string title, string body) => SelectedPreview = new PreviewViewModel(title, body);

    // ---- search + deep-linking ----

    /// <summary>Finds every transcript item matching <paramref name="query"/> (case-insensitive).</summary>
    public IEnumerable<SearchHit> Find(string query, string? sessionTitle = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        foreach (var item in Items)
        {
            var (kind, text) = Describe(item);
            if (!string.IsNullOrEmpty(text) && text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                yield return new SearchHit(item.AnchorId, kind, SearchHit.Excerpt(text, query), sessionTitle);
            }
        }
    }

    /// <summary>Scrolls the transcript to a given anchor (deep-link target).</summary>
    public void ScrollTo(string anchorId) => ScrollToRequested?.Invoke(anchorId);

    private void RunSearch()
    {
        Matches.Clear();
        foreach (var hit in Find(SearchQuery))
        {
            Matches.Add(hit);
        }

        _matchCursor = Matches.Count > 0 ? 0 : -1;
        if (_matchCursor >= 0)
        {
            ScrollToRequested?.Invoke(Matches[0].AnchorId);
        }

        Raise(nameof(MatchSummary));
    }

    private void StepMatch(int direction)
    {
        if (Matches.Count == 0)
        {
            return;
        }

        _matchCursor = ((_matchCursor + direction) % Matches.Count + Matches.Count) % Matches.Count;
        ScrollToRequested?.Invoke(Matches[_matchCursor].AnchorId);
        Raise(nameof(MatchSummary));
    }

    private void SelectHit(SearchHit? hit)
    {
        if (hit is null)
        {
            return;
        }

        var index = Matches.IndexOf(hit);
        if (index >= 0)
        {
            _matchCursor = index;
            Raise(nameof(MatchSummary));
        }

        ScrollToRequested?.Invoke(hit.AnchorId);
    }

    private void NavigateKind(Func<TranscriptItem, bool> predicate, int direction, ref int cursor)
    {
        var anchors = Items.Where(predicate).Select(i => i.AnchorId).ToList();
        if (anchors.Count == 0)
        {
            return;
        }

        cursor = cursor < 0 && direction < 0
            ? anchors.Count - 1
            : ((cursor + direction) % anchors.Count + anchors.Count) % anchors.Count;
        ScrollToRequested?.Invoke(anchors[cursor]);
    }

    private static bool IsPrompt(TranscriptItem item) => item is MessageBubbleItem { IsUser: true };
    private static bool IsChange(TranscriptItem item) => item is ToolCallItem;

    private static (string Kind, string Text) Describe(TranscriptItem item) => item switch
    {
        MessageBubbleItem m => (m.Speaker, m.Text),
        ToolCallItem t => (t.Kind.ToString(), t.Header + " " + t.Detail),
        NoticeItem n => ("Notice", n.Text),
        PlanItemView p => ("Plan", string.Join(" ", p.Entries.Select(e => e.Content))),
        PermissionItem pm => ("Permission", pm.Title),
        _ => (string.Empty, string.Empty),
    };

    private void RaisePanels()
    {
        Raise(nameof(HasFiles));
        Raise(nameof(HasTools));
        Raise(nameof(HasSidebarContent));
        Raise(nameof(ShowLeftPanel));
        RaiseActivity();
    }

    private static bool IsFileTool(ToolKind kind)
        => kind is ToolKind.Edit or ToolKind.Delete or ToolKind.Move;

    private static string TextOf(IReadOnlyList<ContentBlock> content)
        => string.Concat(content.Select(b => b switch
        {
            TextContent t => t.Text,
            DiffContent d => Diff.UnifiedDiff.Format(d.Path, d.OldText, d.NewText),
            _ => string.Empty,
        }));

    // Enter / Send: while a turn is running this QUEUES; when idle it sends immediately.
    private async Task SendAsync()
    {
        var text = PromptText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Record(text);
        PromptText = string.Empty;

        if (IsTurnActive)
        {
            PendingPrompts.Add(new QueuedPrompt(text));
            Raise(nameof(HasQueue));
            return;
        }

        await SubmitAsync(text);
    }

    // Steer: stop the current turn and send this prompt now (skips the queue).
    private async Task SendNowAsync()
    {
        var text = PromptText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Record(text);
        PromptText = string.Empty;

        if (IsTurnActive)
        {
            await _host.CancelAsync(SessionId);
        }

        await SubmitAsync(text);
    }

    private void Record(string text)
    {
        _history.Add(text);
        _prompts.AppendHistory(SessionId, text);
        _historyIndex = _history.Count;
        _lastPrompt = text;
    }

    private async Task SubmitAsync(string text)
    {
        _interrupted = false;
        UpdateBanner();
        IsTurnActive = true;

        var blocks = new List<ContentBlock>(Attachments.Count + 1);
        blocks.AddRange(Attachments.Select(a => a.Content));
        blocks.Add(new TextContent(text));
        if (Attachments.Count > 0)
        {
            Attachments.Clear();
            Raise(nameof(HasAttachments));
        }

        await _host.PromptAsync(SessionId, blocks);
    }

    // When a turn ends normally, send the next queued prompt.
    private void DrainQueue()
    {
        if (IsTurnActive || PendingPrompts.Count == 0)
        {
            return;
        }

        var next = PendingPrompts[0];
        PendingPrompts.RemoveAt(0);
        Raise(nameof(HasQueue));
        _ = SubmitAsync(next.Text);
    }

    private async Task CancelAsync()
    {
        IsTurnActive = false;
        await _host.CancelAsync(SessionId);
    }

    private void RemoveQueued(QueuedPrompt? item)
    {
        if (item is not null && PendingPrompts.Remove(item))
        {
            Raise(nameof(HasQueue));
        }
    }

    private void EditQueued(QueuedPrompt? item)
    {
        if (item is null || !PendingPrompts.Remove(item))
        {
            return;
        }

        // Put it back in the composer to edit; anything already typed is prepended back onto the queue.
        if (!string.IsNullOrWhiteSpace(PromptText))
        {
            PendingPrompts.Insert(0, new QueuedPrompt(PromptText));
        }

        PromptText = item.Text;
        Raise(nameof(HasQueue));
    }

    private void MoveQueued(QueuedPrompt? item, int direction)
    {
        if (item is null)
        {
            return;
        }

        var i = PendingPrompts.IndexOf(item);
        var j = i + direction;
        if (i >= 0 && j >= 0 && j < PendingPrompts.Count)
        {
            PendingPrompts.Move(i, j);
        }
    }

    private async Task RespondAsync(bool allow)
    {
        if (PendingPermission is not { } permission)
        {
            return;
        }

        var option = PickOption(permission.Options, allow) ?? permission.Options[0];
        await _host.RespondPermissionAsync(SessionId, permission.RequestId, option.OptionId);
    }

    // Respond with a specific option; "always" options also record a standing trust rule.
    private async Task RespondWithAsync(PermissionOption option)
    {
        if (PendingPermission is not { } permission)
        {
            return;
        }

        if (option.Kind == PermissionOptionKind.AllowAlways)
        {
            _policy.Remember(_host.HostUrl, permission.ToolKind, allow: true);
        }
        else if (option.Kind == PermissionOptionKind.RejectAlways)
        {
            _policy.Remember(_host.HostUrl, permission.ToolKind, allow: false);
        }

        await _host.RespondPermissionAsync(SessionId, permission.RequestId, option.OptionId);
    }

    // Narrowest matching option: prefer "once" over "always".
    private static PermissionOption? PickOption(IReadOnlyList<PermissionOption> options, bool allow)
        => options.FirstOrDefault(o => IsAllow(o.Kind) == allow && IsOnce(o.Kind))
           ?? options.FirstOrDefault(o => IsAllow(o.Kind) == allow);

    private static bool IsOnce(PermissionOptionKind kind)
        => kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.RejectOnce;

    private static bool IsAllow(PermissionOptionKind kind)
        => kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways;
}
