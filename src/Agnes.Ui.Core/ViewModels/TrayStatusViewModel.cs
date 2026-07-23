using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// A session as the tray needs to see it: an identity, a label, and its high-level activity. Deliberately
/// narrow so the aggregate view model stays framework-agnostic and unit-testable — the desktop's
/// <c>SessionDocument</c> implements it, and a test double implements it just as easily. Change
/// notifications (activity/title flips) ride <see cref="INotifyPropertyChanged"/>.
/// </summary>
public interface ITraySession : INotifyPropertyChanged
{
    /// <summary>The live session's id (empty until a session is attached to the tab).</summary>
    string SessionId { get; }

    /// <summary>The display label shown in the jump menu.</summary>
    string? Title { get; }

    /// <summary>The session's high-level activity, from which idle/working/needs-attention is derived.</summary>
    SessionActivity Activity { get; }
}

/// <summary>One session that currently needs the user, as an immutable menu row.</summary>
public sealed record TrayAttentionRow(string SessionId, string Title);

/// <summary>
/// Computes the aggregate tray status — how many sessions are idle / working / need attention, a one-line
/// tooltip, and the list of sessions currently needing attention — over the same live session collection the
/// main window drives. Purely a read-only projection: it observes the collection and each session's activity
/// and recomputes, never mutating a session. Framework-agnostic so the Avalonia tray glue is a thin shell over
/// it and the whole thing is unit-testable headless.
/// </summary>
public sealed class TrayStatusViewModel : ObservableObject, IDisposable
{
    private readonly IEnumerable<ITraySession> _sessions;
    private readonly List<ITraySession> _watched = [];
    private int _idleCount;
    private int _workingCount;
    private int _needsAttentionCount;
    private string _tooltip = string.Empty;
    private bool _disposed;

    public TrayStatusViewModel(IEnumerable<ITraySession> sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        SelectCommand = new RelayCommand<TrayAttentionRow>(Select);

        if (_sessions is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += OnCollectionChanged;
        }

        Resubscribe();
        Recompute();
    }

    /// <summary>Sessions with nothing in flight and nothing to review.</summary>
    public int IdleCount
    {
        get => _idleCount;
        private set => SetProperty(ref _idleCount, value);
    }

    /// <summary>Sessions with an active turn.</summary>
    public int WorkingCount
    {
        get => _workingCount;
        private set => SetProperty(ref _workingCount, value);
    }

    /// <summary>Sessions blocked on the user (open permission / awaiting input / errored).</summary>
    public int NeedsAttentionCount
    {
        get => _needsAttentionCount;
        private set
        {
            if (SetProperty(ref _needsAttentionCount, value))
            {
                OnPropertyChanged(nameof(HasAttention));
            }
        }
    }

    /// <summary>Whether anything needs the user right now (drives the tray icon's attention state).</summary>
    public bool HasAttention => _needsAttentionCount > 0;

    /// <summary>A one-line summary for the tray tooltip, e.g. "Agnes — 1 needs attention, 2 working".</summary>
    public string Tooltip
    {
        get => _tooltip;
        private set => SetProperty(ref _tooltip, value);
    }

    /// <summary>The sessions currently needing attention (in collection order), as jump-menu rows.</summary>
    public ObservableCollection<TrayAttentionRow> NeedsAttention { get; } = [];

    /// <summary>Raised with a session id when the user picks a session from the tray menu, so the shell can
    /// bring the app forward and activate that session's tab.</summary>
    public event Action<string>? ActivateRequested;

    /// <summary>Selects a needs-attention row — asks the shell to activate that session.</summary>
    public ICommand SelectCommand { get; }

    private void Select(TrayAttentionRow? row)
    {
        if (row is { SessionId.Length: > 0 })
        {
            ActivateRequested?.Invoke(row.SessionId);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Resubscribe();
        Recompute();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Activity flips drive every count; the title only affects the menu label. Recompute on either.
        if (e.PropertyName is nameof(ITraySession.Activity) or nameof(ITraySession.Title) or null)
        {
            Recompute();
        }
    }

    // Keep our per-session subscriptions in sync with the (possibly changed) collection.
    private void Resubscribe()
    {
        foreach (var session in _watched)
        {
            session.PropertyChanged -= OnSessionChanged;
        }

        _watched.Clear();
        foreach (var session in _sessions)
        {
            session.PropertyChanged += OnSessionChanged;
            _watched.Add(session);
        }
    }

    private void Recompute()
    {
        int idle = 0, working = 0, attention = 0;
        var rows = new List<TrayAttentionRow>();
        foreach (var session in _sessions)
        {
            switch (session.Activity)
            {
                case SessionActivity.Running:
                    working++;
                    break;
                case SessionActivity.NeedsInput:
                case SessionActivity.Error:
                    attention++;
                    rows.Add(new TrayAttentionRow(session.SessionId, DisplayTitle(session)));
                    break;
                default:
                    idle++;
                    break;
            }
        }

        IdleCount = idle;
        WorkingCount = working;
        NeedsAttentionCount = attention;

        NeedsAttention.Clear();
        foreach (var row in rows)
        {
            NeedsAttention.Add(row);
        }

        Tooltip = BuildTooltip(idle, working, attention);
    }

    private static string DisplayTitle(ITraySession session)
        => string.IsNullOrWhiteSpace(session.Title) ? "Untitled session" : session.Title!;

    private static string BuildTooltip(int idle, int working, int attention)
    {
        if (idle + working + attention == 0)
        {
            return "Agnes — no active sessions";
        }

        var parts = new List<string>(3);
        if (attention > 0)
        {
            parts.Add($"{attention} {(attention == 1 ? "needs" : "need")} attention");
        }

        if (working > 0)
        {
            parts.Add($"{working} working");
        }

        if (idle > 0)
        {
            parts.Add($"{idle} idle");
        }

        return "Agnes — " + string.Join(", ", parts);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_sessions is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged -= OnCollectionChanged;
        }

        foreach (var session in _watched)
        {
            session.PropertyChanged -= OnSessionChanged;
        }

        _watched.Clear();
    }
}
