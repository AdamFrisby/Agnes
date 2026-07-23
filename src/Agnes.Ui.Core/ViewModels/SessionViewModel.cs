using System.Collections.ObjectModel;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly Agnes.Abstractions.Events.IEventBus _bus;
    private readonly SessionView _view;
    private readonly IUiDispatcher _dispatcher;
    private readonly IPromptStore _prompts;
    private readonly IPermissionPolicy _policy;

    // The host's prompt library, loaded lazily so typing a template's slash token (e.g. /review) expands it.
    private IReadOnlyList<Agnes.Abstractions.LibraryPrompt> _libraryPrompts = [];
    private IReadOnlyList<Agnes.Abstractions.PromptTemplate> _promptTemplates = [];
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
    private GitStashInfo? _lastStash;
    private string _targetBranch = string.Empty;
    private bool _carryStash = true;
    private string _gitOpMessage = string.Empty;
    private bool _gitOpFailed;
    private int _matchCursor = -1;
    private int _promptCursor = -1;
    private int _changeCursor = -1;
    private bool _showDiscarded;
    private SendPolicy _sendPolicy = SendPolicy.QueueInAgent;

    public SessionViewModel(IAgnesHost host, SessionView view, IUiDispatcher dispatcher, string title, IPromptStore? prompts = null, IPermissionPolicy? policy = null, Agnes.Abstractions.Events.IEventBus? eventBus = null)
    {
        _host = host;
        _view = view;
        _dispatcher = dispatcher;
        _prompts = prompts ?? NullPromptStore.Instance;
        _policy = policy ?? NullPermissionPolicy.Instance;
        _bus = eventBus ?? new Agnes.Abstractions.Events.EventBus();
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
        HostSendNowCommand = new RelayCommand<PendingMessageView>(p => { if (p is not null) { _ = _host.SendPendingNowAsync(SessionId, p.Id); } });
        HostRemoveCommand = new RelayCommand<PendingMessageView>(p => { if (p is not null) { _ = _host.RemovePendingMessageAsync(SessionId, p.Id); } });
        HostMoveUpCommand = new RelayCommand<PendingMessageView>(p => MoveHostPending(p, -1));
        HostMoveDownCommand = new RelayCommand<PendingMessageView>(p => MoveHostPending(p, +1));
        ToggleDiscardedCommand = new RelayCommand(() => ShowDiscarded = !ShowDiscarded);
        AllowCommand = new AsyncRelayCommand(() => RespondAsync(allow: true));
        DenyCommand = new AsyncRelayCommand(() => RespondAsync(allow: false));
        RespondWithCommand = new RelayCommand<PermissionOption>(o => { if (o is not null) { _ = RespondWithAsync(o); } });
        ToggleLeftCommand = new RelayCommand(() => { _leftHidden = !_leftHidden; OnPropertyChanged(nameof(ShowLeftPanel)); });
        ToggleToolsCommand = new RelayCommand(() => ToolsExpanded = !ToolsExpanded);
        ToggleFilesCommand = new RelayCommand(() => FilesExpanded = !FilesExpanded);
        ToggleToolsListCommand = new RelayCommand(() => ToolsListExpanded = !ToolsListExpanded);
        ShowAllToolsCommand = new RelayCommand(() => ShowAllTools = true);
        CompactCommand = new RelayCommand(() => SendControl("/compact"));
        ClearContextCommand = new RelayCommand(() => SendControl("/clear"));
        RestartAgentCommand = new RelayCommand(() => { _ = RestartAgentAsync(); });
        // The tools panel shows only the last N until "show all"; keep the view + label in sync.
        ToolActivity.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(VisibleToolActivity));
            OnPropertyChanged(nameof(HasMoreTools));
            OnPropertyChanged(nameof(MoreToolsLabel));
        };
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorOpen = !IsInspectorOpen);
        SetModeCommand = new RelayCommand<SessionMode>(m => { if (m is not null) { _ = SetModeAsync(m); } });
        RefreshGitCommand = new AsyncRelayCommand(RefreshGitAsync);
        RewindToCommand = new RelayCommand<TranscriptItem>(RewindTo);
        ResumeCommand = new RelayCommand(Resume);
        ScheduleCommand = new AsyncRelayCommand(ScheduleAsync, () => !string.IsNullOrWhiteSpace(PromptText) && _view.Info is not null);
        CommitCommand = new AsyncRelayCommand(CommitAsync, () => !string.IsNullOrWhiteSpace(CommitMessage) && GitDirty);
        StashCommand = new AsyncRelayCommand(StashAsync, () => GitDirty);
        PopStashCommand = new AsyncRelayCommand(PopStashAsync, () => HasStash);
        SwitchBranchCommand = new AsyncRelayCommand(SwitchBranchAsync, () => !string.IsNullOrWhiteSpace(TargetBranch));
        PullCommand = new AsyncRelayCommand(PullAsync);
        PushCommand = new AsyncRelayCommand(() => PushAsync(false));
        PublishBranchCommand = new AsyncRelayCommand(() => PushAsync(true));
        RefreshPullRequestsCommand = new AsyncRelayCommand(RefreshPullRequestsAsync);
        CheckoutPullRequestCommand = new AsyncRelayCommand<PullRequestInfo>(pr => pr is null ? Task.CompletedTask : CheckoutPullRequestAsync(pr));
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
        RemoveAttachmentCommand = new RelayCommand<PromptAttachment>(a => { if (a is not null) { Attachments.Remove(a); OnPropertyChanged(nameof(HasAttachments)); } });
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
                Preview($"{m.Speaker} message", m.Text, markdown: true);
            }
        });

        _transcript.PendingPermissionChanged += () => { OnPropertyChanged(nameof(PendingPermission)); RaiseActivity(); };
        _transcript.PendingQuestionChanged += () => { OnPropertyChanged(nameof(PendingQuestion)); RaiseActivity(); };
        AnswerQuestionCommand = new RelayCommand<QuestionItem>(item => { _ = AnswerQuestionAsync(item); });
        DismissQuestionCommand = new RelayCommand<QuestionItem>(item => { _ = DismissQuestionAsync(item); });

        _mainAgentNode = new AgentNode(null, title, isMain: true, SelectAgent) { IsSelected = true };
        AgentTree.Add(_mainAgentNode);
        Subagents = new SubagentsPanelViewModel(title);
        _transcript.SubagentAdded += AddSubagent;
        // The roster (participant panel) consumes the same SubagentAdded pipeline, de-duped independently.
        _transcript.SubagentAdded += Subagents.Add;
        // The ListBox observes Items directly, but the empty-state flag is a computed bool — keep it in
        // sync with the collection so "No messages yet" disappears the moment the first item arrives.
        _transcript.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsTranscriptEmpty));

        foreach (var @event in _view.Events)
        {
            Apply(@event);
        }

        _view.EventAppended += OnEvent;
        _host.StateChanged += OnHostStateChanged;
        _host.ReadStateChanged += OnReadStateChanged;
        UpdateBanner();
        _ = RefreshGitAsync();

        // Review comments are project-scoped (durable across sessions); load the ones left on this session's
        // project, and keep their staleness anchors fresh as the modified-file diffs change underneath them.
        ReviewComments = new ReviewCommentsViewModel(host, SessionId, view.Info?.Project, _dispatcher);
        ReviewComments.JumpRequested += OnReviewJumpRequested;
        ReviewComments.Files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasReviewComments));
            OnPropertyChanged(nameof(HasSidebarContent));
            OnPropertyChanged(nameof(ShowLeftPanel));
        };
        ModifiedFiles.CollectionChanged += (_, _) => SyncReviewDiffs();
        _ = ReviewComments.LoadAsync();
        _ = LoadPromptTemplatesAsync();
    }

    /// <summary>
    /// Loads the host's saved prompts + slash-token templates so typing a template's token in the composer
    /// expands it. Best-effort: a host without a library simply has none, so failures are swallowed.
    /// </summary>
    private async Task LoadPromptTemplatesAsync()
    {
        try
        {
            var prompts = await _host.GetPromptsAsync().ConfigureAwait(false);
            var templates = await _host.GetPromptTemplatesAsync().ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                _libraryPrompts = prompts;
                _promptTemplates = templates;
                UpdateSlash();
            });
        }
        catch
        {
            // Templates are a convenience; a host without a prompt library just offers none.
        }
    }

    /// <summary>The project-scoped review comments surfaced for this session (grouped by file).</summary>
    public ReviewCommentsViewModel ReviewComments { get; }

    /// <summary>Whether the review panel has any comments (drives its sidebar visibility).</summary>
    public bool HasReviewComments => ReviewComments.HasComments;

    // Feed the current modified-file diffs to the review VM so it can hash new comments' anchor lines and
    // recompute staleness of existing ones.
    private void SyncReviewDiffs()
        => ReviewComments.UpdateDiffs(ModifiedFiles.Select(f => (f.Name, f.Detail)));

    // Jump: reveal the commented file's diff in the preview pane.
    private void OnReviewJumpRequested(Abstractions.ReviewComment comment)
    {
        var entry = ModifiedFiles.FirstOrDefault(f => f.Name == comment.FilePath);
        if (entry is not null)
        {
            Preview(entry.Name, entry.Detail);
        }
    }

    // ---- read / unread state (sessions/05) ----

    private long _readCursor;
    private bool _stickyUnread;
    private bool _isActive;

    /// <summary>Whether this session has activity the user hasn't seen. Empty and currently-active sessions
    /// never show unread. Synced across the user's devices via the host's read cursor.</summary>
    public bool IsUnread => !_isActive && _view.LastSequence > 0 && (_stickyUnread || _view.LastSequence > _readCursor);

    private void OnReadStateChanged(string sessionId, long readSequence, bool stickyUnread)
    {
        if (sessionId != SessionId)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            _readCursor = readSequence;
            _stickyUnread = stickyUnread;
            OnPropertyChanged(nameof(IsUnread));
        });
    }

    /// <summary>Called by the shell when this session's tab is focused/unfocused. Focusing marks it read.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
        {
            _ = _host.MarkSessionReadAsync(SessionId, _view.LastSequence);
        }

        OnPropertyChanged(nameof(IsUnread));
    }

    /// <summary>Marks this session unread (sticky — stays unread while open until the next focus).</summary>
    public void MarkUnread() => _ = _host.MarkSessionUnreadAsync(SessionId);

    public string Title { get; }
    public string SessionId => _view.SessionId;

    private string? _agentTitle;

    /// <summary>The agent's auto-generated name for the conversation (Claude's on-disk aiTitle), prettified;
    /// null until the agent produces one. Used to name the session in the left panel and the tab.</summary>
    public string? AgentTitle
    {
        get => _agentTitle;
        private set
        {
            if (SetProperty(ref _agentTitle, value))
            {
                OnPropertyChanged(nameof(HasAgentTitle));
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(HasSidebarContent));
                OnPropertyChanged(nameof(ShowLeftPanel));
            }
        }
    }

    public bool HasAgentTitle => !string.IsNullOrWhiteSpace(_agentTitle);

    /// <summary>The best available session name: the agent's title if it has produced one, else the
    /// folder-derived title. Shown at the top of the left panel.</summary>
    public string DisplayTitle => HasAgentTitle ? _agentTitle! : Title;

    /// <summary>Turns Claude's kebab-case aiTitle ("agnes-structured-questions") into a readable label
    /// ("Agnes structured questions").</summary>
    private static string PrettifyTitle(string raw)
    {
        var spaced = raw.Replace('-', ' ').Replace('_', ' ').Trim();
        while (spaced.Contains("  ", StringComparison.Ordinal))
        {
            spaced = spaced.Replace("  ", " ");
        }

        return spaced.Length == 0 ? raw : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

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

    /// <summary>True when the transcript has nothing to show yet, so the view can offer a "start" cue
    /// instead of a blank pane (defect #10). Kept in sync wherever <see cref="DisplayItems"/> is raised.</summary>
    public bool IsTranscriptEmpty => !DisplayItems.Any();

    // ---- agent / subagent tree ----

    /// <summary>The session's agents: the main agent with any subagents nested beneath it.</summary>
    public ObservableCollection<AgentNode> AgentTree { get; } = [];

    /// <summary>The participant roster (sessions/04): the lead agent plus each reported subagent, with a
    /// controllable-vs-observe-only flag. Fed from the same SubagentAdded pipeline as <see cref="AgentTree"/>.</summary>
    public SubagentsPanelViewModel Subagents { get; }

    public bool HasSubagents => _mainAgentNode.Children.Count > 0;

    // ---- embedded terminal (platform/03) ----
    // Created once per session (keyed by this SessionView), so its scrollback + dock location persist for the
    // session's lifetime and a dock move never reconnects the PTY. Lazily built so a session with no terminal
    // pays nothing.
    private TerminalPanelViewModel? _terminal;
    private ICommand? _toggleTerminal;
    private bool _isTerminalVisible;

    /// <summary>The session's embedded CLI-fallback terminal (platform/03), built on first use.</summary>
    public TerminalPanelViewModel Terminal => _terminal ??= new TerminalPanelViewModel(_host, _view, _dispatcher);

    /// <summary>Whether the terminal panel is shown for this session (show/hide; on phones the shell hides it
    /// when the host can't back a terminal). Toggling it never tears down the terminal.</summary>
    public bool IsTerminalVisible
    {
        get => _isTerminalVisible;
        set
        {
            if (SetProperty(ref _isTerminalVisible, value))
            {
                Terminal.IsVisible = value;
            }
        }
    }

    /// <summary>Shows/hides the terminal panel.</summary>
    public ICommand ToggleTerminalCommand => _toggleTerminal ??= new RelayCommand(() => IsTerminalVisible = !IsTerminalVisible);

    // ---- file browser (git-and-files/03) ----
    // Built once per session (keyed by this SessionView) and lazily, so a session that never opens the
    // browser pays nothing. Toggling visibility never discards its navigation state.
    private FileBrowserViewModel? _fileBrowser;
    private ICommand? _toggleFileBrowser;
    private bool _isFileBrowserVisible;

    /// <summary>The session's workspace file browser (git-and-files/03), built on first use.</summary>
    public FileBrowserViewModel FileBrowser => _fileBrowser ??= new FileBrowserViewModel(_host, _view.SessionId, _dispatcher);

    /// <summary>Whether the file-browser panel is shown for this session. Toggling it on lists the root the
    /// first time; toggling off keeps the browser's navigation state.</summary>
    public bool IsFileBrowserVisible
    {
        get => _isFileBrowserVisible;
        set
        {
            if (SetProperty(ref _isFileBrowserVisible, value))
            {
                FileBrowser.IsVisible = value;
                if (value && FileBrowser.Entries.Count == 0)
                {
                    _ = FileBrowser.RefreshAsync();
                }
            }
        }
    }

    /// <summary>Shows/hides the file-browser panel.</summary>
    public ICommand ToggleFileBrowserCommand => _toggleFileBrowser ??= new RelayCommand(() => IsFileBrowserVisible = !IsFileBrowserVisible);

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
        OnPropertyChanged(nameof(HasSubagents));
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

        OnPropertyChanged(nameof(DisplayItems));
        OnPropertyChanged(nameof(IsTranscriptEmpty));
        ScrollToBottomRequested?.Invoke(); // switching conversations lands at the latest, not the top
    }

    /// <summary>Raised when the view should jump to the bottom of the transcript (agent/tab switch, open).</summary>
    public event Action? ScrollToBottomRequested;

    /// <summary>
    /// A shareable reference to this session for cross-device handoff. Any Agnes client can
    /// reconnect to the same live session by subscribing to (host, sessionId) — the event-sourced
    /// snapshot+tail replays full history, so a phone can pick up exactly where the desktop left off.
    /// </summary>
    public string HandoffReference => $"{_host.HostUrl}#{SessionId}";
    public ObservableCollection<TranscriptItem> Items => _transcript.Items;
    public PermissionItem? PendingPermission => _transcript.PendingPermission;
    public QuestionItem? PendingQuestion => _transcript.PendingQuestion;

    public System.Windows.Input.ICommand AnswerQuestionCommand { get; }
    public System.Windows.Input.ICommand DismissQuestionCommand { get; }

    private async Task AnswerQuestionAsync(QuestionItem? item)
    {
        if (item is not null)
        {
            await _host.AnswerQuestionAsync(SessionId, item.RequestId, item.BuildAnswers());
        }
    }

    private async Task DismissQuestionAsync(QuestionItem? item)
    {
        if (item is not null)
        {
            await _host.AnswerQuestionAsync(SessionId, item.RequestId, []);
        }
    }

    // Left column
    public ObservableCollection<ToolEntry> ModifiedFiles { get; } = [];
    public ObservableCollection<ToolEntry> ToolActivity { get; } = [];

    /// <summary>Audit trail: every permission granted or denied this session.</summary>
    public ObservableCollection<PermissionAuditEntry> Approvals { get; } = [];

    /// <summary>Audit trail: forwarded MCP tool calls a sandboxed agent made against host servers.</summary>
    public ObservableCollection<McpCallEntry> McpCalls { get; } = [];

    public PlanItemView? Plan
    {
        get => _plan;
        private set { if (SetProperty(ref _plan, value)) { RaisePanels(); } }
    }

    // Right column
    public PreviewViewModel? SelectedPreview
    {
        get => _selectedPreview;
        private set { if (SetProperty(ref _selectedPreview, value)) { OnPropertyChanged(nameof(ShowRightPanel)); } }
    }

    public bool HasFiles => ModifiedFiles.Count > 0;
    public bool HasTools => ToolActivity.Count > 0;

    // ---- left-panel collapse + the tools "show more" cap ----
    private const int ToolDisplayLimit = 50;

    private bool _showAllTools;
    public bool ShowAllTools
    {
        get => _showAllTools;
        set { if (SetProperty(ref _showAllTools, value)) { OnPropertyChanged(nameof(VisibleToolActivity)); OnPropertyChanged(nameof(HasMoreTools)); } }
    }

    /// <summary>The tool calls to show — the most recent <see cref="ToolDisplayLimit"/> until "show all".</summary>
    public IEnumerable<ToolEntry> VisibleToolActivity
        => ShowAllTools || ToolActivity.Count <= ToolDisplayLimit
            ? ToolActivity
            : ToolActivity.Skip(ToolActivity.Count - ToolDisplayLimit);

    public bool HasMoreTools => !ShowAllTools && ToolActivity.Count > ToolDisplayLimit;
    public string MoreToolsLabel => $"Show all {ToolActivity.Count}";

    private bool _filesExpanded = true;
    public bool FilesExpanded { get => _filesExpanded; set => SetProperty(ref _filesExpanded, value); }

    private bool _toolsListExpanded = true;
    public bool ToolsListExpanded { get => _toolsListExpanded; set => SetProperty(ref _toolsListExpanded, value); }

    public System.Windows.Input.ICommand ToggleFilesCommand { get; }
    public System.Windows.Input.ICommand ToggleToolsListCommand { get; }
    public System.Windows.Input.ICommand ShowAllToolsCommand { get; }

    /// <summary>Ask the agent to compact / clear its context. Sent as a control command the agent
    /// interprets (Claude honours /compact and /clear); harmless text otherwise.</summary>
    public System.Windows.Input.ICommand CompactCommand { get; }
    public System.Windows.Input.ICommand ClearContextCommand { get; }

    /// <summary>Restart the agent process (relaunch + resume) — recovers a crashed/hung agent, and the
    /// manual fallback after the host paused auto-restart on a crash loop.</summary>
    public System.Windows.Input.ICommand RestartAgentCommand { get; }

    private async Task RestartAgentAsync()
    {
        try
        {
            await _host.RestartAgentAsync(SessionId);
        }
        catch
        {
            // The host emits a NoticeEvent on failure; nothing to do here.
        }
    }

    private void SendControl(string command)
    {
        PromptText = command;
        if (SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
    }
    public bool HasApprovals => Approvals.Count > 0;
    public bool HasMcpCalls => McpCalls.Count > 0;

    /// <summary>Audit trail of brokered git-credential grants/denials for this session.</summary>
    public ObservableCollection<CredentialUseEntry> CredentialUses { get; } = [];
    public bool HasCredentialUses => CredentialUses.Count > 0;

    public bool HasSidebarContent => HasAgentTitle || Plan is not null || HasFiles || HasTools || HasApprovals || HasMcpCalls || HasCredentialUses || HasSubagents || HasReviewComments;
    public bool ShowLeftPanel => HasSidebarContent && !_leftHidden && !IsPreviewFullScreen;
    public bool ShowRightPanel => SelectedPreview is not null;

    public bool ToolsExpanded
    {
        get => _toolsExpanded;
        set => SetProperty(ref _toolsExpanded, value);
    }

    public bool IsPreviewFullScreen
    {
        get => _previewFullScreen;
        set { if (SetProperty(ref _previewFullScreen, value)) { OnPropertyChanged(nameof(ShowLeftPanel)); OnPropertyChanged(nameof(ShowChat)); } }
    }

    /// <summary>Chat (and left panel) are hidden while reviewing a preview full-screen.</summary>
    public bool ShowChat => !(IsPreviewFullScreen && ShowRightPanel);

    // Connection / session state
    public SessionBanner Banner
    {
        get => _banner;
        private set
        {
            if (SetProperty(ref _banner, value))
            {
                OnPropertyChanged(nameof(ShowBanner));
                OnPropertyChanged(nameof(BannerText));
                OnPropertyChanged(nameof(CanRetry));
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
            if (SetProperty(ref _turnActive, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
                RaiseActivity();
                OnPropertyChanged(nameof(SendGestureHint));
            }
        }
    }

    /// <summary>
    /// Composer hint that spells out what Ctrl+Enter does right now — it sends when idle but QUEUES while a
    /// turn is running, so the same gesture must not silently mean two different things.
    /// </summary>
    public string SendGestureHint => IsTurnActive
        ? "Ctrl+Enter queues after this turn · Ctrl+Shift+Enter sends now"
        : "Ctrl+Enter to send";

    /// <summary>High-level session state, derived from what's in flight.</summary>
    public SessionActivity Activity =>
        _interrupted ? SessionActivity.Error
        : PendingPermission is not null || PendingQuestion is not null ? SessionActivity.NeedsInput
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
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(ActivityText));
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
            if (SetProperty(ref _usage, value))
            {
                OnPropertyChanged(nameof(UsageSummary));
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
        set => SetProperty(ref _isInspectorOpen, value);
    }

    // Search within the session (deep-links each hit by anchor).
    public ObservableCollection<SearchHit> Matches { get; } = [];

    /// <summary>Raised with a transcript item's <c>AnchorId</c> when the view should scroll to it.</summary>
    public event Action<string>? ScrollToRequested;

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set => SetProperty(ref _isSearchOpen, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set { if (SetProperty(ref _searchQuery, value)) { RunSearch(); } }
    }

    public string MatchSummary => Matches.Count > 0
        ? $"{_matchCursor + 1} / {Matches.Count}"
        : string.IsNullOrWhiteSpace(SearchQuery) ? string.Empty : "No matches";

    public string PromptText
    {
        get => _promptText;
        set
        {
            if (SetProperty(ref _promptText, value))
            {
                SendCommand.NotifyCanExecuteChanged();
                SendNowCommand.NotifyCanExecuteChanged();
                ScheduleCommand.NotifyCanExecuteChanged();
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

    // ---- host-side pending queue (sessions/03) ----
    // The authoritative, multi-client queue owned host-side. These mirror the latest PendingQueueEvent
    // snapshot, so every client on the session sees the same queued/discarded messages in the same order.

    public ICommand HostSendNowCommand { get; }
    public ICommand HostRemoveCommand { get; }
    public ICommand HostMoveUpCommand { get; }
    public ICommand HostMoveDownCommand { get; }
    public ICommand ToggleDiscardedCommand { get; }

    /// <summary>The session's host-side pending queue (in send order), synced from PendingQueueEvent.</summary>
    public ObservableCollection<PendingMessageView> HostPending { get; } = [];

    /// <summary>Messages that could no longer be delivered (e.g. the session was torn down before the queue
    /// drained) — kept visible and copyable rather than silently dropped.</summary>
    public ObservableCollection<PendingMessageView> DiscardedMessages { get; } = [];

    public bool HasHostPending => HostPending.Count > 0;
    public bool HasDiscarded => DiscardedMessages.Count > 0;

    public bool ShowDiscarded
    {
        get => _showDiscarded;
        set => SetProperty(ref _showDiscarded, value);
    }

    /// <summary>The send policies offered by the composer's policy selector.</summary>
    public IReadOnlyList<SendPolicy> SendPolicies { get; } = [SendPolicy.QueueInAgent, SendPolicy.InterruptAndSend, SendPolicy.PendingUntilReady];

    /// <summary>What a send does while a turn is active. Setting it pushes the choice to the host (a
    /// per-session setting); the default is <see cref="SendPolicy.QueueInAgent"/>.</summary>
    public SendPolicy SendPolicy
    {
        get => _sendPolicy;
        set
        {
            if (SetProperty(ref _sendPolicy, value))
            {
                _ = _host.SetSendPolicyAsync(SessionId, value);
                OnPropertyChanged(nameof(SendGestureHint));
            }
        }
    }

    // Rebuilds the host queue + discarded projections from a snapshot, then refreshes the derived flags.
    private void ApplyPendingQueue(PendingQueueEvent snapshot)
    {
        HostPending.Clear();
        foreach (var m in snapshot.Queue)
        {
            HostPending.Add(new PendingMessageView(m.Id, PreviewText(m.Content)));
        }

        DiscardedMessages.Clear();
        foreach (var m in snapshot.Discarded)
        {
            DiscardedMessages.Add(new PendingMessageView(m.Id, PreviewText(m.Content)));
        }

        OnPropertyChanged(nameof(HasHostPending));
        OnPropertyChanged(nameof(HasDiscarded));
    }

    private static string PreviewText(IReadOnlyList<ContentBlock> content)
        => string.Concat(content.Select(b => b switch
        {
            TextContent t => t.Text,
            DiffContent d => d.Path,
            ResourceLinkContent r => r.Name ?? r.Uri,
            _ => string.Empty,
        }));

    // Reorder a host-queue entry by asking the host to move it; the resulting PendingQueueEvent re-syncs
    // every client's view (we don't mutate the local collection optimistically — the host is authoritative).
    private void MoveHostPending(PendingMessageView? item, int direction)
    {
        if (item is null)
        {
            return;
        }

        var index = HostPending.IndexOf(item);
        var target = index + direction;
        if (index >= 0 && target >= 0 && target < HostPending.Count)
        {
            _ = _host.ReorderPendingMessageAsync(SessionId, item.Id, target);
        }
    }

    // ---- composer context: attachments + slash commands ----

    /// <summary>Context attached to the next prompt (references / images), shown as chips.</summary>
    public ObservableCollection<PromptAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Slash-command suggestions matching what's typed after "/".</summary>
    public ObservableCollection<SlashCommand> SlashSuggestions { get; } = [];

    public bool ShowSlash
    {
        get => _showSlash;
        private set => SetProperty(ref _showSlash, value);
    }

    public string ReferenceInput
    {
        get => _referenceInput;
        set => SetProperty(ref _referenceInput, value);
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
        private set { if (SetProperty(ref _currentModeId, value)) { OnPropertyChanged(nameof(CurrentModeName)); } }
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
            OnPropertyChanged(nameof(IsRewound));
            OnPropertyChanged(nameof(DisplayItems));
            OnPropertyChanged(nameof(IsTranscriptEmpty));
        }
    }

    private void Resume()
    {
        _rewindIndex = -1;
        OnPropertyChanged(nameof(IsRewound));
        OnPropertyChanged(nameof(DisplayItems));
        OnPropertyChanged(nameof(IsTranscriptEmpty));
    }

    // ---- git (host working directory) ----

    public ObservableCollection<GitFileChange> GitChanges { get; } = [];

    /// <summary>Open pull requests on the session's forge (loaded on demand via <see cref="RefreshPullRequestsCommand"/>).</summary>
    public ObservableCollection<PullRequestInfo> PullRequests { get; } = [];

    public ICommand RefreshGitCommand { get; }
    public AsyncRelayCommand CommitCommand { get; }
    public AsyncRelayCommand StashCommand { get; }
    public AsyncRelayCommand PopStashCommand { get; }
    public AsyncRelayCommand SwitchBranchCommand { get; }
    public AsyncRelayCommand PullCommand { get; }
    public AsyncRelayCommand PushCommand { get; }
    public AsyncRelayCommand PublishBranchCommand { get; }
    public AsyncRelayCommand RefreshPullRequestsCommand { get; }
    public AsyncRelayCommand<PullRequestInfo> CheckoutPullRequestCommand { get; }

    public GitStatus? Git
    {
        get => _git;
        private set
        {
            if (SetProperty(ref _git, value))
            {
                OnPropertyChanged(nameof(HasGit));
                OnPropertyChanged(nameof(GitBranch));
                OnPropertyChanged(nameof(GitDirty));
                OnPropertyChanged(nameof(GitSummary));
                CommitCommand.NotifyCanExecuteChanged();
                StashCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The most recent stash Agnes created for this session (so the UI can offer "restore").</summary>
    public GitStashInfo? LastStash
    {
        get => _lastStash;
        private set
        {
            if (SetProperty(ref _lastStash, value))
            {
                OnPropertyChanged(nameof(HasStash));
                PopStashCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasStash => _lastStash is not null;

    /// <summary>The branch name typed into the switcher.</summary>
    public string TargetBranch
    {
        get => _targetBranch;
        set { if (SetProperty(ref _targetBranch, value)) { SwitchBranchCommand.NotifyCanExecuteChanged(); } }
    }

    /// <summary>Whether a branch switch should carry uncommitted changes across via a stash.</summary>
    public bool CarryStash
    {
        get => _carryStash;
        set => SetProperty(ref _carryStash, value);
    }

    /// <summary>The last git-operation status line (stash/switch/pull/push/PR), shown in the git flyout.</summary>
    public string GitOpMessage
    {
        get => _gitOpMessage;
        private set { if (SetProperty(ref _gitOpMessage, value)) { OnPropertyChanged(nameof(HasGitOpMessage)); } }
    }

    /// <summary>Whether the last git operation failed (so the message renders as an error, e.g. a refused pull).</summary>
    public bool GitOpFailed
    {
        get => _gitOpFailed;
        private set => SetProperty(ref _gitOpFailed, value);
    }

    public bool HasGitOpMessage => !string.IsNullOrEmpty(_gitOpMessage);

    public AsyncRelayCommand PauseSandboxCommand { get; }
    public AsyncRelayCommand ResumeSandboxCommand { get; }
    public AsyncRelayCommand DeleteSandboxCommand { get; }

    public SandboxStatus? Sandbox
    {
        get => _sandbox;
        private set
        {
            if (SetProperty(ref _sandbox, value))
            {
                OnPropertyChanged(nameof(HasSandbox));
                OnPropertyChanged(nameof(SandboxPaused));
                OnPropertyChanged(nameof(SandboxSummary));
                PauseSandboxCommand.NotifyCanExecuteChanged();
                ResumeSandboxCommand.NotifyCanExecuteChanged();
                DeleteSandboxCommand.NotifyCanExecuteChanged();
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
        set { if (SetProperty(ref _commitMessage, value)) { CommitCommand.NotifyCanExecuteChanged(); } }
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

    private void ReportGit(string message, bool failed)
        => _dispatcher.Post(() => { GitOpMessage = message; GitOpFailed = failed; });

    private async Task StashAsync()
    {
        var stash = await _host.GitStashAsync(SessionId);
        _dispatcher.Post(() =>
        {
            LastStash = stash;
            GitOpMessage = stash is null ? "Nothing to stash." : $"Stashed {stash.FileCount} file(s) from {stash.Branch}.";
            GitOpFailed = stash is null;
        });
        await RefreshGitAsync();
    }

    private async Task PopStashAsync()
    {
        if (_lastStash is null)
        {
            return;
        }

        var result = await _host.GitPopStashAsync(SessionId, _lastStash.StashId);
        _dispatcher.Post(() =>
        {
            GitOpMessage = result.Message;
            GitOpFailed = !result.Success;
            if (result.Success)
            {
                LastStash = null;
            }
        });
        await RefreshGitAsync();
    }

    private async Task SwitchBranchAsync()
    {
        var branch = TargetBranch.Trim();
        if (branch.Length == 0)
        {
            return;
        }

        var result = await _host.GitSwitchBranchAsync(SessionId, branch, CarryStash);
        _dispatcher.Post(() =>
        {
            GitOpMessage = result.Message;
            GitOpFailed = !result.Success;
            if (result.Success)
            {
                TargetBranch = string.Empty;
            }
        });
        await RefreshGitAsync();
    }

    private async Task PullAsync()
    {
        var result = await _host.GitPullAsync(SessionId);
        // A non-fast-forwardable remote is refused server-side; surface it as a clear, actionable error.
        var message = result.NonFastForward
            ? $"Pull refused: the remote has diverged and can't be fast-forwarded. Reconcile it manually. {result.Message}"
            : result.Message;
        ReportGit(message, !result.Success);
        await RefreshGitAsync();
    }

    private async Task PushAsync(bool publishBranch)
    {
        var result = await _host.GitPushAsync(SessionId, publishBranch);
        ReportGit(result.Message, !result.Success);
        await RefreshGitAsync();
    }

    private async Task RefreshPullRequestsAsync()
    {
        var prs = await _host.ListPullRequestsAsync(SessionId);
        _dispatcher.Post(() =>
        {
            PullRequests.Clear();
            foreach (var pr in prs)
            {
                PullRequests.Add(pr);
            }

            if (prs.Count == 0)
            {
                GitOpMessage = "No open pull requests (or no forge configured for this remote).";
                GitOpFailed = false;
            }
        });
    }

    private async Task CheckoutPullRequestAsync(PullRequestInfo pullRequest)
    {
        var result = await _host.CheckoutPullRequestAsync(SessionId, pullRequest.Id);
        ReportGit(result.Message, !result.Success);
        await RefreshGitAsync();
    }

    /// <summary>Attaches an arbitrary content block (e.g. an image from the shell's file picker).</summary>
    public void Attach(PromptAttachment attachment)
    {
        Attachments.Add(attachment);
        OnPropertyChanged(nameof(HasAttachments));
        _ = _bus.DispatchAsync(new Plugins.AttachmentAddedEvent(SessionId)); // observe-only
    }

    /// <summary>Uploads a picked/pasted file to the workspace (materialized to a gitignored path, not sent
    /// inline) and adds it as a reference attachment — the agent receives the path, never the bytes.</summary>
    public async Task AttachFileAsync(string fileName, byte[] data)
    {
        var path = await _host.UploadAttachmentAsync(SessionId, fileName, data).ConfigureAwait(true);
        Attach(PromptAttachment.Reference(path));
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
        if (command is null)
        {
            return;
        }

        if (command.IsBroken)
        {
            // The referenced prompt is gone: surface the breakage in the composer rather than inserting
            // nothing (AC: a broken template is visibly broken, never silently empty).
            PromptText = $"[broken template /{command.Name}: referenced prompt was deleted]";
            return;
        }

        PromptText = command.Expansion;
        if (command.SendImmediately && SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
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

            foreach (var template in _promptTemplates)
            {
                var token = template.SlashToken.TrimStart('/');
                if (!token.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var prompt = _libraryPrompts.FirstOrDefault(p => p.Id == template.PromptId);
                if (prompt is null)
                {
                    SlashSuggestions.Add(new SlashCommand(token, "⚠ broken template — referenced prompt was deleted", string.Empty, IsBroken: true));
                }
                else
                {
                    var send = template.Behavior == Agnes.Abstractions.TemplateBehavior.InsertAndSend;
                    SlashSuggestions.Add(new SlashCommand(token, send ? $"{prompt.Title} (send)" : prompt.Title, prompt.MarkdownBody, SendImmediately: send));
                }
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
                OnPropertyChanged(nameof(HasApprovals));
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

            case SessionTitleEvent titleEvent when !string.IsNullOrWhiteSpace(titleEvent.Title):
                AgentTitle = PrettifyTitle(titleEvent.Title);
                break;

            case ModeChangedEvent mode:
                CurrentModeId = mode.ModeId;
                break;

            case PendingQueueEvent queue:
                ApplyPendingQueue(queue);
                break;

            case McpToolCallEvent mcp:
                McpCalls.Insert(0, new McpCallEntry(mcp.Server, mcp.Tool, @event.Timestamp));
                OnPropertyChanged(nameof(HasMcpCalls));
                RaisePanels();
                break;

            case GitCredentialEvent gc:
                CredentialUses.Insert(0, new CredentialUseEntry(gc.Host, gc.Repo, gc.Allowed, @event.Timestamp));
                OnPropertyChanged(nameof(HasCredentialUses));
                RaisePanels();
                break;

            case UsageReportedEvent u:
                // Merge: each event may carry only some fields (context from a message, cost from the
                // result), so keep the last real value for any field this event didn't report.
                if (u.Metrics.ContextUsed is not null) _ctxUsed = u.Metrics.ContextUsed;
                if (u.Metrics.ContextWindow is not null) _ctxWindow = u.Metrics.ContextWindow;
                if (u.Metrics.OutputTokens is not null) _outputTokens = u.Metrics.OutputTokens;
                if (u.Metrics.CostUsd is not null) _costUsd = u.Metrics.CostUsd;
                Usage = new UsageInfo(_ctxUsed, _ctxWindow, _outputTokens, _costUsd);
                break;
        }

        // While filtered to a subagent, refresh the (snapshot) view as its events arrive.
        if (_selectedAgentId is not null)
        {
            OnPropertyChanged(nameof(DisplayItems));
            OnPropertyChanged(nameof(IsTranscriptEmpty));
        }

        // New activity while the tab is focused keeps it read (unless the user stuck it unread); otherwise
        // the badge may need refreshing.
        if (_isActive && !_stickyUnread)
        {
            _ = _host.MarkSessionReadAsync(SessionId, _view.LastSequence);
        }
        else
        {
            OnPropertyChanged(nameof(IsUnread));
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
        _ = _bus.DispatchAsync(new Plugins.RetryRequestedEvent(SessionId)); // observe-only

        if (Banner == SessionBanner.Interrupted && !string.IsNullOrWhiteSpace(_lastPrompt))
        {
            // Resend through the normal path so the user gets immediate feedback (the Running/thinking
            // indicator + Stop button) while the host resumes — which for a restored sandboxed session can
            // take a while (cold-starting the VM + re-attaching) — and so a failure surfaces as a banner.
            await SubmitAsync(_lastPrompt);
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
                var isFile = IsFileTool(tc.Kind);
                // Modified files open as a DIFF, not the raw edit request — build one from the tool input.
                var entry = new ToolEntry(tc.ToolCallId, tc.Title, tc.Kind.ToString(), tc.Status.ToString(),
                    isFile ? FileDiff(tc) : TextOf(tc.Content));
                _tools[tc.ToolCallId] = entry;
                (isFile ? ModifiedFiles : ToolActivity).Add(entry);
                RaisePanels();
                break;

            case ToolCallUpdateEvent u when _tools.TryGetValue(u.ToolCallId, out var tracked):
                if (u.Status is { } status)
                {
                    tracked.StatusText = status.ToString();
                }

                // For an edit, keep the diff we built on start; the tool result is just a confirmation.
                if (!IsFileToolKind(tracked.Kind) && u.Content is { } content && TextOf(content) is { Length: > 0 } text)
                {
                    tracked.Detail = text;
                }

                break;
        }
    }

    private void Preview(string title, string body, bool markdown = false) => SelectedPreview = new PreviewViewModel(title, body, markdown);

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

        OnPropertyChanged(nameof(MatchSummary));
    }

    private void StepMatch(int direction)
    {
        if (Matches.Count == 0)
        {
            return;
        }

        _matchCursor = ((_matchCursor + direction) % Matches.Count + Matches.Count) % Matches.Count;
        ScrollToRequested?.Invoke(Matches[_matchCursor].AnchorId);
        OnPropertyChanged(nameof(MatchSummary));
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
            OnPropertyChanged(nameof(MatchSummary));
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
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasTools));
        OnPropertyChanged(nameof(HasSidebarContent));
        OnPropertyChanged(nameof(ShowLeftPanel));
        RaiseActivity();
    }

    private static bool IsFileTool(ToolKind kind)
        => kind is ToolKind.Edit or ToolKind.Delete or ToolKind.Move;

    private static bool IsFileToolKind(string kind)
        => kind is nameof(ToolKind.Edit) or nameof(ToolKind.Delete) or nameof(ToolKind.Move);

    /// <summary>
    /// Renders a file-edit tool call as a unified diff for the preview pane. If the content is already a
    /// diff (ACP agents, or a native adapter that emits DiffContent) it's used as-is; otherwise the edit
    /// request (Claude's Edit/Write input JSON — file_path + old_string/new_string, or content) is turned
    /// into one. This works for stored sessions too, since the full input JSON is persisted.
    /// </summary>
    private static string FileDiff(ToolCallEvent tc)
    {
        var text = TextOf(tc.Content);
        if (Diff.DiffParser.LooksLikeDiff(text))
        {
            return text;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("file_path", out var fp))
            {
                var path = fp.GetString() ?? string.Empty;
                if (root.TryGetProperty("old_string", out var oldS) && root.TryGetProperty("new_string", out var newS))
                {
                    return Diff.UnifiedDiff.Format(path, oldS.GetString() ?? string.Empty, newS.GetString() ?? string.Empty);
                }

                if (root.TryGetProperty("content", out var content)) // Write = a whole new file
                {
                    return Diff.UnifiedDiff.Format(path, string.Empty, content.GetString() ?? string.Empty);
                }
            }
        }
        catch
        {
            // Not a JSON edit request — fall back to showing the raw detail.
        }

        return text;
    }

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

        // Non-default send policies are host-authoritative: the host applies the policy (queue / hold /
        // interrupt-and-send) and every connected client converges on the result via PendingQueueEvent
        // (mirrored in HostPending / DiscardedMessages). The default QueueInAgent keeps the lightweight
        // client-local queue below (unchanged behaviour); switch policy to engage the shared host queue.
        if (SendPolicy != SendPolicy.QueueInAgent)
        {
            await _host.EnqueuePendingMessageAsync(SessionId, BuildPromptBlocks(text));
            return;
        }

        if (IsTurnActive)
        {
            PendingPrompts.Add(new QueuedPrompt(text));
            OnPropertyChanged(nameof(HasQueue));
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

    // Builds the outgoing content blocks (any attached context first, then the typed text) and clears the
    // attachment chips. Shared by the immediate-send path and the host-policy enqueue path.
    private List<ContentBlock> BuildPromptBlocks(string text)
    {
        var blocks = new List<ContentBlock>(Attachments.Count + 1);
        blocks.AddRange(Attachments.Select(a => a.Content));
        blocks.Add(new TextContent(text));
        if (Attachments.Count > 0)
        {
            Attachments.Clear();
            OnPropertyChanged(nameof(HasAttachments));
        }

        return blocks;
    }

    private async Task SubmitAsync(string text)
    {
        // Client event spine: a client plugin may rewrite the outgoing message or veto it before it leaves
        // this client (the host's BeforePromptEvent still fires afterward for host-side plugins).
        var send = await _bus.DispatchAsync(new Plugins.BeforeMessageSendEvent(SessionId, text));
        if (send.IsCanceled)
        {
            return;
        }

        text = send.Text;
        _interrupted = false;
        UpdateBanner();
        IsTurnActive = true;

        var blocks = BuildPromptBlocks(text);

        try
        {
            await _host.PromptAsync(SessionId, blocks);

            // Prompting a dormant sandboxed session re-attaches (resumes) its VM server-side; refresh so the
            // toolbar chip flips from "stopped" to "running" now that the sandbox is live again.
            if (HasSandbox)
            {
                await RefreshSandboxAsync();
            }
        }
        catch (Exception)
        {
            // A send can fail server-side — e.g. a restored sandboxed session whose VM is truly gone, or a
            // transient hub error. Never let it crash the app: end the turn and surface it as a stale
            // banner (Fork/Duplicate starts fresh).
            IsTurnActive = false;
            _stale = true;
            UpdateBanner();
        }
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
        OnPropertyChanged(nameof(HasQueue));
        _ = SubmitAsync(next.Text);
    }

    private async Task CancelAsync()
    {
        if (!await _bus.AllowsAsync(new Plugins.BeforeTurnCancelEvent(SessionId)))
        {
            return; // a plugin kept the turn running
        }

        IsTurnActive = false;
        await _host.CancelAsync(SessionId);
    }

    private void RemoveQueued(QueuedPrompt? item)
    {
        if (item is not null && PendingPrompts.Remove(item))
        {
            OnPropertyChanged(nameof(HasQueue));
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
        OnPropertyChanged(nameof(HasQueue));
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
