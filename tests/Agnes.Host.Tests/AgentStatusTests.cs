using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Mcp;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace Agnes.Host.Tests;

/// <summary>
/// The agent's one-line status: what a model writes, what Agnes keeps of it, and what it is told back.
/// <para>
/// Three things here are load-bearing and each has been a real bug in something like this. Truncation must
/// be <b>audible</b> — a model told "noted" after losing half its sentence writes the same paragraph next
/// time. The coalescing window must <b>replace, not drop</b> — the latest line is the only one anybody
/// reads, so a rate limiter that discards the newest report is worse than no limiter. And the write must
/// take the <b>same path every other session fact takes</b>: persisted, broadcast, on the spine — a status
/// held in a mutable field would vanish on reconnect and never reach a client that joins later.
/// </para>
/// </summary>
public sealed class AgentStatusTests : IDisposable
{
    // ---- harness ---------------------------------------------------------------------------------

    /// <summary>Stands in for a subscribed client: what the host broadcast, in order.</summary>
    private sealed class CollectingBroadcaster : ISessionBroadcaster
    {
        private readonly List<(string SessionId, SessionEvent Event)> _published = [];

        public IReadOnlyList<(string SessionId, SessionEvent Event)> Published
        {
            get { lock (_published) { return _published.ToArray(); } }
        }

        public Task PublishAsync(string sessionId, SessionEvent @event)
        {
            lock (_published)
            {
                _published.Add((sessionId, @event));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class VetoStatus(string reason) : IEventInterceptor<BeforeStatusReportedEvent>
    {
        public ValueTask InterceptAsync(BeforeStatusReportedEvent evt, CancellationToken ct = default)
        {
            evt.Cancel(reason);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RedactStatus : IEventInterceptor<BeforeStatusReportedEvent>
    {
        public ValueTask InterceptAsync(BeforeStatusReportedEvent evt, CancellationToken ct = default)
        {
            evt.Status = evt.Status.Replace("hunter2", "[redacted]", StringComparison.Ordinal);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A plugin watching the spine for the observe-only fact.</summary>
    private sealed class WatchStatuses : IEventObserver<AgentStatusEvent>
    {
        private readonly List<string> _seen = [];

        public IReadOnlyList<string> Seen
        {
            get { lock (_seen) { return _seen.ToArray(); } }
        }

        public ValueTask ObserveAsync(AgentStatusEvent evt, CancellationToken ct = default)
        {
            lock (_seen)
            {
                _seen.Add(evt.Status);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A clock the test moves by hand, timers included — the coalescing window schedules a real
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>, so a clock that only answers
    /// <c>GetUtcNow</c> would leave the deferred write waiting on wall-clock seconds.
    /// </summary>
    private sealed class StatusClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            // A timer registered after the clock already passed its due moment must still fire: the code under
            // test schedules from a background task, so the test's Advance can easily win that race. Off-thread
            // so CreateTimer isn't re-entered by its own callback.
            if (timer.IsDue(GetUtcNow()))
            {
                _ = Task.Run(timer.Fire);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            FakeTimer[] due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.IsDue(_now)).ToArray();
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private void Forget(FakeTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class FakeTimer(StatusClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _dueAt;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                return true;
            }

            public bool IsDue(DateTimeOffset now) => _dueAt is { } due && due <= now;

            public void Fire()
            {
                _dueAt = null;
                callback(state);
            }

            public void Dispose() => clock.Forget(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("agnes-status").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp dir that outlives the run is noise, not a failure.
        }
    }

    private sealed record Harness(
        SessionManager Manager, string SessionId, CollectingBroadcaster Broadcaster, InMemoryEventStore Store);

    private async Task<Harness> OpenAsync(
        StatusOptions? options = null, TimeProvider? clock = null, params object[] plugins)
    {
        var bus = new EventBus();
        foreach (var plugin in plugins)
        {
            switch (plugin)
            {
                case IEventInterceptor<BeforeStatusReportedEvent> i:
                    bus.Intercept(i);
                    break;
                case IEventObserver<AgentStatusEvent> o:
                    bus.Observe(o);
                    break;
                default:
                    throw new ArgumentException("Unsupported plugin type.", nameof(plugins));
            }
        }

        var broadcaster = new CollectingBroadcaster();
        var store = new InMemoryEventStore();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), store, broadcaster,
            NullLoggerFactory.Instance, eventBus: bus, status: options, timeProvider: clock);
        var info = await manager.OpenSessionAsync("scripted", _dir, useSandbox: false);
        return new Harness(manager, info.SessionId, broadcaster, store);
    }

    private static async Task<IReadOnlyList<AgentStatusEvent>> StatusesAsync(Harness h)
        => (await h.Store.ReadSinceAsync(h.SessionId, 0)).OfType<AgentStatusEvent>().ToArray();

    /// <summary>
    /// Polls until the log holds <paramref name="count"/> status events, or gives up. The deferred flush runs
    /// off a background task, so "it happened" is a wait rather than an assertion about ordering — and the
    /// clock keeps moving while we wait, because the flush task reads the clock and arms its timer in two
    /// steps that a hand-advanced clock can land between.
    /// </summary>
    private static async Task<IReadOnlyList<AgentStatusEvent>> WaitForStatusesAsync(
        Harness h, int count, StatusClock? clock = null)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var statuses = await StatusesAsync(h);
            if (statuses.Count >= count)
            {
                return statuses;
            }

            clock?.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(10);
        }

        return await StatusesAsync(h);
    }

    // ---- normalisation ---------------------------------------------------------------------------

    [Fact]
    public void A_report_is_trimmed_and_its_inner_whitespace_collapsed()
    {
        var result = AgentStatusText.Normalize("   Found   the  leak\tin the pool.  ", 240);

        Assert.NotNull(result);
        Assert.Equal("Found the leak in the pool.", result.Status);
        Assert.True(result.Verbatim);
    }

    [Fact]
    public void Only_the_first_line_survives_and_the_agent_is_told()
    {
        var result = AgentStatusText.Normalize("Fixing the parser.\n- step one\n- step two", 240);

        Assert.NotNull(result);
        Assert.Equal("Fixing the parser.", result.Status);
        Assert.True(result.TrimmedToFirstLine);
        Assert.False(result.Clipped);
        Assert.Equal("Kept the first line; reports are a single line.", AgentStatusText.Acknowledge(result));
    }

    [Fact]
    public void A_trailing_newline_is_not_a_second_line()
    {
        var result = AgentStatusText.Normalize("Running the tests.\n", 240);

        Assert.NotNull(result);
        Assert.False(result.TrimmedToFirstLine);
        Assert.Equal("Noted.", AgentStatusText.Acknowledge(result));
    }

    [Fact]
    public void A_report_that_starts_with_a_blank_line_still_finds_its_text()
    {
        // A model that opens with a newline meant the sentence after it, not "nothing".
        var result = AgentStatusText.Normalize("\n\nStill unpicking the migration.", 240);

        Assert.NotNull(result);
        Assert.Equal("Still unpicking the migration.", result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t  ")]
    [InlineData("\n\n")]
    public void An_empty_report_is_not_a_status(string? raw)
        => Assert.Null(AgentStatusText.Normalize(raw, 240));

    [Fact]
    public void An_over_long_report_is_cut_at_a_word_boundary_within_the_limit()
    {
        var raw = string.Join(' ', Enumerable.Repeat("alpha", 100)); // 599 chars
        var result = AgentStatusText.Normalize(raw, 40);

        Assert.NotNull(result);
        Assert.True(result.Clipped);
        Assert.True(result.Status.Length <= 40, $"'{result.Status}' is {result.Status.Length} chars");
        Assert.EndsWith("alpha…", result.Status, StringComparison.Ordinal);
        Assert.DoesNotContain(" …", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_unbroken_token_longer_than_the_limit_is_cut_mid_word()
    {
        // There is no word boundary to fall back to, and answering with a lone ellipsis would be worse than
        // answering with the first thirty characters of whatever it is.
        var result = AgentStatusText.Normalize(new string('x', 500), 30);

        Assert.NotNull(result);
        Assert.Equal(30, result.Status.Length);
        Assert.EndsWith("…", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void The_limit_stated_in_the_tool_description_is_the_limit_applied()
    {
        // The description and the nudge are attribute constants and cannot interpolate an int, so the number
        // exists twice. This is the assertion that stops the two drifting.
        Assert.Equal(
            StatusOptions.DefaultMaxCharsText,
            StatusOptions.DefaultMaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(StatusOptions.DefaultMaxCharsText, AgentStatusNudge.Text, StringComparison.Ordinal);
    }

    // ---- what the agent is told ------------------------------------------------------------------

    [Fact]
    public void A_report_that_fits_gets_a_bare_acknowledgement()
    {
        var result = AgentStatusText.Normalize("Rewiring the cache.", 240);

        Assert.NotNull(result);
        Assert.Equal("Noted.", AgentStatusText.Acknowledge(result));
    }

    [Fact]
    public void A_clipped_report_is_told_what_was_kept_and_what_the_limit_is()
    {
        var result = AgentStatusText.Normalize(string.Join(' ', Enumerable.Repeat("beta", 100)), 40);

        Assert.NotNull(result);
        Assert.Equal(
            $"Noted the first 40 characters: \"{result.Status}\". "
            + "Keep future reports to one or two sentences under 40 characters.",
            AgentStatusText.Acknowledge(result));
    }

    [Fact]
    public void A_report_that_is_both_too_long_and_multi_line_is_told_about_both()
    {
        var result = AgentStatusText.Normalize(string.Join(' ', Enumerable.Repeat("gamma", 50)) + "\nand more", 40);

        Assert.NotNull(result);
        var ack = AgentStatusText.Acknowledge(result);
        Assert.Contains("Noted the first 40 characters", ack, StringComparison.Ordinal);
        Assert.Contains("Kept the first line", ack, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_acknowledgement_quotes_the_hosts_configured_limit_not_the_default()
    {
        var h = await OpenAsync(new StatusOptions { MaxChars = 50, MinIntervalSeconds = 0 });
        await using var _ = h.Manager;

        var result = await h.Manager.ReportStatusAsync(h.SessionId, string.Join(' ', Enumerable.Repeat("delta", 40)));

        Assert.Equal(50, result.MaxChars);
        Assert.Contains("first 50 characters", AgentStatusText.Acknowledge(result), StringComparison.Ordinal);
    }

    // ---- the append ------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_is_persisted_broadcast_and_seen_on_the_spine()
    {
        var watcher = new WatchStatuses();
        var h = await OpenAsync(new StatusOptions { MinIntervalSeconds = 0 }, clock: null, watcher);
        await using var _ = h.Manager;

        await h.Manager.ReportStatusAsync(h.SessionId, "Found the deadlock; adding a timeout.");

        var stored = Assert.Single(await StatusesAsync(h));
        Assert.Equal("Found the deadlock; adding a timeout.", stored.Status);
        Assert.True(stored.Sequence > 0);

        var broadcast = Assert.Single(h.Broadcaster.Published, p => p.Event is AgentStatusEvent);
        Assert.Equal(h.SessionId, broadcast.SessionId);
        Assert.Equal("Found the deadlock; adding a timeout.", ((AgentStatusEvent)broadcast.Event).Status);

        Assert.Equal("Found the deadlock; adding a timeout.", Assert.Single(watcher.Seen));
    }

    [Fact]
    public async Task An_empty_report_is_refused()
    {
        var h = await OpenAsync();
        await using var _ = h.Manager;

        await Assert.ThrowsAsync<ArgumentException>(() => h.Manager.ReportStatusAsync(h.SessionId, "   "));
        Assert.Empty(await StatusesAsync(h));
    }

    [Fact]
    public async Task A_veto_reaches_the_agent_as_the_reason_and_records_nothing()
    {
        var h = await OpenAsync(null, null, new VetoStatus("it names an internal hostname"));
        await using var _ = h.Manager;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Manager.ReportStatusAsync(h.SessionId, "Deploying to db-prod-3."));

        Assert.Contains("it names an internal hostname", error.Message, StringComparison.Ordinal);
        Assert.Empty(await StatusesAsync(h));
    }

    [Fact]
    public async Task A_rewrite_is_what_gets_recorded()
    {
        var h = await OpenAsync(null, null, new RedactStatus());
        await using var _ = h.Manager;

        var result = await h.Manager.ReportStatusAsync(h.SessionId, "Logged in with hunter2; continuing.");

        Assert.Equal("Logged in with [redacted]; continuing.", result.Status);
        Assert.Equal("Logged in with [redacted]; continuing.", Assert.Single(await StatusesAsync(h)).Status);
    }

    [Fact]
    public async Task A_report_lands_on_a_dormant_session_too()
    {
        // A session restored from the catalogue has no live handle. Nothing may be woken to write one line —
        // but the line must still be persisted and broadcast, or a plugin reporting for a stopped session
        // would silently write nowhere.
        var broadcaster = new CollectingBroadcaster();
        var store = new InMemoryEventStore();
        await store.SaveSessionAsync(new SessionRecord(
            "sess-dormant", "scripted", _dir, "agent-1", UseWorktree: false, SkipPermissions: false,
            Sandboxed: false, CreatedAt: DateTimeOffset.UnixEpoch));

        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), store, broadcaster, NullLoggerFactory.Instance);
        await manager.RestoreAsync();

        await manager.ReportStatusAsync("sess-dormant", "Waiting on a review.");

        var stored = Assert.Single((await store.ReadSinceAsync("sess-dormant", 0)).OfType<AgentStatusEvent>());
        Assert.Equal("Waiting on a review.", stored.Status);
        Assert.Single(broadcaster.Published, p => p.Event is AgentStatusEvent);
    }

    // ---- the coalescing window -------------------------------------------------------------------

    [Fact]
    public async Task A_second_report_inside_the_window_replaces_the_first_and_lands_when_it_closes()
    {
        var clock = new StatusClock(DateTimeOffset.UnixEpoch);
        var h = await OpenAsync(new StatusOptions { MinIntervalSeconds = 20 }, clock);
        await using var _ = h.Manager;

        await h.Manager.ReportStatusAsync(h.SessionId, "Starting on the migration.");
        clock.Advance(TimeSpan.FromSeconds(1));
        await h.Manager.ReportStatusAsync(h.SessionId, "Migration is half done.");
        clock.Advance(TimeSpan.FromSeconds(1));
        await h.Manager.ReportStatusAsync(h.SessionId, "Migration failed on the index.");

        // Only the first got through; the two behind it collapsed into one pending line.
        Assert.Equal(["Starting on the migration."], (await StatusesAsync(h)).Select(s => s.Status));

        clock.Advance(TimeSpan.FromSeconds(20));
        var statuses = await WaitForStatusesAsync(h, 2, clock);

        // The newest survives the collapse — the middle report is the one nobody needed.
        Assert.Equal(
            ["Starting on the migration.", "Migration failed on the index."],
            statuses.Select(s => s.Status));
    }

    [Fact]
    public async Task A_report_after_the_window_is_written_straight_away()
    {
        var clock = new StatusClock(DateTimeOffset.UnixEpoch);
        var h = await OpenAsync(new StatusOptions { MinIntervalSeconds = 20 }, clock);
        await using var _ = h.Manager;

        await h.Manager.ReportStatusAsync(h.SessionId, "First.");
        clock.Advance(TimeSpan.FromSeconds(21));
        await h.Manager.ReportStatusAsync(h.SessionId, "Second.");

        Assert.Equal(["First.", "Second."], (await StatusesAsync(h)).Select(s => s.Status));
    }

    [Fact]
    public async Task A_held_report_still_acknowledges_the_agent_immediately()
    {
        var clock = new StatusClock(DateTimeOffset.UnixEpoch);
        var h = await OpenAsync(new StatusOptions { MinIntervalSeconds = 20 }, clock);
        await using var _ = h.Manager;

        await h.Manager.ReportStatusAsync(h.SessionId, "First.");
        var held = await h.Manager.ReportStatusAsync(h.SessionId, "Second, right behind it.");

        // The agent is not told to wait — the window is the host's business, not something to reason about.
        Assert.Equal("Second, right behind it.", held.Status);
        Assert.Equal("Noted.", AgentStatusText.Acknowledge(held));
    }

    [Fact]
    public async Task A_zero_interval_turns_coalescing_off()
    {
        var clock = new StatusClock(DateTimeOffset.UnixEpoch);
        var h = await OpenAsync(new StatusOptions { MinIntervalSeconds = 0 }, clock);
        await using var _ = h.Manager;

        await h.Manager.ReportStatusAsync(h.SessionId, "One.");
        await h.Manager.ReportStatusAsync(h.SessionId, "Two.");
        await h.Manager.ReportStatusAsync(h.SessionId, "Three.");

        Assert.Equal(["One.", "Two.", "Three."], (await StatusesAsync(h)).Select(s => s.Status));
    }

    // ---- the session catalogue -------------------------------------------------------------------

    [Fact]
    public async Task The_summary_carries_the_latest_line_and_when_it_was_said()
    {
        var h = await OpenAsync(new StatusOptions { MinIntervalSeconds = 0 });
        await using var _ = h.Manager;

        var before = await h.Manager.ListSessionSummariesAsync();
        Assert.Null(Assert.Single(before).LatestStatus);

        await h.Manager.ReportStatusAsync(h.SessionId, "Older line.");
        await h.Manager.ReportStatusAsync(h.SessionId, "Newest line.");

        var summary = Assert.Single(await h.Manager.ListSessionSummariesAsync());
        Assert.Equal("Newest line.", summary.LatestStatus);
        Assert.NotNull(summary.LatestStatusAt);
        var stored = await StatusesAsync(h);
        Assert.Equal(stored[^1].Timestamp, summary.LatestStatusAt);
    }

    [Fact]
    public async Task A_restored_session_gets_its_status_back_from_the_log()
    {
        // The cache is per-process; the log is not. A host that has just restarted must still be able to say
        // what each of its dormant sessions was last doing.
        var store = new InMemoryEventStore();
        await store.SaveSessionAsync(new SessionRecord(
            "sess-old", "scripted", _dir, "agent-1", UseWorktree: false, SkipPermissions: false,
            Sandboxed: false, CreatedAt: DateTimeOffset.UnixEpoch));
        await store.AppendAsync("sess-old", new AgentStatusEvent("Older line."));
        await store.AppendAsync("sess-old", new AgentStatusEvent("Last thing it said."));
        await store.AppendAsync("sess-old", new NoticeEvent("something afterwards"));

        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), store, new CollectingBroadcaster(),
            NullLoggerFactory.Instance);
        await manager.RestoreAsync();

        var summary = Assert.Single(await manager.ListSessionSummariesAsync());
        Assert.Equal("Last thing it said.", summary.LatestStatus);
    }

    // ---- the standing nudge ----------------------------------------------------------------------

    [Fact]
    public void The_mcp_server_states_the_nudge_in_its_instructions()
    {
        // ServerInstructions is the only channel that reaches every MCP client's model context regardless of
        // what its CLI does about system prompts — an unset one silently makes report_status opt-in.
        var options = new McpServerOptions();
        AgnesMcpEndpoints.ConfigureServer(options);

        Assert.Equal(AgentStatusNudge.Text, options.ServerInstructions);
        Assert.Contains("report_status", options.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_system_prompt_carries_the_nudge_only_for_a_session_that_got_the_agnes_server()
    {
        await using var withTools = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter("copilot")), new InMemoryEventStore(),
            new CollectingBroadcaster(), NullLoggerFactory.Instance,
            localMcp: new LocalMcpOptions { Url = "http://127.0.0.1:5117/mcp-agnes", BindUrl = "http://127.0.0.1:5117" });
        await withTools.MaterializeHostMcpAsync("copilot", "sess-tools", project: null, workspaceId: null, default);

        Assert.Equal(AgentStatusNudge.Text, withTools.ComposeSystemPrompt("sess-tools"));

        // No endpoint configured means no agnes server in that session's config: telling the model to call a
        // tool it hasn't got just costs it a failed turn.
        await using var withoutTools = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter("copilot")), new InMemoryEventStore(),
            new CollectingBroadcaster(), NullLoggerFactory.Instance);
        await withoutTools.MaterializeHostMcpAsync("copilot", "sess-bare", project: null, workspaceId: null, default);

        Assert.Null(withoutTools.ComposeSystemPrompt("sess-bare"));
    }
}
