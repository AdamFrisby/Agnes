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

    // ---- stop / cancel ----

    [Fact]
    public void Stop_cancels_the_turn_and_turn_state_tracks_the_lifecycle()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");

        Assert.False(vm.IsTurnActive);
        Assert.False(vm.CancelCommand.CanExecute(null));

        vm.PromptText = "do a big thing";
        vm.SendCommand.Execute(null);
        Assert.True(vm.IsTurnActive);
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        Assert.Equal(1, host.Cancels);
        Assert.False(vm.IsTurnActive);

        // A naturally-ending turn also clears the active state.
        vm.PromptText = "another";
        vm.SendCommand.Execute(null);
        Assert.True(vm.IsTurnActive);
        view.Apply(Seq(new TurnEndedEvent(StopReason.EndTurn), 1));
        Assert.False(vm.IsTurnActive);
    }

    // ---- conversation rewind ----

    [Fact]
    public void Rewind_shows_history_up_to_a_point_and_resume_restores_live()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");

        view.Apply(Seq(new MessageChunkEvent(MessageRole.User, new TextContent("one")), 1));
        view.Apply(Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("resp1")), 2));
        view.Apply(Seq(new MessageChunkEvent(MessageRole.User, new TextContent("two")), 3));

        Assert.Equal(3, vm.Items.Count);
        Assert.False(vm.IsRewound);
        Assert.Equal(3, vm.DisplayItems.Count());

        vm.RewindToCommand.Execute(vm.Items[0]); // rewind to the first message
        Assert.True(vm.IsRewound);
        Assert.Single(vm.DisplayItems);

        // New live events still append to Items, but the rewound view stays historical.
        view.Apply(Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent("resp2")), 4));
        Assert.Single(vm.DisplayItems);
        Assert.Equal(4, vm.Items.Count);

        vm.ResumeCommand.Execute(null);
        Assert.False(vm.IsRewound);
        Assert.Equal(4, vm.DisplayItems.Count());
    }

    // ---- git ----

    [Fact]
    public void Git_status_surfaces_and_commit_sends_the_message()
    {
        var host = new FakeHost { GitState = new GitStatus(true, "main", true, [new GitFileChange("a.cs", "M")]) };
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode");

        Assert.True(vm.HasGit);            // refreshed in the constructor
        Assert.Equal("main", vm.GitBranch);
        Assert.True(vm.GitDirty);
        Assert.Single(vm.GitChanges);

        Assert.False(vm.CommitCommand.CanExecute(null)); // no message yet
        vm.CommitMessage = "fix the thing";
        Assert.True(vm.CommitCommand.CanExecute(null));

        vm.CommitCommand.Execute(null);
        Assert.Equal("fix the thing", host.Commits.Single());
    }

    // ---- cross-device handoff ----

    [Fact]
    public void Handoff_reference_identifies_the_session_for_reconnect()
    {
        var host = new FakeHost();
        var vm = new SessionViewModel(host, Live("s1"), ImmediateDispatcher.Instance, "OpenCode");
        Assert.Equal("fake://host#s1", vm.HandoffReference);
    }

    // ---- session modes (Ask / Code) ----

    [Fact]
    public void Mode_selector_switches_mode_and_tracks_mode_changed_events()
    {
        var host = new FakeHost();
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(
            new SessionInfo("s1", "opencode", string.Empty, 0,
                [new SessionMode("ask", "Ask"), new SessionMode("code", "Code")], "ask"),
            [], 0));
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");

        Assert.True(vm.HasModes);
        Assert.Equal(2, vm.Modes.Count);
        Assert.Equal("ask", vm.CurrentModeId);
        Assert.Equal("Ask", vm.CurrentModeName);

        vm.SetModeCommand.Execute(vm.Modes.First(m => m.Id == "code"));
        Assert.Equal("code", host.Mode);          // sent to the host
        Assert.Equal("code", vm.CurrentModeId);    // optimistic

        // An inbound mode change (from the agent) updates the current mode.
        view.Apply(Seq(new ModeChangedEvent("ask"), 1));
        Assert.Equal("ask", vm.CurrentModeId);
    }

    // ---- composer: slash commands + attachments ----

    [Fact]
    public void Slash_commands_suggest_and_expand()
    {
        var host = new FakeHost();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode");
        Assert.False(vm.ShowSlash);

        vm.PromptText = "/re";
        Assert.True(vm.ShowSlash);
        var review = Assert.Single(vm.SlashSuggestions, c => c.Name == "review");

        vm.ApplySlashCommand.Execute(review);
        Assert.Equal(review.Expansion, vm.PromptText);
        Assert.False(vm.ShowSlash);
    }

    [Fact]
    public void Attachments_are_added_as_chips_and_sent_with_the_prompt()
    {
        var host = new FakeHost();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode");

        vm.ReferenceInput = "@src/config.ts";
        vm.AddReferenceCommand.Execute(null);
        Assert.True(vm.HasAttachments);
        Assert.Equal("src/config.ts", Assert.Single(vm.Attachments).Label);
        Assert.Equal(string.Empty, vm.ReferenceInput);

        vm.PromptText = "update the config";
        vm.SendCommand.Execute(null);

        Assert.NotNull(host.LastContent);
        Assert.Contains(host.LastContent!, b => b is ResourceLinkContent { Uri: "src/config.ts" });
        Assert.Contains(host.LastContent!, b => b is TextContent { Text: "update the config" });
        Assert.False(vm.HasAttachments); // cleared after send
    }

    // ---- raw event inspector ----

    [Fact]
    public void Raw_event_inspector_captures_the_stream_and_toggles()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");

        view.Apply(Seq(new MessageChunkEvent(MessageRole.User, new TextContent("hi")), 1));
        view.Apply(Seq(new ToolCallEvent("tc1", "a.cs", ToolKind.Edit, ToolCallStatus.Completed, []), 2));

        Assert.Equal(2, vm.RawEvents.Count);
        Assert.Equal("MessageChunk", vm.RawEvents[0].Kind);
        Assert.Equal("ToolCall", vm.RawEvents[1].Kind);

        Assert.False(vm.IsInspectorOpen);
        vm.ToggleInspectorCommand.Execute(null);
        Assert.True(vm.IsInspectorOpen);
    }

    // ---- session activity state ----

    [Fact]
    public void Activity_state_reflects_what_is_in_flight()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        Assert.Equal(SessionActivity.Idle, vm.Activity);

        vm.PromptText = "go";
        vm.SendCommand.Execute(null);
        Assert.Equal(SessionActivity.Running, vm.Activity);

        view.Apply(Seq(new PermissionRequestedEvent("r1", "tc1", "Delete?",
            [new PermissionOption("once", "Allow once", PermissionOptionKind.AllowOnce)]), 1));
        Assert.Equal(SessionActivity.NeedsInput, vm.Activity);
        Assert.True(vm.NeedsAttention);

        view.Apply(Seq(new PermissionResolvedEvent("r1", "once", PermissionOutcome.Allowed), 2));
        Assert.Equal(SessionActivity.Running, vm.Activity); // permission cleared, turn still active

        view.Apply(Seq(new TurnEndedEvent(StopReason.EndTurn), 3));
        Assert.Equal(SessionActivity.Idle, vm.Activity);

        view.Apply(Seq(new ToolCallEvent("tc2", "a.cs", ToolKind.Edit, ToolCallStatus.Completed, [new TextContent("--- a\n+++ b\n+x")]), 4));
        Assert.Equal(SessionActivity.ReadyForReview, vm.Activity);

        view.Apply(Seq(new AgentErrorEvent("boom"), 5));
        Assert.Equal(SessionActivity.Error, vm.Activity);
        Assert.True(vm.NeedsAttention);
    }

    // ---- trust / allowlists ----

    [Fact]
    public void A_trusted_tool_is_auto_approved_without_asking()
    {
        var policy = new StubPolicy { Decision = true }; // always allow
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode", null, policy);

        view.Apply(Seq(new ToolCallEvent("tc1", "build/", ToolKind.Delete, ToolCallStatus.Pending, []), 1));
        view.Apply(Seq(new PermissionRequestedEvent("r1", "tc1", "Delete?",
            [new PermissionOption("once", "Allow once", PermissionOptionKind.AllowOnce)]), 2));

        Assert.Equal(("r1", "once"), host.Responses.Single()); // auto-answered by the policy
    }

    [Fact]
    public void Choosing_an_always_option_records_a_standing_trust_rule()
    {
        var policy = new RecordingPolicy(); // Decide returns null → the card is shown
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode", null, policy);

        view.Apply(Seq(new ToolCallEvent("tc1", "build/", ToolKind.Delete, ToolCallStatus.Pending, []), 1));
        view.Apply(Seq(new PermissionRequestedEvent("r1", "tc1", "Delete?",
        [
            new PermissionOption("once", "Allow once", PermissionOptionKind.AllowOnce),
            new PermissionOption("always", "Always allow", PermissionOptionKind.AllowAlways),
        ]), 2));

        var always = vm.PendingPermission!.Options.First(o => o.Kind == PermissionOptionKind.AllowAlways);
        vm.RespondWithCommand.Execute(always);

        Assert.Equal(("fake://host", ToolKind.Delete, true), policy.Remembered.Single());
        Assert.Equal(("r1", "always"), host.Responses.Single());
    }

    // ---- permission audit trail ----

    [Fact]
    public void Approvals_audit_records_resolved_permissions()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");

        view.Apply(Seq(new PermissionRequestedEvent("r1", "tc1", "Delete the build folder?",
            [new PermissionOption("once", "Allow once", PermissionOptionKind.AllowOnce)]), 1));
        Assert.False(vm.HasApprovals);

        view.Apply(Seq(new PermissionResolvedEvent("r1", "once", PermissionOutcome.Allowed), 2));

        var entry = Assert.Single(vm.Approvals);
        Assert.Equal("Delete the build folder?", entry.Title);
        Assert.True(entry.Allowed);
        Assert.True(vm.HasApprovals);
        Assert.True(vm.HasSidebarContent);
    }

    // ---- prompt queue + steer ----

    [Fact]
    public void Sending_while_a_turn_runs_queues_and_drains_in_order_on_turn_end()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");

        vm.PromptText = "first";
        vm.SendCommand.Execute(null); // idle → sends immediately
        Assert.Equal(["first"], host.Prompts);
        Assert.True(vm.IsTurnActive);

        vm.PromptText = "second";
        vm.SendCommand.Execute(null); // busy → queues
        vm.PromptText = "third";
        vm.SendCommand.Execute(null); // busy → queues
        Assert.Single(host.Prompts);            // nothing new sent yet
        Assert.Equal(2, vm.PendingPrompts.Count);
        Assert.True(vm.HasQueue);

        view.Apply(Seq(new TurnEndedEvent(StopReason.EndTurn), 1)); // drains "second"
        Assert.Equal(["first", "second"], host.Prompts);
        Assert.True(vm.IsTurnActive);
        Assert.Single(vm.PendingPrompts);

        view.Apply(Seq(new TurnEndedEvent(StopReason.EndTurn), 2)); // drains "third"
        Assert.Equal(["first", "second", "third"], host.Prompts);
        Assert.False(vm.HasQueue);
    }

    [Fact]
    public void Send_now_steers_by_cancelling_the_current_turn_then_sending()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");

        vm.PromptText = "long running";
        vm.SendCommand.Execute(null);
        Assert.True(vm.IsTurnActive);

        vm.PromptText = "actually do this instead";
        vm.SendNowCommand.Execute(null);

        Assert.Equal(1, host.Cancels);
        Assert.Equal(["long running", "actually do this instead"], host.Prompts);
        Assert.True(vm.IsTurnActive);
    }

    [Fact]
    public void Queue_can_be_reordered_removed_and_edited()
    {
        var host = new FakeHost();
        var view = Live();
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        vm.PromptText = "start";
        vm.SendCommand.Execute(null); // turn active
        foreach (var t in new[] { "a", "b", "c" })
        {
            vm.PromptText = t;
            vm.SendCommand.Execute(null);
        }
        Assert.Equal(["a", "b", "c"], vm.PendingPrompts.Select(p => p.Text));

        vm.RemoveQueuedCommand.Execute(vm.PendingPrompts[1]); // remove "b"
        Assert.Equal(["a", "c"], vm.PendingPrompts.Select(p => p.Text));

        vm.MoveQueuedDownCommand.Execute(vm.PendingPrompts[0]); // a ↓
        Assert.Equal(["c", "a"], vm.PendingPrompts.Select(p => p.Text));

        vm.EditQueuedCommand.Execute(vm.PendingPrompts[0]); // load "c" into composer
        Assert.Equal("c", vm.PromptText);
        Assert.Equal(["a"], vm.PendingPrompts.Select(p => p.Text));
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
    public UsageInfo? Usage => null;

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

    public IReadOnlyList<ContentBlock>? LastContent { get; private set; }

    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content)
    {
        LastContent = content;
        Prompts.Add(string.Concat(content.OfType<TextContent>().Select(c => c.Text)));
        return Task.CompletedTask;
    }

    public int Cancels { get; private set; }

    public Task CancelAsync(string sessionId)
    {
        Cancels++;
        return Task.CompletedTask;
    }

    public List<(string RequestId, string OptionId)> Responses { get; } = [];

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId)
    {
        Responses.Add((requestId, optionId));
        return Task.CompletedTask;
    }

    public string? Mode { get; private set; }

    public Task SetModeAsync(string sessionId, string modeId)
    {
        Mode = modeId;
        return Task.CompletedTask;
    }

    public GitStatus GitState { get; set; } = new(false, null, false, []);
    public List<string> Commits { get; } = [];

    public Task<GitStatus> GetGitStatusAsync(string sessionId) => Task.FromResult(GitState);

    public Task<GitCommitResult> GitCommitAsync(string sessionId, string message)
    {
        Commits.Add(message);
        return Task.FromResult(new GitCommitResult(true, "ok"));
    }

    public Task<HostInfo> GetHostInfoAsync() => Task.FromResult(new HostInfo("fake", "fake", "1.0"));
    public Task<IReadOnlyList<AgentInfo>> ListAgentsAsync() => Task.FromResult<IReadOnlyList<AgentInfo>>([]);
    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory)
        => Task.FromResult(new SessionInfo("s1", adapterId, workingDirectory, 0));
    public Task<SessionView> SubscribeAsync(string sessionId, long since = 0) => Task.FromResult(new SessionView(sessionId));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Suppress unused-event warnings for the parts of the surface these tests don't exercise.
    private void Touch() { UsageChanged?.Invoke(null); AgentsChanged?.Invoke([]); }
}

/// <summary>Policy that returns a fixed decision for every request.</summary>
internal sealed class StubPolicy : IPermissionPolicy
{
    public bool? Decision { get; set; }
    public bool? Decide(string hostUrl, ToolKind? toolKind) => Decision;
    public void Remember(string hostUrl, ToolKind? toolKind, bool allow) { }
    public void Forget(string hostUrl, ToolKind? toolKind) { }
}

/// <summary>Policy that asks every time but records what it was told to remember.</summary>
internal sealed class RecordingPolicy : IPermissionPolicy
{
    public List<(string HostUrl, ToolKind? ToolKind, bool Allow)> Remembered { get; } = [];
    public bool? Decide(string hostUrl, ToolKind? toolKind) => null;
    public void Remember(string hostUrl, ToolKind? toolKind, bool allow) => Remembered.Add((hostUrl, toolKind, allow));
    public void Forget(string hostUrl, ToolKind? toolKind) { }
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
