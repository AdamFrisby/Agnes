using System.Collections.ObjectModel;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
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
    private readonly TranscriptBuilder _transcript = new();
    private readonly Dictionary<string, ToolEntry> _tools = new();
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
    private SessionBanner _banner;
    private string _searchQuery = string.Empty;
    private bool _isSearchOpen;
    private int _matchCursor = -1;
    private int _promptCursor = -1;
    private int _changeCursor = -1;

    public SessionViewModel(IAgnesHost host, SessionView view, IUiDispatcher dispatcher, string title, IPromptStore? prompts = null)
    {
        _host = host;
        _view = view;
        _dispatcher = dispatcher;
        _prompts = prompts ?? NullPromptStore.Instance;
        Title = title;

        _promptText = _prompts.LoadDraft(view.SessionId);
        _history = [.. _prompts.LoadHistory(view.SessionId)];
        _historyIndex = _history.Count;

        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(PromptText));
        AllowCommand = new AsyncRelayCommand(() => RespondAsync(allow: true));
        DenyCommand = new AsyncRelayCommand(() => RespondAsync(allow: false));
        ToggleLeftCommand = new RelayCommand(() => { _leftHidden = !_leftHidden; Raise(nameof(ShowLeftPanel)); });
        ToggleToolsCommand = new RelayCommand(() => ToolsExpanded = !ToolsExpanded);
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

        _transcript.PendingPermissionChanged += () => Raise(nameof(PendingPermission));

        foreach (var @event in _view.Events)
        {
            Apply(@event);
        }

        _view.EventAppended += OnEvent;
        _host.StateChanged += OnHostStateChanged;
        UpdateBanner();
    }

    public string Title { get; }
    public string SessionId => _view.SessionId;
    public ObservableCollection<TranscriptItem> Items => _transcript.Items;
    public PermissionItem? PendingPermission => _transcript.PendingPermission;

    // Left column
    public ObservableCollection<ToolEntry> ModifiedFiles { get; } = [];
    public ObservableCollection<ToolEntry> ToolActivity { get; } = [];

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
    public bool HasSidebarContent => Plan is not null || HasFiles || HasTools;
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

    public string BannerText => Banner switch
    {
        SessionBanner.Offline => "Offline — the host is unreachable.",
        SessionBanner.Reconnecting => "Reconnecting…",
        SessionBanner.Interrupted => "The last turn was interrupted.",
        SessionBanner.Stale => "This session is stale and may be out of date.",
        _ => string.Empty,
    };

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
                _prompts.SaveDraft(SessionId, value);
            }
        }
    }

    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand AllowCommand { get; }
    public AsyncRelayCommand DenyCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public ICommand ToggleLeftCommand { get; }
    public ICommand ToggleToolsCommand { get; }
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

        switch (@event)
        {
            case PermissionRequestedEvent pr:
                NotificationRaised?.Invoke(new AppNotification("Permission needed", pr.Title, NotificationKind.Blocker, SessionId));
                break;

            case TurnEndedEvent { Reason: not StopReason.Cancelled }:
                NotificationRaised?.Invoke(new AppNotification($"{Title}: response ready", "The agent finished its turn.", NotificationKind.Completion, SessionId));
                break;

            case AgentErrorEvent ae:
                _interrupted = true;
                UpdateBanner();
                NotificationRaised?.Invoke(new AppNotification("Agent error", ae.Message, NotificationKind.Error, SessionId));
                break;
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
    }

    private static bool IsFileTool(ToolKind kind)
        => kind is ToolKind.Edit or ToolKind.Delete or ToolKind.Move;

    private static string TextOf(IReadOnlyList<ContentBlock> content)
        => string.Concat(content.OfType<TextContent>().Select(c => c.Text));

    private async Task SendAsync()
    {
        var text = PromptText;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _history.Add(text);
            _prompts.AppendHistory(SessionId, text);
            _historyIndex = _history.Count;
            _lastPrompt = text;
        }

        _interrupted = false;
        UpdateBanner();
        PromptText = string.Empty;
        await _host.PromptAsync(SessionId, [new TextContent(text)]);
    }

    private async Task RespondAsync(bool allow)
    {
        if (PendingPermission is not { } permission)
        {
            return;
        }

        var option = permission.Options.FirstOrDefault(o => IsAllow(o.Kind) == allow)
                     ?? permission.Options[0];
        await _host.RespondPermissionAsync(SessionId, permission.RequestId, option.OptionId);
    }

    private static bool IsAllow(PermissionOptionKind kind)
        => kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways;
}
