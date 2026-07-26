using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// The status dashboard: one optional tab that answers "what is everything doing, and what wants me?"
/// without opening a single session. It is deliberately a document like any other — you open it when you
/// want it and close it when you don't — because the workbench's job is the session you're in, and a
/// permanently-docked overview would compete with it.
///
/// <para>Everything on it is derived: the approvals come from the same cross-session aggregation the top bar
/// badges, the live cards mirror the tabs this window already holds, and the "elsewhere" list is the hosts'
/// own session catalogue. The dashboard owns no session state of its own.</para>
/// </summary>
public sealed class DashboardDocument : Document, IDisposable
{
    public DashboardDocument(DashboardViewModel dashboard)
    {
        Dashboard = dashboard;
        Id = "dashboard";
        Title = "Dashboard";
        CanClose = true;
    }

    public DashboardViewModel Dashboard { get; }

    /// <summary>Closing the tab stops its polling — an overview nobody is looking at must not keep asking
    /// every host what it's doing. The dock factory disposes closed dockables that ask to be.</summary>
    public void Dispose() => Dashboard.Dispose();
}

/// <summary>The dashboard's state: attention first, then live work, then what's running elsewhere.</summary>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly IUiDispatcher _dispatcher;
    private readonly System.Timers.Timer _tick;

    public DashboardViewModel(MainWindowViewModel owner, IUiDispatcher dispatcher, Func<IEnumerable<IAgnesHost>> hosts)
    {
        _owner = owner;
        _dispatcher = dispatcher;

        Catalog = new SessionCatalogViewModel(hosts, dispatcher, owner.IsSessionOpen);
        Catalog.AttachRequested += row => _ = _owner.JoinFromDashboardAsync(row);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        FocusCommand = new RelayCommand<DashboardSessionRow>(row => { if (row is not null) { _owner.ActivateSessionById(row.SessionId); } });
        OpenNoticeCommand = new RelayCommand<DashboardNotice>(notice =>
        {
            if (notice?.SessionId is { Length: > 0 } id)
            {
                _owner.ActivateSessionById(id);
            }
        });

        // A dashboard that quietly goes stale is worse than no dashboard: it makes a finished run look like a
        // running one. One cheap tick a half-minute re-asks the hosts and re-dates every relative timestamp.
        _tick = new System.Timers.Timer(30_000) { AutoReset = true };
        _tick.Elapsed += (_, _) => _ = RefreshAsync();
        _tick.Start();
    }

    /// <summary>The cross-session approvals list — the window's, not a second copy, so answering one anywhere
    /// updates everywhere.</summary>
    public ApprovalsViewModel Approvals => _owner.Approvals;

    /// <summary>Every session the connected hosts know about, whether or not it's open here.</summary>
    public SessionCatalogViewModel Catalog { get; }

    /// <summary>One card per session open in this window, mirroring its live progress.</summary>
    public ObservableCollection<DashboardSessionRow> Live { get; } = [];

    /// <summary>Sessions on the connected hosts that aren't open in this window — each offering to join.</summary>
    public ObservableCollection<CatalogSessionRow> Elsewhere { get; } = [];

    /// <summary>Things worth saying out loud that aren't approvals: a host that dropped, a session that
    /// faulted. Same prominent band, because both mean "you need to look".</summary>
    public ObservableCollection<DashboardNotice> Notices { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>Jumps to the tab holding a live session.</summary>
    public IRelayCommand<DashboardSessionRow> FocusCommand { get; }

    /// <summary>Jumps to the session a notice is about (a no-op for one that names no session).</summary>
    public IRelayCommand<DashboardNotice> OpenNoticeCommand { get; }

    public int WorkingCount => Live.Count(r => r.IsWorking);

    /// <summary>How many distinct things are waiting on a human: every blocked open session, plus every
    /// approval that isn't already counted by one of them (an external request, or a session not open here).
    /// Counted this way so a permission card doesn't inflate the badge twice.</summary>
    public int BlockedCount => Live.Count(r => r.NeedsAttention)
        + Approvals.Approvals.Count(a => a.SessionId is null || Live.All(r => r.SessionId != a.SessionId));
    public int LiveCount => Live.Count;
    public int ElsewhereCount => Elsewhere.Count;

    /// <summary>Whether the counter pills have anything to say. Spelled as bools rather than binding the
    /// counts straight to <c>IsVisible</c>, so a zero can never render as a "0 working" badge.</summary>
    public bool HasWorking => WorkingCount > 0;

    public bool HasBlocked => BlockedCount > 0;

    public bool HasLive => Live.Count > 0;
    public bool HasElsewhere => Elsewhere.Count > 0;
    public bool HasNotices => Notices.Count > 0;

    /// <summary>Whether anything at all wants a human — drives the attention band's presence.</summary>
    public bool HasAttention => Approvals.HasApprovals || Notices.Count > 0;

    /// <summary>Nothing running and nothing to join: say so plainly rather than showing three empty sections.</summary>
    public bool IsEmpty => Live.Count == 0 && Elsewhere.Count == 0 && !Catalog.IsLoading;

    /// <summary>
    /// Re-asks the hosts for approvals and their session catalogue, then rebuilds the three sections from what
    /// this window currently holds. Cheap and idempotent — it reads, it never opens or resumes anything.
    /// </summary>
    public async Task RefreshAsync()
    {
        await Task.WhenAll(Approvals.LoadAsync(), Catalog.LoadAsync()).ConfigureAwait(false);
        _dispatcher.Post(Rebuild);
    }

    /// <summary>Rebuilds the live cards, the "elsewhere" list and the notices from current state.</summary>
    public void Rebuild()
    {
        foreach (var row in Live)
        {
            row.Detach();
        }

        Live.Clear();
        foreach (var doc in _owner.OpenSessions.Where(d => d.Session is not null))
        {
            var row = new DashboardSessionRow(doc);
            row.Changed += RaiseCounts;
            Live.Add(row);
        }

        Elsewhere.Clear();
        foreach (var row in Catalog.Sessions.Where(r => !_owner.IsSessionOpen(r.SessionId)))
        {
            Elsewhere.Add(row);
        }

        Notices.Clear();
        // A session waiting on a permission answer belongs in the attention band whether or not the host's
        // approvals aggregation happens to list it — the tab already knows it's blocked, and "something is
        // stuck" is the fact the band exists to carry.
        foreach (var row in Live.Where(r => r.IsAwaitingInput))
        {
            Notices.Add(new DashboardNotice($"{row.Title} is waiting for you", row.ActivityText, row.SessionId));
        }

        foreach (var row in Live.Where(r => r.IsFaulted))
        {
            Notices.Add(new DashboardNotice($"{row.Title} stopped with an error",
                "Open the session to see what happened.", row.SessionId, IsError: true));
        }

        foreach (var host in Catalog.Sessions.Select(r => r.Host).Distinct().Where(h => h.State != AgnesConnectionState.Connected))
        {
            Notices.Add(new DashboardNotice($"{host.HostUrl} is {host.State.ToString().ToLowerInvariant()}",
                "Sessions on this host can't be reached until it reconnects.", SessionId: null, IsError: true));
        }

        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(WorkingCount));
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(HasWorking));
        OnPropertyChanged(nameof(HasBlocked));
        OnPropertyChanged(nameof(LiveCount));
        OnPropertyChanged(nameof(ElsewhereCount));
        OnPropertyChanged(nameof(HasLive));
        OnPropertyChanged(nameof(HasElsewhere));
        OnPropertyChanged(nameof(HasNotices));
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Dispose()
    {
        _tick.Stop();
        _tick.Dispose();
        foreach (var row in Live)
        {
            row.Detach();
        }
    }
}

/// <summary>
/// One open session as a dashboard card: what it's called, where it runs, what it's doing right now, and how
/// far through its plan it is. It mirrors the tab rather than re-deriving anything — the tab already tracks
/// activity for the tab strip, so a card is a second view of that one truth.
/// </summary>
public sealed class DashboardSessionRow : ObservableObject
{
    private readonly SessionDocument _doc;
    private PlanItemView? _plan;

    public DashboardSessionRow(SessionDocument doc)
    {
        _doc = doc;
        doc.PropertyChanged += OnDocChanged;
        if (doc.Session is { } session)
        {
            session.PropertyChanged += OnSessionChanged;
            session.ToolActivity.CollectionChanged += (_, _) => RaiseAll();
            WatchPlan(session.Plan);
        }
    }

    /// <summary>The tab this card mirrors, so the view can bind through to it and the shell can focus it.</summary>
    public SessionDocument Document => _doc;

    public string SessionId => _doc.Session?.SessionId ?? string.Empty;
    public string Title => _doc.Title ?? "session";
    public string HostName => _doc.HostName;
    public string AgentName => _doc.AgentName;
    public string WorkingDirectory => _doc.WorkingDirectory;

    public bool IsWorking => _doc.IsWorking;
    public bool IsAwaitingInput => _doc.IsAwaitingInput;
    public bool IsReadyForReview => _doc.IsReadyForReview;
    public bool IsFaulted => _doc.IsFaulted;
    public bool NeedsAttention => _doc.NeedsAttention;
    public bool IsUnread => _doc.IsUnread;
    public string ActivityText => _doc.ActivityText;
    public string? UsageSummary => _doc.UsageSummary;

    /// <summary>The tool the agent is running right now, or the last one it ran — the single most useful
    /// "what is it actually doing" line, and the reason a dashboard beats a spinner.</summary>
    public string CurrentStep
    {
        get
        {
            var tools = _doc.Session?.ToolActivity;
            if (tools is null || tools.Count == 0)
            {
                return string.Empty;
            }

            var running = tools.LastOrDefault(t => t.IsRunning);
            return (running ?? tools[^1]).Name;
        }
    }

    public bool HasCurrentStep => CurrentStep.Length > 0;

    // ---- plan progress ----

    private IReadOnlyList<PlanEntry> Entries => _plan?.Entries ?? [];

    public bool HasPlan => Entries.Count > 0;

    public int PlanTotal => Entries.Count;

    public int PlanDone => Entries.Count(e => string.Equals(e.Status, "completed", StringComparison.OrdinalIgnoreCase));

    /// <summary>Plan completion 0..1, for a progress bar. Zero (not one) when there is no plan, so an
    /// unplanned session never reads as "finished".</summary>
    public double PlanFraction => PlanTotal == 0 ? 0 : (double)PlanDone / PlanTotal;

    public string PlanText => PlanTotal == 0 ? string.Empty : $"{PlanDone}/{PlanTotal} steps";

    /// <summary>Raised when anything the dashboard's counters depend on may have changed.</summary>
    public event Action? Changed;

    /// <summary>Stops mirroring the tab (called when the dashboard rebuilds or closes).</summary>
    public void Detach()
    {
        _doc.PropertyChanged -= OnDocChanged;
        if (_doc.Session is { } session)
        {
            session.PropertyChanged -= OnSessionChanged;
        }

        WatchPlan(null);
    }

    private void OnDocChanged(object? sender, PropertyChangedEventArgs e) => RaiseAll();

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.Plan))
        {
            WatchPlan(_doc.Session?.Plan);
        }

        RaiseAll();
    }

    private void WatchPlan(PlanItemView? plan)
    {
        if (ReferenceEquals(plan, _plan))
        {
            return;
        }

        if (_plan is not null)
        {
            _plan.PropertyChanged -= OnPlanChanged;
        }

        _plan = plan;
        if (_plan is not null)
        {
            _plan.PropertyChanged += OnPlanChanged;
        }
    }

    private void OnPlanChanged(object? sender, PropertyChangedEventArgs e) => RaiseAll();

    /// <summary>Re-raises every derived property. They're all cheap reads off one tab, and a targeted
    /// invalidation map would be more code and more ways to miss one.</summary>
    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HostName));
        OnPropertyChanged(nameof(AgentName));
        OnPropertyChanged(nameof(WorkingDirectory));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsAwaitingInput));
        OnPropertyChanged(nameof(IsReadyForReview));
        OnPropertyChanged(nameof(IsFaulted));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(IsUnread));
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(UsageSummary));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(HasCurrentStep));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(PlanDone));
        OnPropertyChanged(nameof(PlanTotal));
        OnPropertyChanged(nameof(PlanFraction));
        OnPropertyChanged(nameof(PlanText));
        Changed?.Invoke();
    }
}

/// <summary>One non-approval notice on the dashboard's attention band (a blocked or faulted session, a
/// dropped host). <paramref name="SessionId"/> is the session to jump to, or null when there isn't one.</summary>
public sealed record DashboardNotice(string Title, string Detail, string? SessionId = null, bool IsError = false);
