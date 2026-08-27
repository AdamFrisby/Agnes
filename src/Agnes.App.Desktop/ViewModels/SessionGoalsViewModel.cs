using System.Collections.ObjectModel;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// The standing "keep going" goals belonging to <b>one</b> session, and the form that arms a new one.
/// </summary>
/// <remarks>
/// <para>A goal is per-session by construction — <see cref="ArmGoalRequest"/> takes a session id, and the
/// host nudges that session and no other. It was nevertheless driven from a control in the window's own
/// top bar, which had to guess its target from whichever tab happened to be focused; the form said either
/// "on &lt;title&gt;" or "open a session first", which is the tell that a window-level control was standing
/// in for a per-tab one. Owning the state here removes the guess: the tab is the target, so there is no
/// focus to lose and no way to arm a goal on a session you weren't looking at.</para>
///
/// <para>The list is filtered to this session for the same reason. The window-level version listed every
/// goal on every connected host, so a session's own goals were mixed in with goals belonging to sessions
/// the user could not see from there.</para>
/// </remarks>
public sealed partial class SessionGoalsViewModel : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;

    private IAgnesHost? _host;
    private string? _sessionId;
    private Action<SessionGoal>? _subscription;

    public SessionGoalsViewModel(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        ArmGoalCommand = new AsyncRelayCommand(ArmAsync, () => CanArmGoal);
        DisarmGoalCommand = new AsyncRelayCommand<GoalRow>(DisarmAsync);
        RemoveGoalCommand = new AsyncRelayCommand<GoalRow>(RemoveAsync);
    }

    /// <summary>This session's goals, newest state first refreshed from the host.</summary>
    public ObservableCollection<GoalRow> Goals { get; } = [];

    public int ArmedGoalCount => Goals.Count(g => g.Armed);

    /// <summary>Whether the tab has a live session to arm a goal on. Unlike the window-level control this
    /// replaces, there is a real answer: no session, no goals affordance.</summary>
    public bool IsAvailable => _sessionId is not null;

    /// <summary>
    /// Points this at a tab's session, or at nothing when the tab has no live session. Re-entrant: a tab
    /// that is re-used for another session detaches from the previous host's broadcast first, so a stale
    /// subscription can't keep refreshing a list that has moved on.
    /// </summary>
    public void Attach(SessionViewModel? session)
    {
        if (_host is { } previous && _subscription is { } handler)
        {
            previous.GoalChanged -= handler;
        }

        _subscription = null;
        _host = session?.Host;
        _sessionId = session?.SessionId;

        Goals.Clear();
        OnPropertyChanged(nameof(ArmedGoalCount));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanArmGoal));
        ArmGoalCommand.NotifyCanExecuteChanged();

        if (_host is not { } host)
        {
            return;
        }

        // Goals move from the host side too — a nudge spends budget, and an agent can disarm its own goal —
        // so the list follows the broadcast rather than only refreshing when the flyout is opened.
        _subscription = changed => { _ = RefreshAsync(); };
        host.GoalChanged += _subscription;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_host is not { } host || _sessionId is not { } sessionId)
        {
            return;
        }

        List<GoalRow> rows;
        try
        {
            rows = [.. (await host.ListGoalsAsync())
                .Where(g => string.Equals(g.SessionId, sessionId, StringComparison.Ordinal))
                .Select(g => new GoalRow(g, host))];
        }
        catch
        {
            // Best-effort, as elsewhere: a host that can't list contributes nothing rather than throwing
            // into a UI callback.
            return;
        }

        _dispatcher.Post(() =>
        {
            Goals.Clear();
            foreach (var row in rows)
            {
                Goals.Add(row);
            }

            OnPropertyChanged(nameof(ArmedGoalCount));
        });
    }

    // ---- the new-goal form ----

    private string _newGoalText = string.Empty;
    private int _newGoalIdleMinutes = 10;
    private int _newGoalMaxProds = 5;
    private bool _newGoalUnlimited;

    public string NewGoalText
    {
        get => _newGoalText;
        set
        {
            if (_newGoalText == value)
            {
                return;
            }

            _newGoalText = value;
            OnPropertyChanged(nameof(NewGoalText));
            OnPropertyChanged(nameof(CanArmGoal));
            ArmGoalCommand.NotifyCanExecuteChanged();
        }
    }

    public int NewGoalIdleMinutes
    {
        get => _newGoalIdleMinutes;
        set { _newGoalIdleMinutes = Math.Clamp(value, 1, 24 * 60); OnPropertyChanged(nameof(NewGoalIdleMinutes)); }
    }

    public int NewGoalMaxProds
    {
        get => _newGoalMaxProds;
        set { _newGoalMaxProds = Math.Clamp(value, 1, 50); OnPropertyChanged(nameof(NewGoalMaxProds)); }
    }

    public bool NewGoalUnlimited
    {
        get => _newGoalUnlimited;
        set
        {
            if (_newGoalUnlimited == value)
            {
                return;
            }

            _newGoalUnlimited = value;
            OnPropertyChanged(nameof(NewGoalUnlimited));
            OnPropertyChanged(nameof(NewGoalHasLimit));
        }
    }

    /// <summary>Inverse of <see cref="NewGoalUnlimited"/> — the numeric field is meaningless without a limit.</summary>
    public bool NewGoalHasLimit => !_newGoalUnlimited;

    public bool CanArmGoal => !string.IsNullOrWhiteSpace(NewGoalText) && _sessionId is not null;

    public IAsyncRelayCommand ArmGoalCommand { get; }

    public IAsyncRelayCommand<GoalRow> DisarmGoalCommand { get; }

    public IAsyncRelayCommand<GoalRow> RemoveGoalCommand { get; }

    private async Task ArmAsync()
    {
        if (_host is not { } host || _sessionId is not { } sessionId || string.IsNullOrWhiteSpace(NewGoalText))
        {
            return;
        }

        try
        {
            await host.ArmGoalAsync(new ArmGoalRequest(
                sessionId, NewGoalText.Trim(), NewGoalIdleMinutes * 60,
                NewGoalUnlimited ? 0 : NewGoalMaxProds)); // 0 = keep going until disarmed
            NewGoalText = string.Empty;
        }
        catch
        {
            // the refresh below reflects whatever actually happened
        }

        await RefreshAsync();
    }

    private async Task DisarmAsync(GoalRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            await row.Host.DisarmGoalAsync(row.Id, "disarmed from the desktop");
        }
        catch
        {
            // as above: the refresh is the source of truth
        }

        await RefreshAsync();
    }

    private async Task RemoveAsync(GoalRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            await row.Host.RemoveGoalAsync(row.Id);
        }
        catch
        {
            // as above
        }

        await RefreshAsync();
    }
}

/// <summary>One armed-or-finished goal, with the display text the flyout shows.</summary>
public sealed class GoalRow(SessionGoal goal, IAgnesHost host)
{
    public SessionGoal Goal { get; } = goal;

    public IAgnesHost Host { get; } = host;

    public string Id => Goal.Id;

    public string Text => Goal.Goal;

    public bool Armed => Goal.Armed;

    /// <summary>Why it stopped, or how it is currently set up — the one line under the goal text.</summary>
    public string Detail => Goal.Armed
        ? $"nudges if idle {Describe(Goal.IdleSeconds)} · {Budget}"
        : $"stopped: {Goal.DisarmedReason ?? "disarmed"} · {Budget}";

    /// <summary>"used 2 of 5", or "2 nudges (no limit)" when the budget is unlimited.</summary>
    private string Budget => Goal.MaxProds > 0
        ? $"used {Goal.ProdsUsed} of {Goal.MaxProds}"
        : $"{Goal.ProdsUsed} nudge(s), no limit";

    private static string Describe(int seconds) => seconds >= 3600
        ? $"{seconds / 3600.0:0.#}h"
        : seconds >= 60 ? $"{seconds / 60}m" : $"{seconds}s";
}
