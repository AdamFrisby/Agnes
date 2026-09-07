using Agnes.Abstractions;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>What the middle of the session page is showing.</summary>
public enum SessionSegment
{
    /// <summary>The conversation.</summary>
    Transcript,

    /// <summary>The graphical sandbox's screen. Only reachable when the session has one.</summary>
    Screen,
}

/// <summary>
/// What this phone asks the host to send for a graphical session.
///
/// Two tiers, because there are two situations and no useful middle. On Wi-Fi the limit is the screen:
/// asking for more pixels than the panel has is bytes nobody can see, so the width is the display's own
/// and 15 fps is enough to watch someone work. On a metered connection the limit is the bill, and 640 at
/// 8 fps is roughly a third of the data for a picture that still answers "what is it doing".
/// </summary>
public sealed record ScreenQuality(int MaxWidth, int MaxFps, int JpegQuality)
{
    /// <summary>Cellular, or a metered hotspot.</summary>
    public static readonly ScreenQuality Metered = new(640, 8, 55);

    /// <summary>Unmetered: as wide as the panel, capped so a tablet doesn't ask for a full desktop.</summary>
    public static ScreenQuality For(int screenPixelWidth) => new(Math.Clamp(screenPixelWidth, 320, 960), 15, 65);
}

/// <summary>
/// A live session, full-screen.
///
/// This is the one place the mobile client diverges hardest from the desktop one. The desktop session
/// is three columns — plan and files on the left, transcript in the middle, a diff preview on the
/// right — because it can afford to show everything at once. A phone gets one column and a thumb, so:
///
///   · the transcript is the screen, edge to edge;
///   · the composer is pinned to the bottom, where the thumb already is;
///   · a permission request is promoted out of the transcript into a card directly above the composer,
///     because approving is the single most valuable thing you can do from a phone and it must not be
///     something you scroll to find;
///   · everything the desktop puts in a panel is a sheet, reachable from a chip strip or the overflow.
/// </summary>
public sealed partial class SessionPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;
    private readonly SessionsViewModel _sessions;

    public SessionPageViewModel(IAppShell shell, SessionsViewModel sessions, SessionEntry entry, SessionViewModel? session)
    {
        _shell = shell;
        _sessions = sessions;
        Entry = entry;
        _session = session;

        BackCommand = new RelayCommand(_shell.Pop);
        SendCommand = new RelayCommand(Send);
        StopCommand = new RelayCommand(Stop);
        DictateCommand = new AsyncRelayCommand(DictateAsync);
        RetryCommand = new RelayCommand(Retry);
        AllowCommand = new RelayCommand(() => Respond(allow: true));
        DenyCommand = new RelayCommand(() => Respond(allow: false));
        RespondWithCommand = new RelayCommand<PermissionOption>(RespondWith);
        ShowFilesCommand = new RelayCommand(() => Sheet(s => new ChangedFilesSheetViewModel(_shell, s)));
        ShowToolsCommand = new RelayCommand(() => Sheet(s => new ToolsSheetViewModel(_shell, s)));
        ShowGitCommand = new RelayCommand(() => Sheet(s => new GitSheetViewModel(_shell, s)));
        ShowInfoCommand = new RelayCommand(() => Sheet(s => new SessionInfoSheetViewModel(_shell, this, s)));
        ShowAgentsCommand = new RelayCommand(() => Sheet(s => new AgentsSheetViewModel(s)));
        ShowPlanCommand = new RelayCommand(() => Sheet(s => new PlanSheetViewModel(s)));
        ShowActionsCommand = new RelayCommand(() => _shell.ShowSheet(new SessionActionsSheetViewModel(_shell, _sessions, Entry)));
        ShowQueueCommand = new RelayCommand(() => Sheet(s => new QueueSheetViewModel(_shell, s)));
        ShowTranscriptCommand = new RelayCommand(() => Segment = SessionSegment.Transcript);
        ShowScreenCommand = new AsyncRelayCommand(ShowScreenAsync);
        SendScreenKeyCommand = new RelayCommand<string>(key =>
        {
            if (!string.IsNullOrEmpty(key))
            {
                ScreenKeyRequested?.Invoke(key);
                _shell.Haptics.Tick();
            }
        });
        ShowScreenKeyboardCommand = new RelayCommand(() => ScreenKeyboardRequested?.Invoke());
        ToggleSearchCommand = new RelayCommand(() =>
        {
            IsSearchOpen = !IsSearchOpen;
            if (!IsSearchOpen && Session is not null)
            {
                Session.SearchQuery = string.Empty;
            }
        });
        ApplySlashCommand = new RelayCommand<SlashCommand>(c =>
        {
            if (c is not null && Session is not null)
            {
                Session.ApplySlashCommand.Execute(c);
            }
        });
        RemoveAttachmentCommand = new RelayCommand<PromptAttachment>(a =>
        {
            if (a is not null && Session is not null)
            {
                Session.RemoveAttachmentCommand.Execute(a);
            }
        });
        OpenToolCommand = new RelayCommand<ToolCallItem>(item =>
        {
            if (item is { HasDetail: true })
            {
                // The diff for an edit, not the "… has been updated successfully" receipt behind it —
                // and the command itself, which the one-line row above could only show the start of.
                _shell.ShowSheet(new DetailSheetViewModel(_shell, item.KindLabel, item.PreviewBody, command: item.Title));
            }
        });
        OpenMessageCommand = new RelayCommand<MessageBubbleItem>(item =>
        {
            if (item is not null)
            {
                _shell.ShowSheet(new DetailSheetViewModel(_shell, item.Speaker, item.Text, markdown: true));
            }
        });
        OpenSharedFileCommand = new RelayCommand<SharedFileItem>(item =>
        {
            if (item is not null && Session is { } live)
            {
                _shell.ShowSheet(new ReceivedFileSheetViewModel(_shell, live, item));
            }
        });
        AnswerQuestionCommand = new RelayCommand<QuestionItem>(item =>
        {
            if (item is not null && Session is not null)
            {
                Session.AnswerQuestionCommand.Execute(item);
                _shell.Haptics.Tick();
            }
        });

        if (session is not null)
        {
            Bind(session);
        }
    }

    public SessionEntry Entry { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    private SessionViewModel? _session;

    public bool IsLive => Session is not null;

    public override string Title => Entry.Title;

    public override string? Subtitle => $"{Entry.HostName} · {Entry.AgentName}";


    // ---- commands ----

    /// <summary>Leaves the session. Routed through the shell so the app-bar chevron and the system back
    /// gesture do exactly the same thing.</summary>
    public IRelayCommand BackCommand { get; }

    public IRelayCommand SendCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IAsyncRelayCommand DictateCommand { get; }
    public IRelayCommand RetryCommand { get; }
    public IRelayCommand AllowCommand { get; }
    public IRelayCommand DenyCommand { get; }
    public IRelayCommand<PermissionOption> RespondWithCommand { get; }
    public IRelayCommand ShowFilesCommand { get; }
    public IRelayCommand ShowToolsCommand { get; }
    public IRelayCommand ShowGitCommand { get; }
    public IRelayCommand ShowInfoCommand { get; }
    public IRelayCommand ShowAgentsCommand { get; }
    public IRelayCommand ShowPlanCommand { get; }
    public IRelayCommand ShowActionsCommand { get; }
    public IRelayCommand ShowQueueCommand { get; }

    /// <summary>Back to the conversation. The screen stays connected — the thumbnail above the transcript
    /// is the whole point of not tearing it down.</summary>
    public IRelayCommand ShowTranscriptCommand { get; }

    /// <summary>Opens the Screen segment: connects the display channel if it isn't already, and tells the
    /// host what this phone can use.</summary>
    public IAsyncRelayCommand ShowScreenCommand { get; }

    /// <summary>Sends one named key (Escape, Tab, an arrow) the soft keyboard has no room for.</summary>
    public IRelayCommand<string> SendScreenKeyCommand { get; }

    /// <summary>Raises the IME over the screen.</summary>
    public IRelayCommand ShowScreenKeyboardCommand { get; }
    public IRelayCommand ToggleSearchCommand { get; }
    public IRelayCommand<SlashCommand> ApplySlashCommand { get; }
    public IRelayCommand<PromptAttachment> RemoveAttachmentCommand { get; }
    public IRelayCommand<ToolCallItem> OpenToolCommand { get; }
    public IRelayCommand<MessageBubbleItem> OpenMessageCommand { get; }
    public IRelayCommand<QuestionItem> AnswerQuestionCommand { get; }

    /// <summary>Opens the sheet for a file the agent sent: the preview, then share / save / open.</summary>
    public IRelayCommand<SharedFileItem> OpenSharedFileCommand { get; }

    public bool CanDictate => _shell.CanDictate;

    /// <summary>Whether the agent's reasoning is rendered inline. Off by default (Appearance) — on a
    /// phone the thinking is usually noise between you and the answer.</summary>
    public bool ShowThinking => _shell.Settings.ShowThinking;

    /// <summary>
    /// The permission options the two big buttons don't already cover — the "always" variants, and
    /// anything bespoke an agent offered. Repeating "Allow once" as a chip under an "Allow once" button
    /// just makes the card look like it's asking twice.
    /// </summary>
    public IReadOnlyList<PermissionOption> StandingOptions => Permission is { } permission
        ? permission.Options.Where(o => o.Kind is not (PermissionOptionKind.AllowOnce or PermissionOptionKind.RejectOnce)).ToList()
        : [];

    // ---- the screen ----

    /// <summary>
    /// Whether this session has a graphical sandbox. Read from the saved pointer (the host's catalogue
    /// says so, or we asked for one at launch) so the segment exists before the subscription lands.
    /// </summary>
    public bool HasDisplay => Entry.HasDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranscriptSegment), nameof(IsScreenSegment), nameof(ShowThumbnail))]
    private SessionSegment _segment = SessionSegment.Transcript;

    public bool IsTranscriptSegment => Segment == SessionSegment.Transcript;

    public bool IsScreenSegment => Segment == SessionSegment.Screen;

    /// <summary>The live strip above the conversation — only for a session that has a screen, and only
    /// while you're looking at the conversation.</summary>
    public bool ShowThumbnail => HasDisplay && IsTranscriptSegment;

    /// <summary>
    /// The panel's width in real pixels, set by the view once it is attached — the quality request is a
    /// promise about what this device can actually show, and the view is the only thing that knows.
    /// </summary>
    public int ScreenPixelWidth { get; set; } = 960;

    /// <summary>What we ask the host for. Metered wins only if the person left the setting on.</summary>
    public ScreenQuality Quality =>
        _shell.IsMeteredNetwork && _shell.Settings.LowerScreenQualityOnMobileData
            ? ScreenQuality.Metered
            : ScreenQuality.For(ScreenPixelWidth);

    /// <summary>
    /// The display channel for this session, built the first time the screen is asked for.
    ///
    /// Built here rather than taken from the session because it is this page's resource: it opens a
    /// second connection to the host and holds it until the page is left, and a view model shared with
    /// the sessions list has no business owning that.
    /// </summary>
    public DisplayViewModel? Display
    {
        get
        {
            // Not built for a session without a screen: the thumbnail binds this on every session page,
            // and a DisplayViewModel per conversation is a connection waiting to be opened by accident.
            if (_display is null && HasDisplay && Session is { } session)
            {
                _display = new DisplayViewModel(session.Host, session.SessionId, _shell.Dispatcher);
            }

            return _display;
        }

        // Settable so a head that already holds one can hand it over rather than opening a second
        // channel to the same session — which is what this becomes the day SessionViewModel.Display
        // lands, and what the headless harness uses to feed the screen synthetic frames.
        set
        {
            if (!ReferenceEquals(_display, value))
            {
                _display = value;
                OnPropertyChanged();
            }
        }
    }

    private DisplayViewModel? _display;

    /// <summary>Raised when the soft keyboard should come up over the screen.</summary>
    public event Action? ScreenKeyboardRequested;

    /// <summary>Raised with an X keysym name for a key the soft keyboard doesn't offer.</summary>
    public event Action<string>? ScreenKeyRequested;

    private async Task ShowScreenAsync()
    {
        Segment = SessionSegment.Screen;
        if (Display is not { } display)
        {
            return;
        }

        // Connect first and *then* state the quality: input and preferences are dropped while the
        // channel is closed, so asking in the other order silently leaves a phone on desktop frames.
        if (!display.IsConnected)
        {
            await display.ConnectCommand.ExecuteAsync(null).ConfigureAwait(true);
        }

        var quality = Quality;
        await display.SetQualityAsync(quality.MaxWidth, quality.MaxFps, quality.JpegQuality).ConfigureAwait(true);
    }

    // ---- in-transcript search ----

    [ObservableProperty]
    private bool _isSearchOpen;

    public override bool OnBackRequested()
    {
        if (IsScreenSegment)
        {
            Segment = SessionSegment.Transcript;
            return true;
        }

        if (IsSearchOpen)
        {
            IsSearchOpen = false;
            if (Session is not null)
            {
                Session.SearchQuery = string.Empty;
            }

            return true;
        }

        return false;
    }

    public override void OnAppearing()
    {
        Session?.SetActive(true);
        ScrollToBottomRequested?.Invoke();
    }

    public override void OnDisappearing()
    {
        Session?.SetActive(false);
        // Leaving the page, not the segment: switching back to the transcript keeps the stream so the
        // thumbnail stays live, but walking away from the session must not leave a video call running.
        _display?.DisconnectCommand.Execute(null);
    }

    /// <summary>Raised when the transcript should jump to the newest item.</summary>
    public event Action? ScrollToBottomRequested;

    /// <summary>Raised with an anchor id when the transcript should scroll to a specific item.</summary>
    public event Action<string>? ScrollToRequested;

    /// <summary>Called by the sessions list once a late subscription lands.</summary>
    public void Adopt(SessionViewModel session)
    {
        Session = session;
        Bind(session);
        RaiseDerived();
    }

    private void Bind(SessionViewModel session)
    {
        session.PropertyChanged += (_, e) =>
        {
            RaiseDerived();
            if (e.PropertyName == nameof(SessionViewModel.PendingPermission) && session.PendingPermission is not null)
            {
                // A phone in a pocket needs the physical cue; the shade notification is separate and only
                // fires when the app isn't foreground.
                _shell.Haptics.Alert();
            }
        };
        session.Items.CollectionChanged += (_, _) => RaiseDerived();
        session.ScrollToBottomRequested += () => ScrollToBottomRequested?.Invoke();
        session.ScrollToRequested += anchor => ScrollToRequested?.Invoke(anchor);
        session.SetActive(true);
    }

    private void Sheet(Func<SessionViewModel, SheetViewModel> build)
    {
        if (Session is { } session)
        {
            _shell.ShowSheet(build(session));
        }
    }

    // ---- composer ----

    private void Send()
    {
        if (Session is not { } session || string.IsNullOrWhiteSpace(session.PromptText))
        {
            return;
        }

        var queues = session.IsTurnActive && session.SendPolicy == SendPolicy.QueueInAgent;
        session.SendCommand.Execute(null);
        _shell.Haptics.Tick();
        if (queues)
        {
            _shell.Toast("Queued — it'll go when this turn ends", ToastKind.Info);
        }

        ScrollToBottomRequested?.Invoke();
    }

    private void Stop()
    {
        Session?.CancelCommand.Execute(null);
        _shell.Haptics.Tick();
    }

    private async Task DictateAsync()
    {
        var spoken = await _shell.DictateAsync().ConfigureAwait(true);
        if (Session is { } session && !string.IsNullOrWhiteSpace(spoken))
        {
            // Append rather than replace: dictation is usually a second thought after some typing.
            session.PromptText = string.IsNullOrWhiteSpace(session.PromptText)
                ? spoken
                : session.PromptText.TrimEnd() + " " + spoken;
            _shell.Haptics.Tick();
        }
    }

    private void Retry()
    {
        if (Session is { } session)
        {
            session.RetryCommand.Execute(null);
        }
        else
        {
            _sessions.RefreshCommand.Execute(null);
        }
    }

    // ---- permission card ----

    /// <summary>The open permission request, promoted out of the transcript to sit above the composer.</summary>
    public PermissionItem? Permission => Session?.PendingPermission;

    public bool HasPermission => Permission is not null;

    /// <summary>The open structured question, if the agent asked one.</summary>
    public QuestionItem? Question => Session?.PendingQuestion;

    public bool HasQuestion => Question is not null;

    private void Respond(bool allow)
    {
        if (Session is not { } session)
        {
            return;
        }

        (allow ? session.AllowCommand : session.DenyCommand).Execute(null);
        _shell.Haptics.Tick();
        _shell.Toast(allow ? "Allowed" : "Denied", allow ? ToastKind.Success : ToastKind.Warning);
    }

    private void RespondWith(PermissionOption? option)
    {
        if (option is null || Session is null)
        {
            return;
        }

        Session.RespondWithCommand.Execute(option);
        _shell.Haptics.Tick();
        _shell.Toast(option.Name, ToastKind.Success);
    }

    // ---- derived state for the chrome ----

    public bool IsTurnActive => Session?.IsTurnActive ?? false;

    public string ActivityText => Session?.ActivityText ?? "Reattaching";

    public SessionActivity Activity => Session?.Activity ?? SessionActivity.Idle;

    public bool ShowBanner => Session?.ShowBanner ?? false;

    public string BannerText => Session?.BannerText ?? string.Empty;

    public bool CanRetry => Session?.CanRetry ?? true;

    public bool IsReadOnly => Session?.IsReadOnly ?? false;

    public bool CanSend => Session is { IsReadOnly: false } s && !string.IsNullOrWhiteSpace(s.PromptText);

    /// <summary>Spells out what the send button will do right now — it sends when idle but queues while a
    /// turn is running, and the same glyph must not silently mean two things.</summary>
    public string SendHint => Session is null ? string.Empty
        : Session.IsReadOnly ? "Watching — this session is read-only"
        : Session.IsTurnActive && Session.SendPolicy == SendPolicy.QueueInAgent ? "Queues after this turn"
        : Session.IsTurnActive ? "Sends now"
        : string.Empty;

    public bool HasSendHint => SendHint.Length > 0;

    // Chip strip
    public bool HasPlan => Session?.Plan is not null;
    public bool HasFiles => Session?.HasFiles ?? false;
    public bool HasTools => Session?.HasTools ?? false;
    public bool HasSubagents => Session?.HasSubagents ?? false;
    public bool HasGit => Session?.HasGit ?? false;
    public string GitBranch => Session?.GitBranch ?? string.Empty;
    public bool HasSandbox => Session?.HasSandbox ?? false;
    public bool IsAutonomous => Session?.IsAutonomous ?? false;
    public string? UsageSummary => Session?.UsageSummary;
    public bool HasUsage => !string.IsNullOrEmpty(UsageSummary);
    public bool HasQueue => (Session?.HasQueue ?? false) || (Session?.HasHostPending ?? false);

    public int QueueCount => (Session?.PendingPrompts.Count ?? 0) + (Session?.HostPending.Count ?? 0);

    public string FilesChip => Session is null ? string.Empty : $"{Session.ModifiedFiles.Count} files";
    public string ToolsChip => Session is null ? string.Empty : $"{Session.ToolActivity.Count} tools";

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Permission));
        OnPropertyChanged(nameof(HasPermission));
        OnPropertyChanged(nameof(StandingOptions));
        OnPropertyChanged(nameof(Question));
        OnPropertyChanged(nameof(HasQuestion));
        OnPropertyChanged(nameof(IsTurnActive));
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(SendHint));
        OnPropertyChanged(nameof(HasSendHint));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasTools));
        OnPropertyChanged(nameof(HasSubagents));
        OnPropertyChanged(nameof(HasGit));
        OnPropertyChanged(nameof(GitBranch));
        OnPropertyChanged(nameof(HasSandbox));
        OnPropertyChanged(nameof(IsAutonomous));
        OnPropertyChanged(nameof(UsageSummary));
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(QueueCount));
        OnPropertyChanged(nameof(FilesChip));
        OnPropertyChanged(nameof(ToolsChip));
        OnPropertyChanged(nameof(HasDisplay));
        OnPropertyChanged(nameof(ShowThumbnail));
        OnPropertyChanged(nameof(Display));
    }
}
