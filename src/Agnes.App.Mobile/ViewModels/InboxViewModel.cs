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

    /// <summary>
    /// Whether this phone may hand over the run of the host as well as admit a guest. Only an owner is
    /// offered the second button: a member's attempt would be refused by the host anyway, and a button
    /// whose only outcome is a refusal teaches nothing.
    /// </summary>
    public bool CanGrantOwner => Host.Role == DeviceRole.Owner;
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
        OpenSharedFileCommand = new RelayCommand<SharedFileRow>(row =>
        {
            if (row is not null)
            {
                // Land on the card, not the top of the transcript: the row exists because you were told
                // about one file, and "here it is" is the whole point of the tap.
                _sessions.OpenAt(row.Entry, row.Sequence);
            }
        });
        AllowCommand = new RelayCommand<BlockerRow>(row => Answer(row, allow: true));
        DenyCommand = new RelayCommand<BlockerRow>(row => Answer(row, allow: false));
        ApproveDeviceCommand = new AsyncRelayCommand<PendingDeviceRow>(row => DecideAsync(row, approve: true));
        ApproveDeviceAsOwnerCommand = new AsyncRelayCommand<PendingDeviceRow>(
            row => DecideAsync(row, approve: true, DeviceRole.Owner));
        DenyDeviceCommand = new AsyncRelayCommand<PendingDeviceRow>(row => DecideAsync(row, approve: false));

        // The blocked list is a live projection of the sessions list, so it re-derives whenever any
        // session's attention state moves rather than being polled.
        _sessions.AttentionChanged += () => _shell.Dispatcher.Post(Rebuild);

        // Files ride the same live-projection idea: an arrival changes no attention state, so it gets its
        // own signal rather than being noticed by accident on the next approval.
        _sessions.SharedFilesChanged += () => _shell.Dispatcher.Post(RebuildSharedFiles);

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

    /// <summary>Files agents sent, newest first, across every session and host.</summary>
    public ObservableCollection<SharedFileRow> SharedFiles { get; } = [];

    public bool HasPendingDevices => PendingDevices.Count > 0;

    public bool HasSharedFiles => SharedFiles.Count > 0;

    public IAsyncRelayCommand<PendingDeviceRow> ApproveDeviceCommand { get; }

    /// <summary>Lets a device in with the run of the host. Shown only on a row whose host says this
    /// phone is an owner.</summary>
    public IAsyncRelayCommand<PendingDeviceRow> ApproveDeviceAsOwnerCommand { get; }

    public IAsyncRelayCommand<PendingDeviceRow> DenyDeviceCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand<BlockerRow> OpenCommand { get; }
    public IRelayCommand<BlockerRow> AllowCommand { get; }
    public IRelayCommand<BlockerRow> DenyCommand { get; }

    /// <summary>Opens the session a received file came from, scrolled to its card.</summary>
    public IRelayCommand<SharedFileRow> OpenSharedFileCommand { get; }

    [ObservableProperty]
    private bool _isRefreshing;

    public int BlockedCount => Blocked.Count;

    public bool HasBlocked => Blocked.Count > 0;

    public bool HasFinished => Finished.Count > 0;

    public bool IsEmpty => !HasBlocked && !HasFinished && !HasPendingDevices && !HasSharedFiles && !IsRefreshing;

    /// <summary>How many rows the "Sent to you" section keeps. It's a recent-things list, not an archive —
    /// the session itself is where a file from last Tuesday lives.</summary>
    private const int SharedFileLimit = 20;

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

        RebuildSharedFiles();
    }

    /// <summary>Re-derives the received-files list from what the live sessions currently hold.</summary>
    private void RebuildSharedFiles()
    {
        var rows = _sessions.All
            .Where(e => e.Session is not null)
            .SelectMany(e => SharedFileAccess.Of(e.Session!).Select(f => new SharedFileRow(e, f)))
            .OrderByDescending(r => r.When)
            .Take(SharedFileLimit)
            .ToList();

        SharedFiles.Clear();
        foreach (var row in rows)
        {
            SharedFiles.Add(row);
        }

        OnPropertyChanged(nameof(HasSharedFiles));
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
                // link.Http, not a default client: a self-signed host is authenticated by its pin, and a
                // default client fails that handshake — which reads here as "no requests waiting".
                var pending = await PairingManagement
                    .PendingAsync(link.Url, link.Saved.Token, link.Http).ConfigureAwait(false);
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
    /// <param name="role">
    /// What the device is admitted as. Member is the ordinary answer and the default button; owner hands
    /// over the host, so it is a second, separately-labelled tap that only an owner is shown.
    /// </param>
    private async Task DecideAsync(PendingDeviceRow? row, bool approve, DeviceRole role = DeviceRole.Member)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            if (approve)
            {
                await PairingManagement
                    .ApproveAsync(row.Host.Url, row.Host.Saved.Token, row.Pending.RequestId, role, row.Host.Http)
                    .ConfigureAwait(false);
            }
            else
            {
                await PairingManagement.DenyAsync(row.Host.Url, row.Host.Saved.Token, row.Pending.RequestId, row.Host.Http)
                    .ConfigureAwait(false);
            }

            _shell.Haptics.Tick();
            _shell.Toast(
                approve
                    ? role == DeviceRole.Owner
                        ? $"{row.DeviceName} is now an owner of {row.Host.Name}"
                        : $"{row.DeviceName} can now use {row.Host.Name}"
                    : "Declined",
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
