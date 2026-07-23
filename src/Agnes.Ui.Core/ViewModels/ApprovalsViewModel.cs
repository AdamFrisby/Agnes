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
/// The cross-session approvals list (notifications/02 tier 1): one place that answers "what needs me right
/// now" by unioning every open permission request across the hosts the client can see, newest first, with a
/// jump-to-session action per item. It's a read-mostly aggregation over data sessions already emit — it never
/// answers a request itself, so existing per-session permission handling is untouched. Framework-agnostic: it
/// talks to whatever <see cref="IAgnesHost"/>s the <paramref name="hosts"/> provider yields, so the desktop
/// app and the offline simulation drive it identically.
/// </summary>
public sealed class ApprovalsViewModel : ObservableObject
{
    private readonly Func<IEnumerable<IAgnesHost>> _hosts;
    private readonly IUiDispatcher _dispatcher;

    public ApprovalsViewModel(Func<IEnumerable<IAgnesHost>> hosts, IUiDispatcher dispatcher)
    {
        _hosts = hosts;
        _dispatcher = dispatcher;

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        JumpCommand = new RelayCommand<ApprovalRow>(Jump);
    }

    /// <summary>The open approvals, most-recent first.</summary>
    public ObservableCollection<ApprovalRow> Approvals { get; } = [];

    /// <summary>How many requests are waiting — drives the "Approvals (N)" affordance.</summary>
    public int Count => Approvals.Count;

    /// <summary>Whether anything is waiting (drives the affordance's visibility/badge).</summary>
    public bool HasApprovals => Approvals.Count > 0;

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public ICommand RefreshCommand { get; }
    public ICommand JumpCommand { get; }

    /// <summary>Raised when the user activates an item, so the shell can focus/open the originating session.</summary>
    public event Action<ApprovalRow>? JumpRequested;

    /// <summary>
    /// Re-queries every host and rebuilds the unified list. Cheap enough to call on demand — e.g. when the
    /// panel opens, or after a permission is answered — so the view stays current without a push channel.
    /// </summary>
    public async Task LoadAsync()
    {
        var rows = new List<ApprovalRow>();
        foreach (var host in _hosts())
        {
            try
            {
                var approvals = await host.GetOpenApprovalsAsync().ConfigureAwait(false);
                rows.AddRange(approvals.Select(a => new ApprovalRow(host, a)));
            }
            catch
            {
                // Best-effort per host: one unreachable host must not blank the whole list.
            }
        }

        // Merge-sort across hosts (each host returns its own newest-first slice).
        rows.Sort((x, y) => y.RequestedAt.CompareTo(x.RequestedAt));
        _dispatcher.Post(() => Rebuild(rows));
    }

    private void Rebuild(IReadOnlyList<ApprovalRow> rows)
    {
        Approvals.Clear();
        foreach (var row in rows)
        {
            Approvals.Add(row);
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasApprovals));
        Status = rows.Count == 0 ? "Nothing needs you right now." : $"{rows.Count} request(s) waiting.";
    }

    private void Jump(ApprovalRow? row)
    {
        if (row is not null)
        {
            JumpRequested?.Invoke(row);
        }
    }
}

/// <summary>One open approval as a bindable row, tagged with the host it lives on so the shell can route the
/// jump-to-session action to the right connection.</summary>
public sealed class ApprovalRow
{
    public ApprovalRow(IAgnesHost host, OpenApproval approval)
    {
        Host = host;
        Approval = approval;
    }

    public IAgnesHost Host { get; }

    public OpenApproval Approval { get; }

    public string SessionId => Approval.SessionId;
    public string RequestId => Approval.RequestId;
    public string Title => Approval.Title;
    public DateTimeOffset RequestedAt => Approval.RequestedAt;
}
