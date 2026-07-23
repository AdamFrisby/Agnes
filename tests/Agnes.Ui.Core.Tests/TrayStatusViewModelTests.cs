using System.Collections.ObjectModel;
using System.ComponentModel;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public sealed class TrayStatusViewModelTests
{
    // A minimal ITraySession test double: an id, a title, and a mutable activity that raises change
    // notifications, mirroring what the desktop's SessionDocument exposes.
    private sealed class FakeSession : ITraySession
    {
        private SessionActivity _activity;
        private string? _title;

        public FakeSession(string id, string? title, SessionActivity activity)
        {
            SessionId = id;
            _title = title;
            _activity = activity;
        }

        public string SessionId { get; }

        public string? Title
        {
            get => _title;
            set { _title = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))); }
        }

        public SessionActivity Activity
        {
            get => _activity;
            set { _activity = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Activity))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [Fact]
    public void Aggregate_counts_split_sessions_by_activity()
    {
        var sessions = new ObservableCollection<FakeSession>
        {
            new("s1", "Idle one", SessionActivity.Idle),
            new("s2", "Review", SessionActivity.ReadyForReview),   // counts as idle (not working / attention)
            new("s3", "Working", SessionActivity.Running),
            new("s4", "Permission", SessionActivity.NeedsInput),
            new("s5", "Broke", SessionActivity.Error),
        };

        using var vm = new TrayStatusViewModel(sessions);

        Assert.Equal(2, vm.IdleCount);
        Assert.Equal(1, vm.WorkingCount);
        Assert.Equal(2, vm.NeedsAttentionCount);
        Assert.True(vm.HasAttention);
    }

    [Fact]
    public void Needs_attention_list_holds_exactly_the_blocked_sessions_with_titles()
    {
        var sessions = new ObservableCollection<FakeSession>
        {
            new("s1", "Idle one", SessionActivity.Idle),
            new("s2", "Awaiting", SessionActivity.NeedsInput),
            new("s3", "Running", SessionActivity.Running),
            new("s4", "Errored", SessionActivity.Error),
        };

        using var vm = new TrayStatusViewModel(sessions);

        Assert.Equal(2, vm.NeedsAttention.Count);
        Assert.Contains(vm.NeedsAttention, r => r.SessionId == "s2" && r.Title == "Awaiting");
        Assert.Contains(vm.NeedsAttention, r => r.SessionId == "s4" && r.Title == "Errored");
        Assert.DoesNotContain(vm.NeedsAttention, r => r.SessionId is "s1" or "s3");
    }

    [Fact]
    public void Selecting_a_row_raises_activate_with_that_session_id()
    {
        var sessions = new ObservableCollection<FakeSession>
        {
            new("s1", "Awaiting", SessionActivity.NeedsInput),
        };

        using var vm = new TrayStatusViewModel(sessions);
        string? activated = null;
        vm.ActivateRequested += id => activated = id;

        var row = Assert.Single(vm.NeedsAttention);
        vm.SelectCommand.Execute(row);

        Assert.Equal("s1", activated);
    }

    [Fact]
    public void Flipping_a_session_into_needs_attention_updates_the_aggregate()
    {
        var session = new FakeSession("s1", "Session", SessionActivity.Idle);
        var sessions = new ObservableCollection<FakeSession> { session };

        using var vm = new TrayStatusViewModel(sessions);
        Assert.Equal(0, vm.NeedsAttentionCount);
        Assert.False(vm.HasAttention);
        Assert.Equal(1, vm.IdleCount);

        // A permission request appears — no main window involved, just the session's own state changing.
        session.Activity = SessionActivity.NeedsInput;

        Assert.Equal(1, vm.NeedsAttentionCount);
        Assert.True(vm.HasAttention);
        Assert.Equal(0, vm.IdleCount);
        Assert.Equal("s1", Assert.Single(vm.NeedsAttention).SessionId);
    }

    [Fact]
    public void Adding_and_removing_sessions_re_subscribes_and_recomputes()
    {
        var sessions = new ObservableCollection<FakeSession>();
        using var vm = new TrayStatusViewModel(sessions);
        Assert.Equal(0, vm.NeedsAttentionCount);

        var added = new FakeSession("s1", "Late arrival", SessionActivity.Idle);
        sessions.Add(added);
        Assert.Equal(1, vm.IdleCount);

        // The newly-added session flips — it must already be observed.
        added.Activity = SessionActivity.NeedsInput;
        Assert.Equal(1, vm.NeedsAttentionCount);

        sessions.Remove(added);
        Assert.Equal(0, vm.NeedsAttentionCount);
        Assert.Empty(vm.NeedsAttention);
    }

    [Fact]
    public void Empty_session_set_has_zero_counts_and_a_sensible_tooltip()
    {
        var sessions = new ObservableCollection<FakeSession>();

        using var vm = new TrayStatusViewModel(sessions);

        Assert.Equal(0, vm.IdleCount);
        Assert.Equal(0, vm.WorkingCount);
        Assert.Equal(0, vm.NeedsAttentionCount);
        Assert.False(vm.HasAttention);
        Assert.Empty(vm.NeedsAttention);
        Assert.Equal("Agnes — no active sessions", vm.Tooltip);
    }

    [Fact]
    public void Tooltip_summarizes_the_aggregate()
    {
        var sessions = new ObservableCollection<FakeSession>
        {
            new("s1", "a", SessionActivity.NeedsInput),
            new("s2", "b", SessionActivity.Running),
            new("s3", "c", SessionActivity.Running),
            new("s4", "d", SessionActivity.Idle),
        };

        using var vm = new TrayStatusViewModel(sessions);

        Assert.Equal("Agnes — 1 needs attention, 2 working, 1 idle", vm.Tooltip);
    }

    [Fact]
    public void A_session_without_a_title_falls_back_to_a_placeholder()
    {
        var sessions = new ObservableCollection<FakeSession>
        {
            new("s1", null, SessionActivity.NeedsInput),
        };

        using var vm = new TrayStatusViewModel(sessions);

        Assert.Equal("Untitled session", Assert.Single(vm.NeedsAttention).Title);
    }
}
