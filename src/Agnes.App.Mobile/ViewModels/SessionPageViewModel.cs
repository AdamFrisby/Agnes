using Agnes.Abstractions;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

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
                _shell.ShowSheet(new DetailSheetViewModel(_shell, item.Header, item.Detail));
            }
        });
        OpenMessageCommand = new RelayCommand<MessageBubbleItem>(item =>
        {
            if (item is not null)
            {
                _shell.ShowSheet(new DetailSheetViewModel(_shell, item.Speaker, item.Text, markdown: true));
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
    public IRelayCommand ToggleSearchCommand { get; }
    public IRelayCommand<SlashCommand> ApplySlashCommand { get; }
    public IRelayCommand<PromptAttachment> RemoveAttachmentCommand { get; }
    public IRelayCommand<ToolCallItem> OpenToolCommand { get; }
    public IRelayCommand<MessageBubbleItem> OpenMessageCommand { get; }
    public IRelayCommand<QuestionItem> AnswerQuestionCommand { get; }

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

    // ---- in-transcript search ----

    [ObservableProperty]
    private bool _isSearchOpen;

    public override bool OnBackRequested()
    {
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

    public override void OnDisappearing() => Session?.SetActive(false);

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
    }
}
