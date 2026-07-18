using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

public class SessionStateTests
{
    private static SessionView Live(string id = "s1")
    {
        var view = new SessionView(id);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(id, "opencode", string.Empty, 0), [], 0));
        return view;
    }

    private static SessionEvent Seq(SessionEvent e, long n) => e with { Sequence = n };

    // ---- connection / session state banners ----

    [Fact]
    public void Host_state_drives_the_banner()
    {
        var host = new FakeHost();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode");
        Assert.Equal(SessionBanner.None, vm.Banner);
        Assert.False(vm.ShowBanner);

        host.SetState(AgnesConnectionState.Reconnecting);
        Assert.Equal(SessionBanner.Reconnecting, vm.Banner);
        Assert.True(vm.ShowBanner);
        Assert.False(vm.CanRetry);

        host.SetState(AgnesConnectionState.Disconnected);
        Assert.Equal(SessionBanner.Offline, vm.Banner);
        Assert.True(vm.CanRetry);

        host.SetState(AgnesConnectionState.Connected);
        Assert.Equal(SessionBanner.None, vm.Banner);
    }

    [Fact]
    public void Agent_error_shows_interrupted_and_retry_resends_last_prompt()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");

        vm.PromptText = "do the thing";
        vm.SendCommand.Execute(null);
        Assert.Equal("do the thing", host.Prompts.Single());

        view.Apply(Seq(new AgentErrorEvent("boom"), 1));
        Assert.Equal(SessionBanner.Interrupted, vm.Banner);
        Assert.True(vm.CanRetry);

        vm.RetryCommand.Execute(null);
        Assert.Equal(2, host.Prompts.Count);
        Assert.Equal("do the thing", host.Prompts[1]);
        Assert.Equal(SessionBanner.None, vm.Banner);
    }

    [Fact]
    public void Stale_wins_over_connection_state_until_dismissed()
    {
        var host = new FakeHost();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode");

        vm.MarkStale();
        Assert.Equal(SessionBanner.Stale, vm.Banner);

        host.SetState(AgnesConnectionState.Reconnecting);
        Assert.Equal(SessionBanner.Stale, vm.Banner);

        vm.DismissBannerCommand.Execute(null);
        // Falls back to the live host state.
        Assert.Equal(SessionBanner.Reconnecting, vm.Banner);
    }

    // ---- prompt history + draft persistence ----

    [Fact]
    public void Sent_prompts_persist_and_recall_walks_history()
    {
        var store = new InMemoryPromptStore();
        var host = new FakeHost();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode", store);

        vm.PromptText = "first";
        vm.SendCommand.Execute(null);
        vm.PromptText = "second";
        vm.SendCommand.Execute(null);
        Assert.Equal("", vm.PromptText);

        vm.RecallPreviousCommand.Execute(null);
        Assert.Equal("second", vm.PromptText);
        vm.RecallPreviousCommand.Execute(null);
        Assert.Equal("first", vm.PromptText);
        vm.RecallPreviousCommand.Execute(null); // clamp at oldest
        Assert.Equal("first", vm.PromptText);

        vm.RecallNextCommand.Execute(null);
        Assert.Equal("second", vm.PromptText);
        vm.RecallNextCommand.Execute(null); // past newest -> empty
        Assert.Equal("", vm.PromptText);

        Assert.Equal(["first", "second"], store.LoadHistory("s1"));
    }

    [Fact]
    public void Draft_is_saved_on_edit_and_restored_on_reopen()
    {
        var store = new InMemoryPromptStore();
        var host = new FakeHost();

        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode", store);
        vm.PromptText = "half-written idea";
        Assert.Equal("half-written idea", store.LoadDraft("s1"));

        // Reopening the same session restores the draft.
        var reopened = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode", store);
        Assert.Equal("half-written idea", reopened.PromptText);
    }

    // ---- collapse all tool output ----

    [Fact]
    public void Tool_output_starts_expanded_and_toggles()
    {
        var host = new FakeHost();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode");
        Assert.True(vm.ToolsExpanded);

        vm.ToggleToolsCommand.Execute(null);
        Assert.False(vm.ToolsExpanded);
    }

    // ---- full-screen review ----

    [Fact]
    public void Full_screen_preview_hides_chat_and_left_panel()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        view.Apply(Seq(new ToolCallEvent("tc1", "f", ToolKind.Edit, ToolCallStatus.Completed, [new TextContent("--- a\n+++ b\n+x")]), 1));

        vm.ShowFilePreviewCommand.Execute(vm.ModifiedFiles[0]);
        Assert.True(vm.ShowChat);
        Assert.True(vm.ShowLeftPanel);

        vm.ToggleFullScreenCommand.Execute(null);
        Assert.False(vm.ShowChat);
        Assert.False(vm.ShowLeftPanel);
        Assert.True(vm.ShowRightPanel);
    }

    // ---- notifications ----

    [Fact]
    public void Raises_notifications_for_blockers_and_completions_but_not_cancellation()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        var got = new List<AppNotification>();
        vm.NotificationRaised += got.Add;

        view.Apply(Seq(new PermissionRequestedEvent("r1", "tc1", "Run rm -rf",
            [new PermissionOption("a", "Allow", PermissionOptionKind.AllowOnce)]), 1));
        view.Apply(Seq(new TurnEndedEvent(StopReason.EndTurn), 2));
        view.Apply(Seq(new TurnEndedEvent(StopReason.Cancelled), 3));
        view.Apply(Seq(new AgentErrorEvent("stream died"), 4));

        Assert.Equal(3, got.Count);
        Assert.Equal(NotificationKind.Blocker, got[0].Kind);
        Assert.Equal(NotificationKind.Completion, got[1].Kind);
        Assert.Equal(NotificationKind.Error, got[2].Kind);
        Assert.All(got, n => Assert.Equal("s1", n.SessionId));
    }

    // ---- search within a session + deep-linking ----

    [Fact]
    public void Search_finds_matches_and_deep_links_the_first_hit()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        string? scrolled = null;
        vm.ScrollToRequested += a => scrolled = a;

        view.Apply(Seq(new MessageChunkEvent(MessageRole.User, new TextContent("please refactor the config loader")), 1));
        view.Apply(Seq(new ToolCallEvent("tc1", "Read config.ts", ToolKind.Read, ToolCallStatus.Completed, [new TextContent("config contents")]), 2));

        vm.SearchQuery = "config";
        Assert.Equal(2, vm.Matches.Count);
        Assert.Equal("1 / 2", vm.MatchSummary);
        Assert.Equal(vm.Matches[0].AnchorId, scrolled);

        vm.NextMatchCommand.Execute(null);
        Assert.Equal("2 / 2", vm.MatchSummary);
        Assert.Equal(vm.Matches[1].AnchorId, scrolled);

        vm.NextMatchCommand.Execute(null); // wraps
        Assert.Equal("1 / 2", vm.MatchSummary);

        vm.SearchQuery = "nothing-here";
        Assert.Empty(vm.Matches);
        Assert.Equal("No matches", vm.MatchSummary);
    }

    [Fact]
    public void Keyboard_navigation_jumps_between_prompts_and_changes()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        string? scrolled = null;
        vm.ScrollToRequested += a => scrolled = a;

        view.Apply(Seq(new MessageChunkEvent(MessageRole.User, new TextContent("first")), 1));
        view.Apply(Seq(new ToolCallEvent("tc1", "Edit a.cs", ToolKind.Edit, ToolCallStatus.Completed, [new TextContent("--- a\n+++ b\n+x")]), 2));
        view.Apply(Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("done")), 3));

        var prompt = vm.Items.OfType<MessageBubbleItem>().First(m => m.IsUser);
        var change = vm.Items.OfType<ToolCallItem>().First();

        vm.NextPromptCommand.Execute(null);
        Assert.Equal(prompt.AnchorId, scrolled);

        vm.NextChangeCommand.Execute(null);
        Assert.Equal(change.AnchorId, scrolled);
    }

    // ---- deep-link anchors ----

    [Fact]
    public void Transcript_items_have_stable_anchor_ids()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        view.Apply(Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("hi")), 1));

        var item = vm.Items.OfType<MessageBubbleItem>().Single();
        Assert.False(string.IsNullOrEmpty(item.AnchorId));
        Assert.Equal(item.AnchorId, item.AnchorId);
    }
}

/// <summary>A host whose state and prompt log the test controls directly.</summary>
internal sealed class FakeHost : IAgnesHost
{
    public string HostUrl => "fake://host";
    public AgnesConnectionState State { get; private set; } = AgnesConnectionState.Connected;
    public string? UsageSummary => null;

    public event Action<AgnesConnectionState>? StateChanged;
    public event Action<string?>? UsageChanged;
    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;

    public List<string> Prompts { get; } = [];

    public void SetState(AgnesConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(AgnesConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content)
    {
        Prompts.Add(string.Concat(content.OfType<TextContent>().Select(c => c.Text)));
        return Task.CompletedTask;
    }

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId) => Task.CompletedTask;

    public Task<HostInfo> GetHostInfoAsync() => Task.FromResult(new HostInfo("fake", "fake", "1.0"));
    public Task<IReadOnlyList<AgentInfo>> ListAgentsAsync() => Task.FromResult<IReadOnlyList<AgentInfo>>([]);
    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory)
        => Task.FromResult(new SessionInfo("s1", adapterId, workingDirectory, 0));
    public Task<SessionView> SubscribeAsync(string sessionId, long since = 0) => Task.FromResult(new SessionView(sessionId));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Suppress unused-event warnings for the parts of the surface these tests don't exercise.
    private void Touch() { UsageChanged?.Invoke(null); AgentsChanged?.Invoke([]); }
}

/// <summary>In-memory prompt store for tests.</summary>
internal sealed class InMemoryPromptStore : IPromptStore
{
    private readonly Dictionary<string, string> _drafts = new();
    private readonly Dictionary<string, List<string>> _history = new();

    public string LoadDraft(string sessionId) => _drafts.TryGetValue(sessionId, out var d) ? d : string.Empty;
    public void SaveDraft(string sessionId, string draft) => _drafts[sessionId] = draft;

    public IReadOnlyList<string> LoadHistory(string sessionId)
        => _history.TryGetValue(sessionId, out var h) ? h.ToArray() : [];

    public void AppendHistory(string sessionId, string prompt)
    {
        if (!_history.TryGetValue(sessionId, out var h))
        {
            _history[sessionId] = h = [];
        }

        h.Add(prompt);
    }
}
