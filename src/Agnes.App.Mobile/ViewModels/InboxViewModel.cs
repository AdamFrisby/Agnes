using System.Collections.ObjectModel;
using Agnes.App.Mobile.Services;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>A device asking to be let onto a host, awaiting a human.</summary>
public sealed record PendingDeviceRow(HostLink Host, PendingPairApproval Pending)
{
    public string DeviceName => Pending.DeviceName;

    public string HostName => Host.Name;

    /// <summary>The digits that must match the ones on the asking device's screen.</summary>
    public string VerificationCode => Pending.VerificationCode;
}

/// <summary>One thing blocking an agent, wherever it came from.</summary>
public sealed partial class BlockerRow : ObservableObject
{
    public BlockerRow(SessionEntry entry, string title, string detail, string kind)
    {
        Entry = entry;
        Title = title;
        Detail = detail;
        Kind = kind;
    }

    public SessionEntry Entry { get; }

    public string Title { get; }

    /// <summary>What this touches and whether it can be undone — the two facts that decide the answer.</summary>
    public string Detail { get; }

    /// <summary>"Approval" or "Question".</summary>
    public string Kind { get; }

    public string SessionTitle => Entry.Title;

    public string HostName => Entry.HostName;

    /// <summary>Only a permission request can be answered from the list; a structured question needs its
    /// own options, so that one takes you into the session.</summary>
    public bool CanAnswerHere => Kind == "Approval";
}

/// <summary>
/// The inbox: everything waiting on you, across every session and host, plus what finished while you
/// were away.
///
/// This tab is the reason the phone client exists. An agent blocked on an approval is an agent doing
/// nothing, and unblocking it is a two-second job that shouldn't require a laptop — so approvals are
/// answerable inline here, without opening the session.
/// </summary>
public sealed partial class InboxViewModel : ObservableObject
{
    private readonly IAppShell _shell;
    private readonly HostBook _hosts;
    private readonly SessionsViewModel _sessions;

    public InboxViewModel(IAppShell shell, HostBook hosts, SessionsViewModel sessions)
    {
        _shell = shell;
        _hosts = hosts;
        _sessions = sessions;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenCommand = new RelayCommand<BlockerRow>(row =>
        {
            if (row is not null)
            {
                _sessions.Open(row.Entry);
            }
        });
        AllowCommand = new RelayCommand<BlockerRow>(row => Answer(row, allow: true));
        DenyCommand = new RelayCommand<BlockerRow>(row => Answer(row, allow: false));
        ApproveDeviceCommand = new AsyncRelayCommand<PendingDeviceRow>(row => DecideAsync(row, approve: true));
        DenyDeviceCommand = new AsyncRelayCommand<PendingDeviceRow>(row => DecideAsync(row, approve: false));

        // The blocked list is a live projection of the sessions list, so it re-derives whenever any
        // session's attention state moves rather than being polled.
        _sessions.AttentionChanged += () => _shell.Dispatcher.Post(Rebuild);

        // The join-requests section tracks its own collection, so anything that touches it — a refresh,
        // an answered request — updates the header and the empty state without a second call.
        PendingDevices.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPendingDevices));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    /// <summary>Agents blocked on a human, newest first.</summary>
    public ObservableCollection<BlockerRow> Blocked { get; } = [];

    /// <summary>Background runs that completed while you weren't looking.</summary>
    public ObservableCollection<InboxRun> Finished { get; } = [];

    /// <summary>Devices asking to be let onto a host this phone is already paired with. This belongs in
    /// the inbox for the same reason approvals do: it is a thing waiting on a human.</summary>
    public ObservableCollection<PendingDeviceRow> PendingDevices { get; } = [];

    public bool HasPendingDevices => PendingDevices.Count > 0;

    public IAsyncRelayCommand<PendingDeviceRow> ApproveDeviceCommand { get; }

    public IAsyncRelayCommand<PendingDeviceRow> DenyDeviceCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand<BlockerRow> OpenCommand { get; }
    public IRelayCommand<BlockerRow> AllowCommand { get; }
    public IRelayCommand<BlockerRow> DenyCommand { get; }

    [ObservableProperty]
    private bool _isRefreshing;

    public int BlockedCount => Blocked.Count;

    public bool HasBlocked => Blocked.Count > 0;

    public bool HasFinished => Finished.Count > 0;

    public bool IsEmpty => !HasBlocked && !HasFinished && !HasPendingDevices && !IsRefreshing;

    /// <summary>Rebuilds the blocked list from what the live sessions currently report.</summary>
    private void Rebuild()
    {
        Blocked.Clear();
        foreach (var entry in _sessions.All)
        {
            if (entry.Session is not { } session)
            {
                continue;
            }

            if (session.PendingPermission is { } permission)
            {
                Blocked.Add(new BlockerRow(entry, permission.Title,
                    $"{permission.ResourceText} · {permission.ReversibleText}", "Approval"));
            }

            if (session.PendingQuestion is { } question)
            {
                var header = question.Questions.FirstOrDefault()?.Header ?? "The agent asked you something";
                Blocked.Add(new BlockerRow(entry, header,
                    question.Questions.FirstOrDefault()?.Prompt ?? string.Empty, "Question"));
            }
        }

        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(HasBlocked));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Refreshes the finished-runs list from every connected host, and re-derives the blocked
    /// list. Failures are per-host and silent — one unreachable host must not blank the tab.</summary>
    public async Task RefreshAsync()
    {
        _shell.Dispatcher.Post(() => { IsRefreshing = true; Rebuild(); });

        var runs = new List<InboxRun>();
        foreach (var link in _hosts.Links)
        {
            if (link.Host is not { } host)
            {
                continue;
            }

            try
            {
                runs.AddRange(await host.GetInboxAsync().ConfigureAwait(false));
            }
            catch
            {
                // A host without scheduled tasks (or momentarily unreachable) contributes nothing.
            }
        }

        // Devices asking to join, from every host this device could vouch on.
        var waiting = new List<PendingDeviceRow>();
        foreach (var link in _hosts.Real.Where(l => l.IsOnline))
        {
            try
            {
                var pending = await PairingManagement
                    .PendingAsync(link.Url, link.Saved.Token).ConfigureAwait(false);
                waiting.AddRange(pending.Select(p => new PendingDeviceRow(link, p)));
            }
            catch
            {
                // A host that predates approval pairing simply has none.
            }
        }

        _shell.Dispatcher.Post(() =>
        {
            Finished.Clear();
            foreach (var run in runs.OrderByDescending(r => r.CompletedAt).Take(50))
            {
                Finished.Add(run);
            }

            PendingDevices.Clear();
            foreach (var row in waiting.OrderByDescending(r => r.Pending.RequestedAt))
            {
                PendingDevices.Add(row);
            }

            IsRefreshing = false;
            OnPropertyChanged(nameof(HasFinished));
            OnPropertyChanged(nameof(HasPendingDevices));
            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    /// <summary>
    /// Approves or declines a device. Approving mints its token host-side; declining is deliberately
    /// just as easy to reach, because "I didn't expect this" should be the cheap answer.
    /// </summary>
    private async Task DecideAsync(PendingDeviceRow? row, bool approve)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            if (approve)
            {
                await PairingManagement.ApproveAsync(row.Host.Url, row.Host.Saved.Token, row.Pending.RequestId)
                    .ConfigureAwait(false);
            }
            else
            {
                await PairingManagement.DenyAsync(row.Host.Url, row.Host.Saved.Token, row.Pending.RequestId)
                    .ConfigureAwait(false);
            }

            _shell.Haptics.Tick();
            _shell.Toast(approve ? $"{row.DeviceName} can now use {row.Host.Name}" : "Declined",
                approve ? ToastKind.Success : ToastKind.Warning);
        }
        catch (Exception ex)
        {
            _shell.Toast("Couldn't answer that request: " + ex.Message, ToastKind.Danger);
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    private void Answer(BlockerRow? row, bool allow)
    {
        if (row?.Entry.Session is not { } session)
        {
            return;
        }

        (allow ? session.AllowCommand : session.DenyCommand).Execute(null);
        _shell.Haptics.Tick();
        _shell.Toast(allow ? $"Allowed — {row.SessionTitle} is moving again" : "Denied", allow ? ToastKind.Success : ToastKind.Warning);
        Rebuild();
    }
}
