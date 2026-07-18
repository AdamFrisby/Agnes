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
        new("claude-code", "Claude Code", "2.0", Available: true),
        new("codex", "Codex", "0.9", Available: true),
    ];

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

    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory)
    {
        var id = $"sim-{Interlocked.Increment(ref _counter):x4}";
        var session = _sessions.GetOrAdd(id, _ => new SimSession(id, adapterId, workingDirectory));
        session.Emit(new MessageChunkEvent(MessageRole.Assistant,
            new TextContent($"Session ready on {DisplayName(adapterId)}. Ask me anything.")));
        session.Emit(new TurnEndedEvent(StopReason.EndTurn));
        return Task.FromResult(new SessionInfo(id, adapterId, workingDirectory, session.Head));
    }

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

        _ = Task.Run(() => RespondAsync(session, text));
        return Task.CompletedTask;
    }

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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- scripted behavior ----

    private static async Task RespondAsync(SimSession session, string prompt)
    {
        await Task.Delay(250).ConfigureAwait(false);
        session.Emit(new ThoughtChunkEvent(new TextContent("Reading the request and planning a response…")));

        if (Mentions(prompt, "delete", "remove", "rm "))
        {
            session.Emit(new PermissionRequestedEvent("req-1", "tc-danger", "Delete files in the working directory?",
            [
                new PermissionOption("allow-once", "Allow once", PermissionOptionKind.AllowOnce),
                new PermissionOption("reject-once", "Reject", PermissionOptionKind.RejectOnce),
            ]));
            return; // wait for the client to answer (RespondPermissionAsync continues the turn)
        }

        if (Mentions(prompt, "file", "create", "write", "plan"))
        {
            session.Emit(new PlanEvent(
            [
                new PlanEntry("Inspect the working directory", "completed"),
                new PlanEntry("Write the requested file", "in_progress"),
                new PlanEntry("Confirm the result", "pending"),
            ]));
            await Task.Delay(200).ConfigureAwait(false);
            session.Emit(new ToolCallEvent("tc-1", "Write output.txt", ToolKind.Edit, ToolCallStatus.InProgress, []));
            await Task.Delay(400).ConfigureAwait(false);
            session.Emit(new ToolCallUpdateEvent("tc-1", ToolCallStatus.Completed, [new TextContent("wrote 3 lines")]));
        }

        var reply = BuildReply(prompt);
        foreach (var word in reply.Split(' '))
        {
            await Task.Delay(35).ConfigureAwait(false);
            session.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent(word + " ")));
        }

        session.Emit(new TurnEndedEvent(StopReason.EndTurn));
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
        public SessionView View { get; }
        public long Head { get { lock (_gate) { return _seq; } } }

        public void Emit(SessionEvent @event)
        {
            lock (_gate)
            {
                var stamped = @event with { Sequence = ++_seq, Timestamp = DateTimeOffset.UtcNow };
                _log.Add(stamped);
                View.Apply(stamped);
            }
        }

        public SessionSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new SessionSnapshot(new SessionInfo(Id, AdapterId, Cwd, _seq), _log.ToArray(), _seq);
            }
        }
    }
}
