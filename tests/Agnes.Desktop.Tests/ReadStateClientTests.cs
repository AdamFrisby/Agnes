using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>Client unread logic (sessions/05): unread = head &gt; read-cursor (or sticky) while not focused;
/// focusing marks read; a synced read cursor from another device clears it.</summary>
public class ReadStateClientTests
{
    private static SessionView Live(string id = "s1")
    {
        var view = new SessionView(id);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(id, "opencode", string.Empty, 0), [], 0));
        return view;
    }

    [Fact]
    public void Unread_tracks_head_vs_cursor_focus_and_sticky()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode", eventBus: new EventBus());

        Assert.False(vm.IsUnread); // no activity yet

        // Activity arrives while the tab isn't focused → unread.
        view.Apply(new NoticeEvent("hi") { Sequence = 1 });
        Assert.True(vm.IsUnread);

        // Another device viewed it → the host syncs a read cursor at head → read on this client too.
        host.RaiseReadState(view.SessionId, 1, false);
        Assert.False(vm.IsUnread);

        // Manually marking unread calls the host and, once synced back as sticky, shows unread again.
        vm.MarkUnread();
        Assert.Contains(view.SessionId, host.Unreads);
        host.RaiseReadState(view.SessionId, 1, true);
        Assert.True(vm.IsUnread);

        // Focusing the tab marks it read (and a focused tab never shows its own unread badge).
        vm.SetActive(true);
        Assert.Contains(host.Reads, r => r.SessionId == view.SessionId);
        Assert.False(vm.IsUnread);
    }
}
