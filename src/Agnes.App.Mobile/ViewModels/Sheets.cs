using System.Collections.ObjectModel;
using Agnes.Abstractions;
using Agnes.App.Mobile.Services;
using Agnes.Protocol;
using Agnes.Ui.Core.Diff;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// A read-only body: a tool's output, a file's diff, a long message. Renders as a diff when the text
/// looks like one, as markdown when the caller says so, and as monospace otherwise — the three shapes
/// agent output actually arrives in.
/// </summary>
public sealed partial class DetailSheetViewModel : SheetViewModel
{
    private readonly IAppShell _shell;

    public DetailSheetViewModel(IAppShell shell, string title, string body, bool markdown = false)
    {
        _shell = shell;
        Title = title;
        Body = body;
        IsMarkdown = markdown && !DiffParser.LooksLikeDiff(body);
        if (!IsMarkdown && DiffParser.LooksLikeDiff(body))
        {
            IsDiff = true;
            DiffLines = DiffParser.Parse(body);
        }

        CopyCommand = new RelayCommand(() => _shell.CopyToClipboard(Body, title));
    }

    public override string Title { get; }

    public override double HeightFraction => 0.88;

    public string Body { get; }

    public bool IsDiff { get; }

    public bool IsMarkdown { get; }

    public bool IsPlain => !IsDiff && !IsMarkdown;

    public IReadOnlyList<DiffLine> DiffLines { get; } = [];

    /// <summary>"+12 −3" for a diff, so the size of a change is legible before reading it.</summary>
    public string DiffSummary
    {
        get
        {
            if (!IsDiff)
            {
                return string.Empty;
            }

            var added = DiffLines.Count(l => l.IsAdded);
            var removed = DiffLines.Count(l => l.IsRemoved);
            return $"+{added}  −{removed}";
        }
    }

    public override string? Subtitle => IsDiff ? DiffSummary : null;

    public IRelayCommand CopyCommand { get; }
}

/// <summary>The files this session changed. The mobile answer to the desktop's left-column file list —
/// summoned, tapped through to a diff, dismissed.</summary>
public sealed partial class ChangedFilesSheetViewModel : SheetViewModel
{
    private readonly IAppShell _shell;

    public ChangedFilesSheetViewModel(IAppShell shell, SessionViewModel session)
    {
        _shell = shell;
        Session = session;
        OpenCommand = new RelayCommand<ToolEntry>(entry =>
        {
            if (entry is not null)
            {
                _shell.ShowSheet(new DetailSheetViewModel(_shell, entry.Name, entry.Detail));
            }
        });
    }

    public SessionViewModel Session { get; }

    public override string Title => "Files changed";

    public override string? Subtitle => $"{Session.ModifiedFiles.Count} in this session";

    public IRelayCommand<ToolEntry> OpenCommand { get; }

    public bool IsEmpty => Session.ModifiedFiles.Count == 0;
}

/// <summary>Every tool call the agent made, newest last — the session's working record.</summary>
public sealed partial class ToolsSheetViewModel : SheetViewModel
{
    private readonly IAppShell _shell;

    public ToolsSheetViewModel(IAppShell shell, SessionViewModel session)
    {
        _shell = shell;
        Session = session;
        OpenCommand = new RelayCommand<ToolEntry>(entry =>
        {
            if (entry is not null)
            {
                _shell.ShowSheet(new DetailSheetViewModel(_shell, entry.Name, entry.Detail));
            }
        });
    }

    public SessionViewModel Session { get; }

    public override string Title => "Tools run";

    public override string? Subtitle => $"{Session.ToolActivity.Count} calls";

    public IRelayCommand<ToolEntry> OpenCommand { get; }

    public bool IsEmpty => Session.ToolActivity.Count == 0;
}

/// <summary>The agent's plan, as a checklist.</summary>
public sealed class PlanSheetViewModel : SheetViewModel
{
    public PlanSheetViewModel(SessionViewModel session) => Session = session;

    public SessionViewModel Session { get; }

    public override string Title => "Plan";

    public override double HeightFraction => 0.6;

    public IReadOnlyList<PlanEntry> Entries => Session.Plan?.Entries ?? [];
}

/// <summary>The lead agent and any subagents it spawned.</summary>
public sealed class AgentsSheetViewModel : SheetViewModel
{
    public AgentsSheetViewModel(SessionViewModel session) => Session = session;

    public SessionViewModel Session { get; }

    public override string Title => "Agents";

    public override double HeightFraction => 0.55;

    public override string? Subtitle => $"{Session.AgentRows.Count} in this session";
}

/// <summary>Prompts waiting to go, in send order.</summary>
public sealed partial class QueueSheetViewModel : SheetViewModel
{
    public QueueSheetViewModel(IAppShell shell, SessionViewModel session)
    {
        Session = session;
        RemoveCommand = new RelayCommand<QueuedPrompt>(p =>
        {
            if (p is not null)
            {
                session.RemoveQueuedCommand.Execute(p);
                shell.Haptics.Tick();
            }
        });
        EditCommand = new RelayCommand<QueuedPrompt>(p =>
        {
            if (p is not null)
            {
                session.EditQueuedCommand.Execute(p);
                Close();
            }
        });
    }

    public SessionViewModel Session { get; }

    public override string Title => "Queued";

    public override double HeightFraction => 0.55;

    public override string? Subtitle => "Sent in order as each turn ends";

    public IRelayCommand<QueuedPrompt> RemoveCommand { get; }

    public IRelayCommand<QueuedPrompt> EditCommand { get; }
}

/// <summary>Git for the session's working copy: what changed, commit it, move it.</summary>
public sealed partial class GitSheetViewModel : SheetViewModel
{
    private readonly IAppShell _shell;

    public GitSheetViewModel(IAppShell shell, SessionViewModel session)
    {
        _shell = shell;
        Session = session;
        CommitCommand = new RelayCommand(() =>
        {
            session.CommitCommand.Execute(null);
            _shell.Haptics.Tick();
        });
        GenerateCommand = new RelayCommand(() => session.GenerateCommitMessageCommand.Execute(null));
        PushCommand = new RelayCommand(() => session.PushCommand.Execute(null));
        PullCommand = new RelayCommand(() => session.PullCommand.Execute(null));
        StashCommand = new RelayCommand(() => session.StashCommand.Execute(null));
        RefreshCommand = new RelayCommand(() => session.RefreshGitCommand.Execute(null));
        session.RefreshGitCommand.Execute(null);
    }

    public SessionViewModel Session { get; }

    public override string Title => "Git";

    public override string? Subtitle => Session.HasGit
        ? $"{Session.GitBranch} · {Session.GitSummary}"
        : "Not a repository";

    public IRelayCommand CommitCommand { get; }
    public IRelayCommand GenerateCommand { get; }
    public IRelayCommand PushCommand { get; }
    public IRelayCommand PullCommand { get; }
    public IRelayCommand StashCommand { get; }
    public IRelayCommand RefreshCommand { get; }
}

/// <summary>Everything about the session that isn't the conversation: usage, sandbox, mode, and the
/// audit trails. The desktop shows these as always-on panels; on a phone they're a look, not a fixture.</summary>
public sealed partial class SessionInfoSheetViewModel : SheetViewModel
{
    private readonly IAppShell _shell;

    public SessionInfoSheetViewModel(IAppShell shell, SessionPageViewModel page, SessionViewModel session)
    {
        _shell = shell;
        Page = page;
        Session = session;

        SetModeCommand = new RelayCommand<SessionMode>(m =>
        {
            if (m is not null)
            {
                session.SetModeCommand.Execute(m);
                _shell.Haptics.Tick();
            }
        });
        CopyHandoffCommand = new RelayCommand(() =>
            _shell.CopyToClipboard(session.HandoffReference, "Session link"));
        PauseSandboxCommand = new RelayCommand(() => session.PauseSandboxCommand.Execute(null));
        ResumeSandboxCommand = new RelayCommand(() => session.ResumeSandboxCommand.Execute(null));
        RestartAgentCommand = new RelayCommand(() =>
        {
            session.RestartAgentCommand.Execute(null);
            _shell.Toast("Restarting the agent…");
            Close();
        });
        CompactCommand = new RelayCommand(() =>
        {
            session.CompactCommand.Execute(null);
            _shell.Toast("Asked the agent to compact its context");
            Close();
        });
        SetPolicyCommand = new RelayCommand<string>(name =>
        {
            if (Enum.TryParse<SendPolicy>(name, out var policy))
            {
                session.SendPolicy = policy;
                OnPropertyChanged(nameof(PolicyQueue));
                OnPropertyChanged(nameof(PolicyInterrupt));
                OnPropertyChanged(nameof(PolicyPending));
            }
        });
    }

    public SessionPageViewModel Page { get; }

    public SessionViewModel Session { get; }

    public override string Title => "Session";

    public override string? Subtitle => Session.SessionId;

    public override double HeightFraction => 0.85;

    public IRelayCommand<SessionMode> SetModeCommand { get; }
    public IRelayCommand CopyHandoffCommand { get; }
    public IRelayCommand PauseSandboxCommand { get; }
    public IRelayCommand ResumeSandboxCommand { get; }
    public IRelayCommand RestartAgentCommand { get; }
    public IRelayCommand CompactCommand { get; }
    public IRelayCommand<string> SetPolicyCommand { get; }

    public bool PolicyQueue => Session.SendPolicy == SendPolicy.QueueInAgent;
    public bool PolicyInterrupt => Session.SendPolicy == SendPolicy.InterruptAndSend;
    public bool PolicyPending => Session.SendPolicy == SendPolicy.PendingUntilReady;

    /// <summary>Context-window occupancy as a 0..1 fraction, or null when the agent doesn't report it.
    /// Drives the meter; a session that never reports usage shows nothing rather than a fake zero.</summary>
    public double? ContextFraction
        => Session.Usage is { HasContext: true } usage ? usage.ContextPercent / 100.0 : null;

    public bool HasContext => ContextFraction is not null;

    public string ContextText => Session.Usage is { HasAnyContext: true } usage
        ? usage.ContextText + " tokens"
        : string.Empty;
}

/// <summary>The session overflow menu: rename, pin, share, remove.</summary>
public sealed partial class SessionActionsSheetViewModel : SheetViewModel
{
    private readonly IAppShell _shell;
    private readonly SessionsViewModel _sessions;

    public SessionActionsSheetViewModel(IAppShell shell, SessionsViewModel sessions, SessionEntry entry)
    {
        _shell = shell;
        _sessions = sessions;
        Entry = entry;
        _renameText = entry.Title;

        PinCommand = new RelayCommand(() =>
        {
            _sessions.TogglePin(Entry);
            OnPropertyChanged(nameof(PinLabel));
            _shell.Haptics.Tick();
        });
        RenameCommand = new RelayCommand(() =>
        {
            var name = RenameText.Trim();
            if (name.Length > 0)
            {
                Entry.UpdateSavedTitle(name);
                _sessions.Persist();
                _shell.Toast("Renamed", ToastKind.Success);
            }

            Close();
        });
        CopyLinkCommand = new RelayCommand(() =>
        {
            if (Entry.Session is { } session)
            {
                _shell.CopyToClipboard(session.HandoffReference, "Session link");
            }

            Close();
        });
        MarkUnreadCommand = new RelayCommand(() =>
        {
            Entry.Session?.MarkUnread();
            Close();
        });
        ForgetCommand = new RelayCommand(() =>
        {
            _sessions.Forget(Entry);
            Close();
            _shell.PopToRoot();
        });
    }

    public SessionEntry Entry { get; }

    public override string Title => Entry.Title;

    public override string? Subtitle => $"{Entry.HostName} · {Entry.AgentName}";

    public override double HeightFraction => 0.6;

    [ObservableProperty]
    private string _renameText;

    public string PinLabel => Entry.Pinned ? "Unpin from the top" : "Pin to the top";

    public bool HasSession => Entry.Session is not null;

    public IRelayCommand PinCommand { get; }
    public IRelayCommand RenameCommand { get; }
    public IRelayCommand CopyLinkCommand { get; }
    public IRelayCommand MarkUnreadCommand { get; }
    public IRelayCommand ForgetCommand { get; }
}

/// <summary>The device's hosts: which are reachable, and the way to add another.</summary>
public sealed partial class HostsSheetViewModel : SheetViewModel
{
    private readonly IAppShell _shell;
    private readonly SessionsViewModel _sessions;

    public HostsSheetViewModel(IAppShell shell, HostBook hosts, SessionsViewModel sessions)
    {
        _shell = shell;
        _sessions = sessions;
        Hosts = new ObservableCollection<HostLink>(hosts.Links);

        AddHostCommand = new RelayCommand(() =>
        {
            Close();
            _shell.Push(new ConnectPageViewModel(_shell, hosts, _sessions));
        });
        ReconnectCommand = new AsyncRelayCommand<HostLink>(async link =>
        {
            if (link is null)
            {
                return;
            }

            await link.ConnectAsync().ConfigureAwait(true);
            _shell.Toast(link.IsOnline ? $"{link.Name} is online" : $"Still can't reach {link.Name}",
                link.IsOnline ? ToastKind.Success : ToastKind.Danger);
        });
        ForgetCommand = new RelayCommand<HostLink>(link =>
        {
            if (link is null || link.IsBuiltIn)
            {
                return;
            }

            hosts.Remove(link);
            Hosts.Remove(link);
            _shell.Toast($"Forgot {link.Name}");
        });
    }

    public ObservableCollection<HostLink> Hosts { get; }

    public override string Title => "Hosts";

    public override string? Subtitle => "One host runs the agents; this device connects to it";

    public override double HeightFraction => 0.68;

    public IRelayCommand AddHostCommand { get; }
    public IAsyncRelayCommand<HostLink> ReconnectCommand { get; }
    public IRelayCommand<HostLink> ForgetCommand { get; }
}
