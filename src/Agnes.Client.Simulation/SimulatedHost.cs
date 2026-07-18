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

    private const int Quota = 5000;

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
        "The Agent Client Protocol (ACP) is an open, JSON-RPC 2.0 standard that lets any coding " +
        "agent talk to any editor over stdio, much like the Language Server Protocol did for " +
        "language tooling. Instead of each editor hand-rolling an integration per agent, ACP " +
        "defines a small set of methods — initialize, session/new, session/prompt — and a stream " +
        "of session/update notifications carrying structured events: message and thought chunks, " +
        "tool calls with status, plans, and permission requests. Because the stream is structured " +
        "rather than a fixed terminal grid, a client can persist unlimited scrollback, render each " +
        "event natively at its own size, and reconnect without losing context — which is exactly " +
        "what Agnes builds on to serve many clients from one host.";

    private readonly ConcurrentDictionary<string, SimSession> _sessions = new();
    private int _counter;
    private int _remaining = 4820;

    public SimulatedHost(string hostUrl = "sim://demo") => HostUrl = hostUrl;

    public string HostUrl { get; }
    public AgnesConnectionState State { get; private set; } = AgnesConnectionState.Disconnected;
    public event Action<AgnesConnectionState>? StateChanged;

    public string? UsageSummary { get; private set; }
    public event Action<string?>? UsageChanged;

    // The simulated host never changes its agent set; required by the interface.
#pragma warning disable CS0067
    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;
#pragma warning restore CS0067

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Set(AgnesConnectionState.Connecting);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        Set(AgnesConnectionState.Connected);
        UpdateUsage();
    }

    private void UpdateUsage()
    {
        UsageSummary = $"Free tier · {_remaining:N0} / {Quota:N0} tokens today";
        UsageChanged?.Invoke(UsageSummary);
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

        _remaining = Math.Max(0, _remaining - 140 - text.Length);
        UpdateUsage();

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

    private static async Task RespondAsync(SimSession session, string prompt, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(250, cancel).ConfigureAwait(false);
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

        public SessionSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new SessionSnapshot(new SessionInfo(Id, AdapterId, Cwd, _seq), _log.ToArray(), _seq);
            }
        }
    }
}
