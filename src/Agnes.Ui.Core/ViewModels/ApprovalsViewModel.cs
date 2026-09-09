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

    /// <summary>Answers an external attention request row with the chosen option, then refreshes the list so
    /// the now-resolved entry drops out. A no-op for a session-permission row (those are answered in-session).</summary>
    public async Task AnswerExternalAsync(ApprovalRow row, string option)
    {
        if (!row.IsExternal)
        {
            return;
        }

        await row.Host.AnswerAttentionRequestAsync(row.RequestId, option).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>Resolves an approval-gated action row (notifications/02 tier 2): approve runs the parked action,
    /// reject turns it down. Then refreshes so the now-resolved entry drops out. A no-op for any other row kind
    /// (session permissions are answered in-session; external attention requests via
    /// <see cref="AnswerExternalAsync"/>).</summary>
    public async Task ResolveGatedAsync(ApprovalRow row, bool approve)
    {
        if (!row.IsGatedAction)
        {
            return;
        }

        await row.Host.ResolveGatedApprovalAsync(row.RequestId, approve).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>The open approvals, most-recent first.</summary>
    public ObservableCollection<ApprovalRow> Approvals { get; } = [];

    /// <summary>
    /// How many requests are waiting — drives the "Approvals (N)" affordance. Counts only what someone
    /// can actually act on: an expired request is still listed, for review, but a badge that counts it is
    /// telling the user to go and do something impossible, which is how one host advertised sixteen
    /// approvals that could never be cleared.
    /// </summary>
    public int Count => Approvals.Count(a => a.IsActionable);

    /// <summary>Whether anything is waiting (drives the affordance's visibility/badge).</summary>
    public bool HasApprovals => Count > 0;

    /// <summary>Requests the agent stopped waiting for — nothing to answer, but worth coming back to,
    /// and the place a standing rule gets set so the next one isn't missed the same way.</summary>
    public IEnumerable<ApprovalRow> Expired => Approvals.Where(a => !a.IsActionable);

    public int ExpiredCount => Approvals.Count(a => !a.IsActionable);

    public bool HasExpired => ExpiredCount > 0;

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
                rows.AddRange(approvals.Select(a => new ApprovalRow(host, a, this)));
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
        OnPropertyChanged(nameof(Expired));
        OnPropertyChanged(nameof(ExpiredCount));
        OnPropertyChanged(nameof(HasExpired));

        var waiting = Count;
        var lapsed = ExpiredCount;
        Status = (waiting, lapsed) switch
        {
            (0, 0) => "Nothing needs you right now.",
            (0, _) => $"Nothing needs you right now — {lapsed} request(s) expired unanswered.",
            (_, 0) => $"{waiting} request(s) waiting.",
            _ => $"{waiting} request(s) waiting · {lapsed} expired unanswered.",
        };
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
/// jump-to-session action to the right connection. An external attention request (extensibility/06) is the
/// same row shape — <see cref="IsExternal"/> is true, <see cref="SessionId"/> is null (nothing to jump to),
/// <see cref="Source"/> labels the caller, and <see cref="Options"/> are the answers to offer.</summary>
public sealed class ApprovalRow
{
    /// <param name="owner">The list this row belongs to, so the row can offer the answers it accepts as
    /// ready-to-bind commands. Optional: a row built without one is inert data (what tests and fixtures
    /// want), and a session permission is answered in its session either way.</param>
    public ApprovalRow(IAgnesHost host, OpenApproval approval, ApprovalsViewModel? owner = null)
    {
        Host = host;
        Approval = approval;
        Choices = owner is null ? [] : BuildChoices(owner);
    }

    public IAgnesHost Host { get; }

    public OpenApproval Approval { get; }

    public string? SessionId => Approval.SessionId;
    public string RequestId => Approval.RequestId;
    public string Title => Approval.Title;
    public DateTimeOffset RequestedAt => Approval.RequestedAt;

    /// <summary>True for an external attention request (labeled with <see cref="Source"/>, answered by id);
    /// false for an in-session agent permission request (jumped to and answered in the session view).</summary>
    public bool IsExternal => Approval.Kind == OpenApprovalKind.ExternalAttention;

    /// <summary>True for an approval-gated action (notifications/02 tier 2), resolved with approve/reject via
    /// <see cref="ApprovalsViewModel.ResolveGatedAsync"/>; <see cref="Source"/> is the action id.</summary>
    public bool IsGatedAction => Approval.Kind == OpenApprovalKind.GatedAction;

    /// <summary>The external caller's free-text label, or (for a gated action) the action id.</summary>
    public string? Source => Approval.Source;

    /// <summary>Whether answering this still does anything — false once the agent stopped waiting.</summary>
    public bool IsActionable => Approval.IsActionable;

    /// <summary>Why this row offers nothing to press, in words.</summary>
    public string? LapsedText => IsActionable ? null : "Expired — the agent stopped waiting";

    /// <summary>The answer choices for an external attention request (empty for a session permission).</summary>
    public IReadOnlyList<string> Options => Approval.Options ?? [];

    /// <summary>
    /// The answers this row can be given right here, as bindable buttons: an external attention request offers
    /// its options, a gated action offers approve/reject. Empty for a session permission, which is answered in
    /// the session (its transcript carries the context the decision needs) — so a surface renders
    /// <see cref="HasChoices"/> buttons or a jump, and never a request with no way to answer it.
    /// </summary>
    public IReadOnlyList<ApprovalChoice> Choices { get; }

    public bool HasChoices => Choices.Count > 0;

    private IReadOnlyList<ApprovalChoice> BuildChoices(ApprovalsViewModel owner)
    {
        if (IsExternal)
        {
            return Options.Count == 0
                // An external asker that named no options still wants an acknowledgement, so offer the one
                // answer that is always meaningful rather than showing a request nobody can clear.
                ? [new ApprovalChoice("Acknowledge", new AsyncRelayCommand(() => owner.AnswerExternalAsync(this, "ok")), IsPrimary: true)]
                : [.. Options.Select((option, i) => new ApprovalChoice(
                    option, new AsyncRelayCommand(() => owner.AnswerExternalAsync(this, option)), IsPrimary: i == 0))];
        }

        if (IsGatedAction)
        {
            return
            [
                new ApprovalChoice("Approve", new AsyncRelayCommand(() => owner.ResolveGatedAsync(this, approve: true)), IsPrimary: true),
                new ApprovalChoice("Reject", new AsyncRelayCommand(() => owner.ResolveGatedAsync(this, approve: false))),
            ];
        }

        return [];
    }
}

/// <summary>One answer a row offers, as a label plus the command that gives it. <paramref name="IsPrimary"/>
/// marks the affirmative/first answer so a view can weight it — it carries no behaviour of its own.</summary>
public sealed record ApprovalChoice(string Label, ICommand Command, bool IsPrimary = false);
