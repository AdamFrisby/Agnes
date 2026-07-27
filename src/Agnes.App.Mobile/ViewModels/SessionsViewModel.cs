using System.Collections.ObjectModel;
using Agnes.App.Mobile.Services;
using Agnes.Client;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// The home tab: every session this device has open, ordered by how much it wants you.
///
/// The list is the app's answer to "what are my agents doing", so ordering is by need rather than by
/// recency alone: blocked first, then running, then unread, then whatever happened most recently.
/// </summary>
public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly IAppShell _shell;
    private readonly HostBook _hosts;
    private readonly IPromptStore _prompts;
    private readonly IPermissionPolicy _policy;
    private readonly INotifier _notifier;
    private readonly System.Timers.Timer _ageTimer;

    public SessionsViewModel(
        IAppShell shell,
        HostBook hosts,
        IPromptStore prompts,
        IPermissionPolicy policy,
        INotifier notifier)
    {
        _shell = shell;
        _hosts = hosts;
        _prompts = prompts;
        _policy = policy;
        _notifier = notifier;

        OpenCommand = new RelayCommand<SessionEntry>(e => { if (e is not null) { Open(e); } });
        NewSessionCommand = new RelayCommand(StartNew);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ShowHostsCommand = new RelayCommand(() => _shell.ShowSheet(new HostsSheetViewModel(_shell, _hosts, this)));
        EntryActionsCommand = new RelayCommand<SessionEntry>(e => { if (e is not null) { _shell.ShowSheet(new SessionActionsSheetViewModel(_shell, this, e)); } });

        // Relative timestamps go stale silently, which makes a live list look frozen. One cheap tick a
        // minute keeps "4m" honest without touching anything else.
        _ageTimer = new System.Timers.Timer(60_000) { AutoReset = true };
        _ageTimer.Elapsed += (_, _) => _shell.Dispatcher.Post(() =>
        {
            foreach (var entry in All)
            {
                entry.RaiseAge();
            }
        });
        _ageTimer.Start();
    }

    /// <summary>Every known session, in display order.</summary>
    public ObservableCollection<SessionEntry> All { get; } = [];

    public IRelayCommand<SessionEntry> OpenCommand { get; }
    public IRelayCommand NewSessionCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand ShowHostsCommand { get; }
    public IRelayCommand<SessionEntry> EntryActionsCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isRestoring;

    /// <summary>True when there is genuinely nothing to show (as opposed to "not loaded yet").</summary>
    public bool IsEmpty => All.Count == 0 && !IsRestoring;

    /// <summary>How many sessions are blocked on the user — the Inbox tab's badge.</summary>
    public int AttentionCount => All.Count(e => e.NeedsAttention);

    /// <summary>The connection summary shown in the header chip.</summary>
    public string HostSummary
    {
        get
        {
            var real = _hosts.Real.ToList();
            if (real.Count == 0)
            {
                // Nothing paired yet — name the built-in demo rather than claiming nothing is connected,
                // which would be both wrong and discouraging on a first launch.
                // The built-in demo is in-memory, so whether it has "connected" yet is immaterial — what
                // matters is that there's something to look at and nothing paired.
                return _hosts.Links.Any(h => h.IsBuiltIn) ? "Demo host · not paired yet" : "No host connected";
            }

            var online = real.Count(h => h.IsOnline);
            return real.Count == 1 ? real[0].Name : $"{online}/{real.Count} hosts online";
        }
    }

    public bool AnyHostOnline => _hosts.Links.Any(h => h.IsOnline);

    // ---- restore ----

    /// <summary>
    /// Rebuilds the list from local state, then reattaches each session in the background. The rows
    /// appear immediately (from what the device remembers) and fill in as their host connects, so a
    /// cold start on a slow network still shows you your sessions rather than a spinner.
    /// </summary>
    public async Task RestoreAsync()
    {
        _shell.Dispatcher.Post(() => IsRestoring = true);

        var saved = SessionRegistry.Load();
        _shell.Dispatcher.Post(() =>
        {
            All.Clear();
            foreach (var entry in saved)
            {
                var host = _hosts.Find(entry.HostUrl) ?? _hosts.Add(new SavedHost(entry.HostName, entry.HostUrl, entry.Token));
                var row = new SessionEntry(entry, host) { IsLoading = true, Pinned = entry.Pinned };
                row.Changed += _ => Resort();
                All.Add(row);
            }

            RaiseSummary();
        });

        await _hosts.ConnectAllAsync().ConfigureAwait(false);
        _shell.Dispatcher.Post(RaiseSummary);

        // Reattach in parallel — each is an independent snapshot+tail, and a phone waking up wants them
        // all back at once, not serially.
        await Task.WhenAll(All.ToList().Select(AttachAsync)).ConfigureAwait(false);

        _shell.Dispatcher.Post(() =>
        {
            IsRestoring = false;
            Resort();
            RaiseSummary();
        });
    }

    /// <summary>Reconnects hosts and reattaches anything that isn't live (pull-to-refresh).</summary>
    public async Task RefreshAsync()
    {
        await _hosts.ConnectAllAsync().ConfigureAwait(false);
        _shell.Dispatcher.Post(RaiseSummary);
        await Task.WhenAll(All.Where(e => !e.IsLive).ToList().Select(AttachAsync)).ConfigureAwait(false);
        _shell.Dispatcher.Post(() => { Resort(); RaiseSummary(); });
    }

    private async Task AttachAsync(SessionEntry entry)
    {
        if (entry.IsLive)
        {
            return;
        }

        _shell.Dispatcher.Post(() => { entry.IsLoading = true; entry.Error = null; entry.RaiseAll(); });

        var host = await entry.Host.ConnectAsync().ConfigureAwait(false);
        if (host is null)
        {
            _shell.Dispatcher.Post(() =>
            {
                entry.IsLoading = false;
                entry.Error = entry.Host.Error ?? "Host unreachable";
                entry.RaiseAll();
            });
            return;
        }

        try
        {
            var view = await host.SubscribeAsync(entry.SessionId).ConfigureAwait(false);
            _shell.Dispatcher.Post(() =>
            {
                var session = CreateSession(host, view, entry.Saved.Title);
                entry.Attach(session);
                WireTitle(entry, session);
                Resort();
            });
        }
        catch (Exception ex)
        {
            _shell.Dispatcher.Post(() =>
            {
                entry.IsLoading = false;
                entry.Error = ex.Message;
                entry.RaiseAll();
            });
        }
    }

    /// <summary>Builds a session view model wired to this app's stores, policy and notifier.</summary>
    private SessionViewModel CreateSession(IAgnesHost host, SessionView view, string title)
    {
        var session = new SessionViewModel(host, view, _shell.Dispatcher, title, _prompts, _policy);
        session.NotificationRaised += n =>
        {
            _notifier.Notify(n);
            // In the foreground the shade is the wrong surface, so the same fact arrives as a toast plus
            // a haptic that matches its urgency.
            _shell.Dispatcher.Post(() =>
            {
                switch (n.Kind)
                {
                    case NotificationKind.Blocker:
                        _shell.Haptics.Alert();
                        _shell.Toast(n.Title, ToastKind.Warning);
                        break;
                    case NotificationKind.Error:
                        _shell.Haptics.Alert();
                        _shell.Toast(n.Body, ToastKind.Danger);
                        break;
                    default:
                        _shell.Haptics.Success();
                        break;
                }
            });
        };
        return session;
    }

    private void WireTitle(SessionEntry entry, SessionViewModel session)
    {
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.AgentTitle) && session.HasAgentTitle)
            {
                entry.UpdateSavedTitle(session.AgentTitle!);
                Persist();
            }
        };
    }

    // ---- opening / creating ----

    public void Open(SessionEntry entry)
    {
        _shell.Haptics.Tick();
        (_shell as ShellViewModel)?.ClearNotification(entry.SessionId);

        if (entry.Session is { } session)
        {
            _shell.Push(new SessionPageViewModel(_shell, this, entry, session));
            return;
        }

        // Not attached yet (offline start, or a failed reattach): push the page anyway with a retry
        // affordance, then try again — landing on the session is what the tap asked for.
        _shell.Push(new SessionPageViewModel(_shell, this, entry, session: null));
        _ = AttachAsync(entry);
    }

    /// <summary>
    /// Opens a session named by a shared link. It may be one this phone already tracks, or one it has never
    /// seen — a colleague's link points at a session on a host you have access to, not necessarily at
    /// something already in your list — so an unknown id is adopted rather than refused.
    /// </summary>
    public void OpenById(HostLink host, string sessionId, long? sequence)
    {
        var entry = All.FirstOrDefault(s => s.SessionId == sessionId && s.Host == host);
        if (entry is null)
        {
            var saved = new SavedSession(host.Name, host.Url, host.Saved.Token, sessionId, AdapterId: string.Empty, Title: "Shared session");
            entry = new SessionEntry(saved, host);
            entry.Changed += _ => Resort();
            All.Add(entry);
            Resort();
            Persist();
        }

        Open(entry);
        if (sequence is > 0)
        {
            RevealSequence(entry, sequence.Value);
        }
    }

    /// <summary>
    /// Scrolls to the moment a link named, once the transcript has streamed far enough to contain it. The
    /// session may still be attaching when the page opens, so this retries briefly rather than landing at the
    /// top and looking as though the link's position was ignored.
    /// </summary>
    private void RevealSequence(SessionEntry entry, long sequence)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var landed = false;
                _shell.Dispatcher.Post(() => landed = entry.Session?.ScrollToSequence(sequence) ?? false);
                if (landed)
                {
                    return;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }
        });
    }

    public void StartNew()
    {
        _shell.Haptics.Tick();
        _shell.Push(new NewSessionPageViewModel(_shell, _hosts, this));
    }

    /// <summary>Registers a freshly-opened session and (by default) shows it.</summary>
    public SessionEntry Adopt(HostLink host, SessionViewModel session, SavedSession saved, bool open = true)
    {
        var entry = new SessionEntry(saved, host);
        entry.Changed += _ => Resort();
        entry.Attach(session);
        WireTitle(entry, session);
        All.Insert(0, entry);
        Persist();
        RaiseSummary();
        if (open)
        {
            _shell.Push(new SessionPageViewModel(_shell, this, entry, session));
        }

        return entry;
    }

    /// <summary>
    /// Seeds a session on the built-in offline host, once, on a first launch with nothing paired.
    ///
    /// A remote-agent client is inert until you have a host, and "install it, then go stand up a server
    /// before you can see anything" is a bad first minute. The demo runs the real event pipeline against
    /// a scripted agent, so the first thing you see is honestly how the app behaves — and it primes with
    /// a prompt so there's a transcript with a plan, tool calls and a diff rather than an empty room.
    /// </summary>
    public async Task SeedDemoAsync()
    {
        var link = _hosts.Find(DemoHost.Url);
        if (link is null)
        {
            return;
        }

        var host = await link.ConnectAsync().ConfigureAwait(false);
        if (host is null)
        {
            return;
        }

        try
        {
            var info = await host.OpenSessionAsync("claude-code-native", "/home/you/projects/agnes").ConfigureAwait(false);
            var view = await host.SubscribeAsync(info.SessionId).ConfigureAwait(false);

            _shell.Dispatcher.Post(() =>
            {
                var session = Build(host, view, info.WorkingDirectory);
                var saved = new SavedSession(link.Name, link.Url, string.Empty, info.SessionId,
                    "claude-code-native", info.WorkingDirectory, info.WorkingDirectory);
                Adopt(link, session, saved, open: false);
                session.PromptText = "Plan the change and write the new config file.";
                session.SendCommand.Execute(null);
            });
        }
        catch
        {
            // The demo is a courtesy; a failure here just leaves the normal empty state.
        }
    }

    /// <summary>Wraps a host + view into a live session (used by the new-session flow).</summary>
    public SessionViewModel Build(IAgnesHost host, SessionView view, string title)
        => CreateSession(host, view, title);

    /// <summary>Removes a session from this device's list. The session itself keeps running on the host —
    /// this is "stop showing me", not "stop working".</summary>
    public void Forget(SessionEntry entry)
    {
        All.Remove(entry);
        Persist();
        RaiseSummary();
        _shell.Toast($"Removed {entry.Title} from this device", ToastKind.Info);
    }

    public void TogglePin(SessionEntry entry)
    {
        entry.SetPinned(!entry.Pinned);
        Persist();
        Resort();
    }

    public void Persist() => SessionRegistry.Save(All.Select(e => e.Saved));

    // ---- ordering ----

    private bool _resorting;

    /// <summary>Reorders in place: pinned first, then by need, then by recency.</summary>
    private void Resort()
    {
        if (_resorting)
        {
            return;
        }

        _resorting = true;
        try
        {
            var ordered = All
                .OrderByDescending(e => e.Pinned)
                .ThenBy(e => e.Order.Rank)
                .ThenByDescending(e => e.Order.When)
                .ToList();

            for (var target = 0; target < ordered.Count; target++)
            {
                var current = All.IndexOf(ordered[target]);
                if (current != target)
                {
                    All.Move(current, target);
                }
            }
        }
        finally
        {
            _resorting = false;
        }

        RaiseSummary();
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HostSummary));
        OnPropertyChanged(nameof(AnyHostOnline));
        OnPropertyChanged(nameof(IsEmpty));
        AttentionChanged?.Invoke();
    }

    /// <summary>Raised when the blocked-session count may have changed (drives the Inbox badge).</summary>
    public event Action? AttentionChanged;
}
