using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// "What is already running over there?" — the host's session catalogue as a bindable list, so a client that
/// has just paired (or reconnected) can rejoin work in progress instead of only being able to start something
/// new. It asks each host for the sessions <em>this</em> caller may reach; the host applies the same access
/// check subscribing does, so nothing here is offered that would then be refused.
///
/// <para>Read-only by construction: loading lists, it never attaches. Activating a row raises
/// <see cref="AttachRequested"/> and the shell decides what attaching means on its surface — a tab on the
/// desktop, a pushed page on a phone. That is what lets both heads share this one aggregation.</para>
/// </summary>
public sealed class SessionCatalogViewModel : ObservableObject
{
    private readonly Func<IEnumerable<IAgnesHost>> _hosts;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string, bool> _isAlreadyOpen;

    /// <param name="hosts">The hosts to ask. A single-host surface passes one; the desktop dashboard passes
    /// every connected host, and each row remembers which host it came from.</param>
    /// <param name="isAlreadyOpen">Whether this client already has the given session id open, so the row can
    /// say "open" rather than offer to attach to it a second time. Defaults to "nothing is open".</param>
    public SessionCatalogViewModel(
        Func<IEnumerable<IAgnesHost>> hosts,
        IUiDispatcher dispatcher,
        Func<string, bool>? isAlreadyOpen = null)
    {
        _hosts = hosts;
        _dispatcher = dispatcher;
        _isAlreadyOpen = isAlreadyOpen ?? (_ => false);

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        AttachCommand = new RelayCommand<CatalogSessionRow>(Attach);
    }

    /// <summary>The catalogued sessions, ordered by how much they want a human.</summary>
    public ObservableCollection<CatalogSessionRow> Sessions { get; } = [];

    public int Count => Sessions.Count;

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>How many of the listed sessions are blocked on a human — the count worth badging.</summary>
    public int BlockedCount => Sessions.Count(s => s.IsBlocked);

    private bool _isLoading;

    /// <summary>True while a load is in flight, so a surface can show progress instead of a false "none".</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>Nothing to show, and not merely still asking — the state an empty screen should render for.
    /// Distinguished from "no sessions yet" so a slow host never flashes "nothing is running" at someone
    /// whose session is in fact running.</summary>
    public bool IsEmpty => Sessions.Count == 0 && !IsLoading;

    private string _status = string.Empty;

    /// <summary>A one-line summary of the last load (count found, or why it found nothing).</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ICommand RefreshCommand { get; }
    public ICommand AttachCommand { get; }

    /// <summary>Raised when the user picks a session to join. The shell attaches it however it attaches
    /// sessions — this view model never subscribes to anything itself.</summary>
    public event Action<CatalogSessionRow>? AttachRequested;

    /// <summary>
    /// Re-asks every host and rebuilds the list. Cheap enough to call whenever the surface opens — it is a
    /// read of state the host already holds, and it neither opens nor resumes a session.
    /// </summary>
    public async Task LoadAsync()
    {
        _dispatcher.Post(() => IsLoading = true);

        var rows = new List<CatalogSessionRow>();
        var failed = 0;
        var hosts = _hosts().ToList();
        foreach (var host in hosts)
        {
            try
            {
                var summaries = await host.ListSessionsAsync().ConfigureAwait(false);
                rows.AddRange(summaries.Select(s => new CatalogSessionRow(host, s, _isAlreadyOpen(s.SessionId))));
            }
            catch
            {
                // Best-effort per host: one unreachable (or older) host must not blank the whole list.
                failed++;
            }
        }

        rows.Sort(Compare);
        _dispatcher.Post(() => Rebuild(rows, hosts.Count, failed));
    }

    /// <summary>Blocked first, then working, then whatever ran most recently — the same "by need, not by
    /// name" ordering the session list uses, because the question being asked is "what should I pick up?".</summary>
    private static int Compare(CatalogSessionRow x, CatalogSessionRow y)
    {
        var rank = x.Rank.CompareTo(y.Rank);
        return rank != 0 ? rank : y.LastActivityAt.CompareTo(x.LastActivityAt);
    }

    private void Rebuild(IReadOnlyList<CatalogSessionRow> rows, int hostCount, int failed)
    {
        Sessions.Clear();
        foreach (var row in rows)
        {
            Sessions.Add(row);
        }

        IsLoading = false;
        var plural = rows.Count == 1 ? string.Empty : "s";
        Status = rows.Count > 0
            ? hostCount > 1
                ? $"{rows.Count} session{plural} across {hostCount} hosts"
                : $"{rows.Count} session{plural} on this host"
            : hostCount == 0 ? "No host connected."
            : failed == hostCount ? "Couldn't ask the host what's running."
            : "Nothing is running here yet.";

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(BlockedCount));
    }

    /// <summary>Marks a row as open in this client (after the shell attaches it), without a reload.</summary>
    public void MarkOpen(string sessionId)
    {
        foreach (var row in Sessions.Where(r => r.SessionId == sessionId))
        {
            row.IsAlreadyOpen = true;
        }
    }

    private void Attach(CatalogSessionRow? row)
    {
        if (row is not null)
        {
            AttachRequested?.Invoke(row);
        }
    }
}

/// <summary>
/// One catalogued session as a bindable row, tagged with the host it lives on so the shell routes the attach
/// to the right connection. Everything here is derived from the <see cref="SessionSummary"/> the host sent —
/// the row holds no live subscription, which is the point: listing is cheap, joining is deliberate.
/// </summary>
public sealed partial class CatalogSessionRow : ObservableObject
{
    public CatalogSessionRow(IAgnesHost host, SessionSummary summary, bool isAlreadyOpen = false)
    {
        Host = host;
        Summary = summary;
        _isAlreadyOpen = isAlreadyOpen;
    }

    public IAgnesHost Host { get; }

    public SessionSummary Summary { get; }

    public string SessionId => Summary.SessionId;
    public string AdapterId => Summary.AdapterId;
    public string WorkingDirectory => Summary.WorkingDirectory;
    public string HostUrl => Host.HostUrl;

    /// <summary>Whether this client already has the session open, so the row offers "Go to it" instead of
    /// opening a second view of the same conversation.</summary>
    [ObservableProperty]
    private bool _isAlreadyOpen;

    /// <summary>The agent's name for the conversation, falling back to the working folder, then the id —
    /// a row is never blank.</summary>
    public string Title => Summary.Title is { Length: > 0 } t ? t
        : FolderName is { Length: > 0 } f ? f
        : Summary.SessionId;

    /// <summary>The leaf of the working directory ("agnes"), which is how people name their projects.</summary>
    public string FolderName => LeafOf(Summary.WorkingDirectory);

    public bool IsWorking => Summary.State == SessionRunState.Working;
    public bool IsDormant => Summary.State == SessionRunState.Dormant;
    public bool IsBlocked => Summary.IsBlocked;
    public bool IsReadOnly => Summary.ReadOnly;

    /// <summary>What the session is doing, in the app's one-meaning-per-hue vocabulary: "needs you" beats
    /// "working", because a blocked session is the one the human can actually unstick.</summary>
    public string StateText => IsBlocked
        ? Summary.OpenApprovals == 1 ? "Needs you" : $"Needs you ({Summary.OpenApprovals})"
        : Summary.State switch
        {
            SessionRunState.Working => "Working",
            SessionRunState.Idle => "Idle",
            _ => "Not loaded",
        };

    /// <summary>When the session last did anything, as "now / 4m / 2h / 3d" (empty if it never has).</summary>
    public string Age => RelativeTime.Format(Summary.LastActivityAt);

    /// <summary>Sort key — see <see cref="SessionCatalogViewModel"/>: blocked, then working, then live, then
    /// dormant.</summary>
    public int Rank => IsBlocked ? 0 : Summary.State switch
    {
        SessionRunState.Working => 1,
        SessionRunState.Idle => 2,
        _ => 3,
    };

    public DateTimeOffset LastActivityAt => Summary.LastActivityAt ?? Summary.StartedAt ?? DateTimeOffset.MinValue;

    /// <summary>Re-raises the relative timestamp, ticked by whatever surface shows it.</summary>
    public void RaiseAge() => OnPropertyChanged(nameof(Age));

    private static string LeafOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }
}
