using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;

namespace Agnes.Client.Simulation;

/// <summary>
/// An in-memory <see cref="IAgnesHost"/> that fakes agents and streams scripted transcripts —
/// so the desktop app can be built and exercised (tabs, reconnect, rendering) without a real
/// server. Subscribing to an unknown session id fabricates one with prior history, which makes
/// the app's auto-reconnect/restore flow testable across restarts.
/// </summary>
public sealed class SimulatedHost : IAgnesHost
{
    private static readonly AgentInfo[] Agents =
    [
        new("opencode", "OpenCode", "1.17.13", Available: true),
        new("claude-code", "Claude Code (ACP)", "2.0", Available: true),
        new("claude-code-native", "Claude Code (native)", "2.0", Available: true),
        new("codex", "Codex", "0.144", Available: true),
    ];

    private const string SampleDiff =
        "--- a/src/config.ts\n" +
        "+++ b/src/config.ts\n" +
        "@@ -5,13 +5,16 @@ import { load } from \"./io\";\n" +
        " export interface Config {\n" +
        "   host: string;\n" +
        "   port: number;\n" +
        "-  retries: number;\n" +
        "+  retries: number;      // now configurable\n" +
        "+  timeoutMs: number;\n" +
        " }\n" +
        " \n" +
        " export const defaultConfig: Config = {\n" +
        "   host: \"localhost\",\n" +
        "   port: 5081,\n" +
        "-  retries: 3,\n" +
        "+  retries: 5,\n" +
        "+  timeoutMs: 30_000,\n" +
        " };\n" +
        " \n" +
        " export function resolve(partial: Partial<Config>): Config {\n" +
        "   return { ...defaultConfig, ...partial };\n" +
        " }\n";

    private const string LongAnswer =
        """
        ## Agent Client Protocol (ACP)

        **ACP** is an open, JSON-RPC 2.0 standard that lets any coding agent talk to any editor over
        stdio — much like the *Language Server Protocol* did for language tooling.

        Instead of each editor hand-rolling an integration per agent, ACP defines a small set of methods:

        - `initialize` — negotiate capabilities
        - `session/new` — start a session
        - `session/prompt` — send a turn

        Progress streams back as `session/update` notifications carrying structured events:

        | Event | Carries |
        | --- | --- |
        | message chunk | streamed assistant text |
        | tool call | status + results |
        | permission | an approval request |

        ```json
        { "method": "session/prompt", "params": { "sessionId": "s1", "text": "hello" } }
        ```

        Because the stream is *structured* rather than a fixed terminal grid, a client can persist
        unlimited scrollback, render each event natively, and reconnect without losing context — which
        is exactly what Agnes builds on to serve many clients from one host.
        """;

    private readonly ConcurrentDictionary<string, SimSession> _sessions = new();
    private int _counter;

    public SimulatedHost(string hostUrl = "sim://demo") => HostUrl = hostUrl;

    public string HostUrl { get; }
    public AgnesConnectionState State { get; private set; } = AgnesConnectionState.Disconnected;
    public event Action<AgnesConnectionState>? StateChanged;

    // The simulated host never changes its agent set; required by the interface.
#pragma warning disable CS0067
    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;
#pragma warning restore CS0067

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Set(AgnesConnectionState.Connecting);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        Set(AgnesConnectionState.Connected);
    }

    public Task<HostInfo> GetHostInfoAsync()
        => Task.FromResult(new HostInfo("sim-host", "Simulated Host", "0.1.0"));

    public Task<IReadOnlyList<AgentInfo>> ListAgentsAsync()
        => Task.FromResult<IReadOnlyList<AgentInfo>>(Agents);

    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true)
    {
        var id = $"sim-{Interlocked.Increment(ref _counter):x4}";
        var session = _sessions.GetOrAdd(id, _ => new SimSession(id, adapterId, workingDirectory));
        session.Emit(new MessageChunkEvent(MessageRole.Assistant,
            new TextContent($"Session ready on {DisplayName(adapterId)}. Ask me anything.")));
        session.Emit(new TurnEndedEvent(StopReason.EndTurn));
        session.RecordUsage(0, 0); // seed the context-window meter (same UsageReportedEvent a real agent emits)
        session.SkipPermissions = skipPermissions;
        return Task.FromResult(new SessionInfo(id, adapterId, workingDirectory, session.Head, Modes, session.CurrentModeId, SandboxFor(id), skipPermissions));
    }

    public Task<ForkPlan?> ProposeForkAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return Task.FromResult<ForkPlan?>(null);
        }

        // Propose a numeral-incremented sibling (no filesystem probing offline — just increment).
        var dir = session.Cwd;
        var proposed = System.Text.RegularExpressions.Regex.IsMatch(dir, @"\d+$")
            ? System.Text.RegularExpressions.Regex.Replace(dir, @"(\d+)$", m => (long.Parse(m.Value) + 1).ToString())
            : dir + "2";
        return Task.FromResult<ForkPlan?>(new ForkPlan(sessionId, dir, proposed, CanCopySandbox: true));
    }

    public Task<SessionInfo> ForkSessionAsync(string sourceSessionId, string targetDirectory, bool copySandbox = true)
    {
        // Offline: just open a fresh simulated session in the target directory.
        var adapterId = _sessions.TryGetValue(sourceSessionId, out var src) ? src.AdapterId : "claude-code-native";
        return OpenSessionAsync(adapterId, targetDirectory);
    }

    // A simulated Incus sandbox so the desktop sandbox chip is demoable + screenshot-verifiable offline.
    private readonly ConcurrentDictionary<string, string> _sandboxState = new();

    private SandboxStatus SandboxFor(string sessionId)
    {
        var state = _sandboxState.GetOrAdd(sessionId, _ => "Running");
        return new SandboxStatus("incus", $"agnes-{sessionId}", state);
    }

    public Task PauseSandboxAsync(string sessionId)
    {
        _sandboxState[sessionId] = "Paused";
        return Task.CompletedTask;
    }

    public Task ResumeSandboxAsync(string sessionId)
    {
        _sandboxState[sessionId] = "Running";
        return Task.CompletedTask;
    }

    public Task DeleteSandboxAsync(string sessionId)
    {
        _sandboxState.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task StopSessionAsync(string sessionId)
    {
        if (_sandboxState.ContainsKey(sessionId))
        {
            _sandboxState[sessionId] = "Stopped";
        }

        return Task.CompletedTask;
    }

    public Task<SandboxStatus?> GetSandboxStatusAsync(string sessionId)
        => Task.FromResult<SandboxStatus?>(_sandboxState.ContainsKey(sessionId) ? SandboxFor(sessionId) : null);

    public Task<SessionView> SubscribeAsync(string sessionId, long since = 0)
    {
        // Unknown id => a restored session (app relaunch): seed it with prior history.
        var session = _sessions.GetOrAdd(sessionId, id => CreateRestored(id));
        session.View.ApplySnapshot(session.Snapshot());
        return Task.FromResult(session.View);
    }

    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return Task.CompletedTask;
        }

        var text = string.Concat(content.OfType<TextContent>().Select(t => t.Text));
        foreach (var block in content)
        {
            session.Emit(new MessageChunkEvent(MessageRole.User, block));
        }

        // Grow the context window and accrue a little cost, then emit the same UsageReportedEvent a
        // real agent would — so the demo exercises the real per-session usage path, not a fake one.
        session.RecordUsage(1_200 + text.Length * 3, 0.002 + text.Length * 0.00002);

        _ = Task.Run(() => RespondAsync(session, text, session.NewTurn()));
        return Task.CompletedTask;
    }

    public Task CancelAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.CancelTurn())
        {
            session.Emit(new TurnEndedEvent(StopReason.Cancelled));
        }

        return Task.CompletedTask;
    }

    private static readonly SessionMode[] Modes =
    [
        new("ask", "Ask"),
        new("code", "Code"),
    ];

    public Task SetModeAsync(string sessionId, string modeId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.CurrentModeId = modeId;
            session.Emit(new ModeChangedEvent(modeId));
        }

        return Task.CompletedTask;
    }

    private bool _committed;

    public Task<GitStatus> GetGitStatusAsync(string sessionId)
    {
        var changes = _committed
            ? Array.Empty<GitFileChange>()
            : new[] { new GitFileChange("src/config.ts", "M"), new GitFileChange("notes.txt", "??") };
        return Task.FromResult(new GitStatus(true, "main", changes.Length > 0, changes));
    }

    public Task<GitCommitResult> GitCommitAsync(string sessionId, string message)
    {
        _committed = true;
        return Task.FromResult(new GitCommitResult(true, $"[main 1a2b3c4] {message}"));
    }

    private readonly List<ScheduledTask> _scheduled = [];
    private readonly List<InboxRun> _inbox =
    [
        new("run-seed", "task-seed", "Nightly dependency audit", "No vulnerable packages found.", DateTimeOffset.UtcNow.AddHours(-2)),
    ];

    public event Action<InboxRun>? InboxRunReceived;

    public Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request)
    {
        var task = new ScheduledTask($"task-{_scheduled.Count + 1}", request.AdapterId, request.WorkingDirectory,
            request.Prompt, Math.Max(5, request.IntervalSeconds), Enabled: true);
        _scheduled.Add(task);
        // Simulate a first run completing immediately so the inbox shows something.
        var run = new InboxRun($"run-{_inbox.Count + 1}", task.Id, request.Prompt, "Completed (simulated).", DateTimeOffset.UtcNow);
        _inbox.Insert(0, run);
        InboxRunReceived?.Invoke(run);
        return Task.FromResult(task);
    }

    public Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync()
        => Task.FromResult<IReadOnlyList<ScheduledTask>>(_scheduled.ToArray());

    public Task RemoveScheduledTaskAsync(string taskId)
    {
        _scheduled.RemoveAll(t => t.Id == taskId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InboxRun>> GetInboxAsync()
        => Task.FromResult<IReadOnlyList<InboxRun>>(_inbox.ToArray());

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            var allowed = optionId.StartsWith("allow", StringComparison.OrdinalIgnoreCase);
            session.Emit(new PermissionResolvedEvent(requestId, optionId,
                allowed ? PermissionOutcome.Allowed : PermissionOutcome.Denied));
            session.Emit(new MessageChunkEvent(MessageRole.Assistant,
                new TextContent(allowed ? "Approved — carrying on." : "Understood, I won't do that.")));
            session.Emit(new TurnEndedEvent(StopReason.EndTurn));
        }

        return Task.CompletedTask;
    }

    public Task AnswerQuestionAsync(string sessionId, string requestId, IReadOnlyList<QuestionAnswer> answers)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Emit(new QuestionAnsweredEvent(requestId));
            var summary = answers.Count == 0
                ? "No problem — I'll use sensible defaults."
                : "Got it — " + string.Join("; ", answers.Select(a =>
                    $"{a.QuestionId}: {(a.SelectedLabels.Count > 0 ? string.Join(", ", a.SelectedLabels) : "(no choice)")}"
                    + (string.IsNullOrWhiteSpace(a.Notes) ? "" : $" ({a.Notes})")));
            session.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent(summary + " Proceeding.")));
            session.Emit(new TurnEndedEvent(StopReason.EndTurn));
        }

        return Task.CompletedTask;
    }

    // ---- plugin management (in-memory: a small fake feed + an installed store with real consent semantics) ----

    private sealed record SimPackage(string PackageId, string DisplayName, string Description, string Publisher,
        IReadOnlyList<string> Versions, bool IsReviewed, IReadOnlyList<string> Capabilities);

    private static readonly SimPackage[] Catalog =
    [
        new("Agnes.Plugins.SlackNotifications", "Slack notifications", "Push turn-ready and permission alerts to a Slack channel.",
            "agnes-community", ["1.2.0", "1.1.0"], IsReviewed: true, ["network"]),
        new("Agnes.Plugins.GitLabHost", "GitLab host", "Detect GitLab remotes and list/checkout merge requests.",
            "agnes-community", ["0.9.1"], IsReviewed: false, ["network"]),
        new("Agnes.Plugins.LocalPrompts", "Local prompt library", "A prompt registry backed by a local folder — no special access.",
            "agnes-community", ["2.0.0"], IsReviewed: true, []),
    ];

    private sealed class SimInstalled
    {
        public required string PackageId { get; init; }
        public required string Version { get; set; }
        public bool Enabled { get; set; } = true;
        public required IReadOnlyList<string> Capabilities { get; init; }
        public Dictionary<string, string> Settings { get; } = [];
    }

    private readonly ConcurrentDictionary<string, SimInstalled> _plugins = new();

    public Task<IReadOnlyList<PluginSearchResultDto>> SearchPluginsAsync(string query)
    {
        var q = (query ?? string.Empty).Trim();
        var hits = Catalog
            .Where(p => q.Length == 0
                || p.PackageId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || p.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(p => new PluginSearchResultDto(p.PackageId, p.DisplayName, p.Description, p.Publisher, p.Versions, p.IsReviewed))
            .ToArray();
        return Task.FromResult<IReadOnlyList<PluginSearchResultDto>>(hits);
    }

    public Task<PluginInstallOutcome> InstallPluginAsync(InstallPluginRequest request)
    {
        var pkg = Catalog.FirstOrDefault(p => p.PackageId == request.PackageId);
        if (pkg is null)
        {
            return Task.FromResult(new PluginInstallOutcome(false, null, false, [], $"No such package '{request.PackageId}'."));
        }

        // Consent: refuse (without running anything) until every declared capability has been granted.
        var missing = pkg.Capabilities.Except(request.GrantedCapabilities).ToArray();
        if (missing.Length > 0)
        {
            return Task.FromResult(new PluginInstallOutcome(false, null, true, missing, null));
        }

        var version = request.Version ?? pkg.Versions[0];
        _plugins[pkg.PackageId] = new SimInstalled { PackageId = pkg.PackageId, Version = version, Capabilities = pkg.Capabilities };
        return Task.FromResult(new PluginInstallOutcome(true, ToDto(pkg.PackageId), false, [], null));
    }

    public Task<PluginInstallOutcome> UpdatePluginAsync(string pluginId, IReadOnlyList<string> grantedCapabilities)
    {
        if (!_plugins.TryGetValue(pluginId, out var installed))
        {
            return Task.FromResult(new PluginInstallOutcome(false, null, false, [], $"'{pluginId}' is not installed."));
        }

        var pkg = Catalog.First(p => p.PackageId == installed.PackageId);
        var missing = pkg.Capabilities.Except(grantedCapabilities).ToArray();
        if (missing.Length > 0)
        {
            return Task.FromResult(new PluginInstallOutcome(false, null, true, missing, null));
        }

        installed.Version = pkg.Versions[0];
        return Task.FromResult(new PluginInstallOutcome(true, ToDto(pluginId), false, [], null));
    }

    public Task SetPluginEnabledAsync(string pluginId, bool enabled)
    {
        if (_plugins.TryGetValue(pluginId, out var p)) { p.Enabled = enabled; }
        return Task.CompletedTask;
    }

    public Task ConfigurePluginAsync(string pluginId, IReadOnlyDictionary<string, string> settings)
    {
        if (_plugins.TryGetValue(pluginId, out var p))
        {
            p.Settings.Clear();
            foreach (var kv in settings) { p.Settings[kv.Key] = kv.Value; }
        }
        return Task.CompletedTask;
    }

    public Task UninstallPluginAsync(string pluginId)
    {
        _plugins.TryRemove(pluginId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InstalledPluginDto>> ListInstalledPluginsAsync()
        => Task.FromResult<IReadOnlyList<InstalledPluginDto>>(_plugins.Keys.OrderBy(k => k).Select(ToDto).ToArray());

    private InstalledPluginDto ToDto(string pluginId)
    {
        var p = _plugins[pluginId];
        return new InstalledPluginDto(pluginId, p.Version, p.Enabled, p.Capabilities, UpdateAvailable: false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- scripted behavior ----

    private static async Task RespondAsync(SimSession session, string prompt, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(250, cancel).ConfigureAwait(false);
            session.Emit(new ThoughtChunkEvent(new TextContent("Reading the request and planning a response…")));

            if (Mentions(prompt, "delete", "remove", "rm "))
            {
                session.Emit(new ToolCallEvent("tc-danger", "build/", ToolKind.Delete, ToolCallStatus.Pending,
                    [new TextContent("rm -rf build/")]));
                session.Emit(new PermissionRequestedEvent("req-1", "tc-danger", "Delete files in the working directory?",
                [
                    new PermissionOption("allow-once", "Allow once", PermissionOptionKind.AllowOnce),
                    new PermissionOption("allow-always", "Always allow deletes", PermissionOptionKind.AllowAlways),
                    new PermissionOption("reject-once", "Reject", PermissionOptionKind.RejectOnce),
                ]));
                return; // wait for the client to answer (RespondPermissionAsync continues the turn)
            }

            if (Mentions(prompt, "question", "ask me", "clarify", "which "))
            {
                session.Emit(new QuestionAskedEvent("q-1", "tc-q",
                [
                    new AgentQuestion("db", "Database", "Which database should the sample app use?",
                    [
                        new QuestionChoice("SQLite", "Zero-config, file-based — great for local/dev."),
                        new QuestionChoice("Postgres", "Full-featured, needs a running server."),
                    ]),
                    new AgentQuestion("features", "Features", "Which features should I scaffold?",
                    [
                        new QuestionChoice("Auth", "Login + sessions."),
                        new QuestionChoice("REST API", "CRUD endpoints."),
                        new QuestionChoice("Realtime", "WebSocket updates."),
                    ], MultiSelect: true),
                ]));
                return; // wait for the client to answer (AnswerQuestionAsync continues the turn)
            }

            if (Mentions(prompt, "explain", "detail", "describe", "overview"))
            {
                foreach (var chunk in LongAnswer.Split(' '))
                {
                    await Task.Delay(12, cancel).ConfigureAwait(false);
                    session.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent(chunk + " ")));
                }

                session.Emit(new TurnEndedEvent(StopReason.EndTurn));
                return;
            }

            if (Mentions(prompt, "file", "create", "write", "plan"))
            {
                session.Emit(new PlanEvent(
                [
                    new PlanEntry("Inspect the working directory", "completed"),
                    new PlanEntry("Write the requested file", "in_progress"),
                    new PlanEntry("Confirm the result", "pending"),
                ]));
                await Task.Delay(150, cancel).ConfigureAwait(false);
                session.Emit(new ToolCallEvent("tc-search", "config", ToolKind.Search, ToolCallStatus.Completed,
                    [new TextContent("3 matches in src/config.ts")]));
                await Task.Delay(150, cancel).ConfigureAwait(false);
                session.Emit(new ToolCallEvent("tc-read", "src/config.ts", ToolKind.Read, ToolCallStatus.Completed,
                    [new TextContent("read 42 lines")]));
                await Task.Delay(200, cancel).ConfigureAwait(false);
                session.Emit(new ToolCallEvent("tc-1", "src/config.ts", ToolKind.Edit, ToolCallStatus.InProgress, [new TextContent(SampleDiff)]));
                await Task.Delay(400, cancel).ConfigureAwait(false);
                session.Emit(new ToolCallUpdateEvent("tc-1", ToolCallStatus.Completed, [new TextContent(SampleDiff)]));

                // Spawn a subagent to review the change — its own sub-conversation.
                await Task.Delay(150, cancel).ConfigureAwait(false);
                session.Emit(new SubagentStartedEvent("sub-review", "code-reviewer"));
                session.Emit(new MessageChunkEvent(MessageRole.Assistant,
                    new TextContent("Reviewing the change for edge cases…")) { AgentId = "sub-review" });
                await Task.Delay(150, cancel).ConfigureAwait(false);
                session.Emit(new ToolCallEvent("tc-rev", "src/config.ts", ToolKind.Read, ToolCallStatus.Completed,
                    [new TextContent("read 16 lines")]) { AgentId = "sub-review" });
                await Task.Delay(150, cancel).ConfigureAwait(false);
                session.Emit(new MessageChunkEvent(MessageRole.Assistant,
                    new TextContent("Looks good — timeoutMs has a sensible default and retries stays bounded.")) { AgentId = "sub-review" });
            }

            var reply = BuildReply(prompt);
            foreach (var word in reply.Split(' '))
            {
                await Task.Delay(35, cancel).ConfigureAwait(false);
                session.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent(word + " ")));
            }

            session.Emit(new TurnEndedEvent(StopReason.EndTurn));
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-turn: CancelAsync emits the TurnEnded(Cancelled), so just stop.
        }
    }

    private static string BuildReply(string prompt)
        => string.IsNullOrWhiteSpace(prompt)
            ? "I'm a simulated agent. Everything you see here is scripted, streamed the same way a real ACP agent would send it."
            : $"Here's a simulated response to \"{prompt.Trim()}\". In a real session this text would stream from the model over ACP, with tool calls and permissions rendered exactly like this.";

    private SimSession CreateRestored(string id)
    {
        var session = new SimSession(id, "opencode", "/tmp/agnes");
        session.Emit(new MessageChunkEvent(MessageRole.User, new TextContent("(restored) summarize what we did last time")));
        session.Emit(new MessageChunkEvent(MessageRole.Assistant,
            new TextContent("Earlier we scaffolded the project and ran a couple of prompts. This tab was restored on relaunch.")));
        session.Emit(new ToolCallEvent("tc-old", "Read README.md", ToolKind.Read, ToolCallStatus.Completed, []));
        session.Emit(new TurnEndedEvent(StopReason.EndTurn));
        return session;
    }

    private static bool Mentions(string text, params string[] needles)
        => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string DisplayName(string adapterId)
        => Agents.FirstOrDefault(a => a.AdapterId == adapterId)?.DisplayName ?? adapterId;

    private void Set(AgnesConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private sealed class SimSession
    {
        private readonly object _gate = new();
        private readonly List<SessionEvent> _log = [];
        private long _seq;
        private CancellationTokenSource? _turn;
        public bool SkipPermissions { get; set; }

        public SimSession(string id, string adapterId, string cwd)
        {
            Id = id;
            AdapterId = adapterId;
            Cwd = cwd;
            View = new SessionView(id);
        }

        public string Id { get; }
        public string AdapterId { get; }
        public string Cwd { get; }
        public string CurrentModeId { get; set; } = "ask";
        public SessionView View { get; }
        public long Head { get { lock (_gate) { return _seq; } } }

        /// <summary>Starts a new turn, cancelling any previous one, and returns its token.</summary>
        public CancellationToken NewTurn()
        {
            lock (_gate)
            {
                _turn?.Cancel();
                _turn = new CancellationTokenSource();
                return _turn.Token;
            }
        }

        /// <summary>Cancels the active turn; returns true if one was running.</summary>
        public bool CancelTurn()
        {
            lock (_gate)
            {
                if (_turn is { IsCancellationRequested: false })
                {
                    _turn.Cancel();
                    return true;
                }

                return false;
            }
        }

        public void Emit(SessionEvent @event)
        {
            lock (_gate)
            {
                var stamped = @event with { Sequence = ++_seq, Timestamp = DateTimeOffset.UtcNow };
                _log.Add(stamped);
                View.Apply(stamped);
            }
        }

        // Representative per-session usage: a real Claude Code window is 200k tokens.
        private const long Window = 200_000;
        private long _contextUsed = 8_400;
        private double _costUsd;

        /// <summary>Advances the (simulated) context/cost and emits the real UsageReportedEvent shape.</summary>
        public void RecordUsage(long contextDelta, double costDelta)
        {
            long ctx;
            double cost;
            lock (_gate)
            {
                _contextUsed = Math.Min(Window, _contextUsed + contextDelta);
                _costUsd += costDelta;
                ctx = _contextUsed;
                cost = _costUsd;
            }

            Emit(new UsageReportedEvent(ContextTokens: ctx, ContextWindow: Window, CostUsd: cost > 0 ? cost : null));
        }

        public SessionSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new SessionSnapshot(
                    new SessionInfo(Id, AdapterId, Cwd, _seq, Modes, CurrentModeId, new SandboxStatus("incus", $"agnes-{Id}", "Running"), SkipPermissions),
                    _log.ToArray(), _seq);
            }
        }
    }
}
