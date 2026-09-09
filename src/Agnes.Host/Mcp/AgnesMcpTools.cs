using System.ComponentModel;
using ModelContextProtocol.Server;
using Agnes.Host.Sharing;
using Agnes.Protocol;

namespace Agnes.Host.Mcp;

/// <summary>
/// Agnes exposed AS an MCP server: each tool is a thin, authorized wrapper over an existing host action
/// (<see cref="IAgnesMcpBackend"/>), so an MCP caller — the OpenAI Realtime voice endpoint, or any MCP
/// client — can drive Agnes without any new server-side authority. Every call first resolves the caller from
/// the request's Agnes device token (the same token a paired client uses); an unauthenticated call is
/// rejected. This is the "Agnes as MCP server" seam; voice is one consumer of it.
/// <para>
/// "No new authority" has to be enforced, not merely intended. Every session-scoped tool asks the SAME
/// <see cref="SessionAccessDecider"/> the SignalR hub and the display channel ask, for the same
/// <see cref="SessionAccessKind"/> the equivalent hub method requires — because a device token that the hub
/// refuses must not be able to list, read and drive every session on the host merely by arriving over
/// <c>/mcp-agnes</c> instead. The catalogue tools filter rather than refuse, exactly as
/// <c>AgnesHub.ListSessions</c> does, so a caller is never shown a session it would then be denied.
/// </para>
/// </summary>
[McpServerToolType]
public sealed partial class AgnesMcpTools
{
    private readonly IAgnesMcpBackend _backend;
    private readonly IMcpDeviceAuthenticator _authenticator;
    private readonly IMcpCallerTokenSource _tokenSource;
    private readonly SessionMcpTokens _sessionTokens;
    private readonly IAgnesDisplayBackend _display;
    private readonly SessionAccessDecider _access;

    public AgnesMcpTools(
        IAgnesMcpBackend backend,
        IMcpDeviceAuthenticator authenticator,
        IMcpCallerTokenSource tokenSource,
        SessionMcpTokens sessionTokens,
        IAgnesDisplayBackend display,
        SessionAccessDecider access)
    {
        _backend = backend;
        _authenticator = authenticator;
        _tokenSource = tokenSource;
        _sessionTokens = sessionTokens;
        _display = display;
        _access = access;
    }

    /// <summary>Authenticates the current request and returns the caller id, or throws so the tool call is
    /// rejected. Called at the top of every tool — actions never run for an unauthenticated request.</summary>
    private string RequireCaller()
    {
        // A sandboxed agent's session token deliberately does NOT satisfy this. These tools act across every
        // session on the host — prompting them, reading their transcripts, answering their permissions — and
        // that is the authority of a paired human, not of an agent running inside one session's VM.
        if (_sessionTokens.SessionFor(_tokenSource.CurrentToken) is not null)
        {
            throw new McpUnauthenticatedException(
                "This tool needs a paired device token; a session token may only manage its own session's goals.");
        }

        var caller = _authenticator.ResolveCaller(_tokenSource.CurrentToken);
        if (caller is null)
        {
            throw new McpUnauthenticatedException("A valid Agnes device token is required to use this MCP endpoint.");
        }

        return caller;
    }

    /// <summary>Authenticates a call to a tool that acts on ONE session (the goal tools, sending the user a
    /// file) and returns the session it may act on: the caller's own when a session token was presented (any
    /// <c>sessionId</c> argument is then ignored rather than trusted), or the named one for a paired device.
    /// The token is the identity, never the argument — that is what stops an agent naming somebody else's
    /// session.</summary>
    private async Task<string> RequireActingSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (_sessionTokens.SessionFor(_tokenSource.CurrentToken) is { } own)
        {
            return own; // an agent may only ever act on itself
        }

        // Authenticate first: a caller with no credential at all is "who are you", not "which session".
        RequireCaller();
        var named = sessionId is { Length: > 0 } value
            ? value
            : throw new ArgumentException("A sessionId is required when calling with a device token.", nameof(sessionId));

        // A device naming somebody else's session is doing what send_prompt does, so it needs what send_prompt needs.
        await RequireSessionAsync(named, SessionAccessKind.Prompt, cancellationToken).ConfigureAwait(false);
        return named;
    }

    /// <summary>The calling session when it is an agent, else null for a paired device.</summary>
    private string? CallerSession => _sessionTokens.SessionFor(_tokenSource.CurrentToken);

    /// <summary>
    /// Authenticates the caller AND checks it may do <paramref name="kind"/> to <paramref name="sessionId"/>.
    /// One line at the top of every session-scoped tool — the whole point being that the answer comes from the
    /// hub's decider, so a change to sharing reaches this endpoint in the same commit.
    /// </summary>
    private async Task RequireSessionAsync(string sessionId, SessionAccessKind kind, CancellationToken cancellationToken)
    {
        RequireCaller();
        if (!await _access.DecideAsync(sessionId, kind, _access.CallerFor(_tokenSource.CurrentToken), cancellationToken)
                .ConfigureAwait(false))
        {
            throw new McpForbiddenException(
                $"This device has no {kind.ToString().ToLowerInvariant()} access to session '{sessionId}'.");
        }
    }

    /// <summary>Whether the caller may watch a session — used to filter the catalogue tools.</summary>
    private Task<bool> CanSeeAsync(string sessionId, CancellationToken cancellationToken)
        => _access.DecideAsync(sessionId, SessionAccessKind.Subscribe, _access.CallerFor(_tokenSource.CurrentToken), cancellationToken);

    [McpServerTool(Name = "list_sessions", ReadOnly = true)]
    [Description("List the coding-agent sessions on this Agnes host (id, title, and coarse status: working, idle, or dormant).")]
    public async Task<IReadOnlyList<McpSessionSummary>> ListSessions(CancellationToken cancellationToken = default)
    {
        RequireCaller();
        var sessions = await _backend.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        var visible = new List<McpSessionSummary>(sessions.Count);
        foreach (var session in sessions)
        {
            if (await CanSeeAsync(session.SessionId, cancellationToken).ConfigureAwait(false))
            {
                visible.Add(session);
            }
        }

        return visible;
    }

    [McpServerTool(Name = "get_session_status", ReadOnly = true)]
    [Description("Get the current status of one session: its coarse status, current mode, available modes, and how many permission requests are open.")]
    public async Task<McpSessionStatus> GetSessionStatus(
        [Description("The session id to inspect.")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        await RequireSessionAsync(sessionId, SessionAccessKind.Subscribe, cancellationToken).ConfigureAwait(false);
        return await _backend.GetSessionStatusAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unknown session '{sessionId}'.", nameof(sessionId));
    }

    [McpServerTool(Name = "send_prompt")]
    [Description("Send a text instruction to a session, exactly as if a user had typed it. Starts (or queues) an agent turn.")]
    public async Task<McpActionResult> SendPrompt(
        [Description("The target session id.")] string sessionId,
        [Description("The instruction to deliver to the agent.")] string text,
        CancellationToken cancellationToken = default)
    {
        await RequireSessionAsync(sessionId, SessionAccessKind.Prompt, cancellationToken).ConfigureAwait(false);
        await _backend.SendPromptAsync(sessionId, text, cancellationToken).ConfigureAwait(false);
        return McpActionResult.Success($"Prompt sent to session {sessionId}.");
    }

    [McpServerTool(Name = "respond_permission")]
    [Description("Answer an outstanding permission request in a session by choosing one of its option ids (e.g. allow or reject).")]
    public async Task<McpActionResult> RespondPermission(
        [Description("The session the permission request belongs to.")] string sessionId,
        [Description("The permission request id (from list_open_approvals).")] string requestId,
        [Description("The chosen option id from the request's options.")] string optionId,
        CancellationToken cancellationToken = default)
    {
        await RequireSessionAsync(sessionId, SessionAccessKind.Approve, cancellationToken).ConfigureAwait(false);
        await _backend.RespondPermissionAsync(sessionId, requestId, optionId, cancellationToken).ConfigureAwait(false);
        return McpActionResult.Success($"Responded to permission {requestId} with option {optionId}.");
    }

    [McpServerTool(Name = "set_mode")]
    [Description("Switch a session's mode (for example Ask or Code) by its mode id.")]
    public async Task<McpActionResult> SetMode(
        [Description("The target session id.")] string sessionId,
        [Description("The mode id to switch to.")] string modeId,
        CancellationToken cancellationToken = default)
    {
        await RequireSessionAsync(sessionId, SessionAccessKind.Prompt, cancellationToken).ConfigureAwait(false);
        await _backend.SetModeAsync(sessionId, modeId, cancellationToken).ConfigureAwait(false);
        return McpActionResult.Success($"Session {sessionId} switched to mode {modeId}.");
    }

    [McpServerTool(Name = "list_open_approvals", ReadOnly = true)]
    [Description("List permission requests across all sessions that are still waiting on a human decision.")]
    public async Task<IReadOnlyList<McpOpenApproval>> ListOpenApprovals(CancellationToken cancellationToken = default)
    {
        RequireCaller();
        var approvals = await _backend.ListOpenApprovalsAsync(cancellationToken).ConfigureAwait(false);
        var visible = new List<McpOpenApproval>(approvals.Count);
        foreach (var a in approvals)
        {
            // An approval with no session can't be attributed to one, so it stays owner-visible only.
            if (a.SessionId is { Length: > 0 } id && await CanSeeAsync(id, cancellationToken).ConfigureAwait(false))
            {
                visible.Add(new McpOpenApproval(a.SessionId, a.RequestId, a.Title, a.Kind.ToString(), a.RequestedAt));
            }
        }

        return visible;
    }

    [McpServerTool(Name = "arm_goal")]
    [Description("Arm a standing goal on a session. If that session then falls idle for longer than "
        + "idleSeconds without the goal being disarmed, Agnes nudges it to keep going. Use this to stay on "
        + "a long task across stalls; disarm it as soon as the goal is met or you are genuinely blocked. "
        + "Supersedes any goal already armed on the session.")]
    public async Task<SessionGoal> ArmGoal(
        [Description("What must be achieved. Written so a future turn can act on it without other context.")] string goal,
        [Description("Nudge only after the session has been completely idle this long. Minimum 30.")] int idleSeconds,
        [Description("How many nudges at most before Agnes gives up. Keep it small.")] int maxProds = 5,
        [Description("Optional: disarm automatically after this many seconds, met or not.")] int? expiresInSeconds = null,
        [Description("The session to arm. Omit to use the calling session.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = await RequireActingSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await _backend.ArmGoalAsync(
            new ArmGoalRequest(target, goal, idleSeconds, maxProds, expiresInSeconds), cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "disarm_goal")]
    [Description("Stop a goal from nudging, recording why. Call this the moment the goal is met, or when "
        + "you are blocked in a way further attempts cannot fix — leaving it armed wastes turns.")]
    public async Task<SessionGoal> DisarmGoal(
        [Description("The goal id returned by arm_goal or list_goals.")] string goalId,
        [Description("Why it is being disarmed, e.g. 'completed' or 'blocked: needs credentials'.")] string reason,
        CancellationToken cancellationToken = default)
    {
        // An agent may only disarm a goal on its own session; a device may disarm any.
        if (CallerSession is { } own)
        {
            var mine = await _backend.ListGoalsAsync(own, cancellationToken).ConfigureAwait(false);
            if (!mine.Any(g => string.Equals(g.Id, goalId, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Unknown goal '{goalId}'.", nameof(goalId));
            }
        }
        else
        {
            // A device may disarm any goal it could have armed: the check is on the goal's OWN session, which
            // the goal id has to be resolved to first — there is no session argument to check against.
            RequireCaller();
            var all = await _backend.ListGoalsAsync(null, cancellationToken).ConfigureAwait(false);
            var goal = all.FirstOrDefault(g => string.Equals(g.Id, goalId, StringComparison.Ordinal))
                ?? throw new ArgumentException($"Unknown goal '{goalId}'.", nameof(goalId));
            await RequireSessionAsync(goal.SessionId, SessionAccessKind.Prompt, cancellationToken).ConfigureAwait(false);
        }

        return await _backend.DisarmGoalAsync(goalId, reason, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unknown goal '{goalId}'.", nameof(goalId));
    }

    [McpServerTool(Name = "list_goals", ReadOnly = true)]
    [Description("List goals and their state (armed, how many nudges used, why a finished one stopped).")]
    public async Task<IReadOnlyList<SessionGoal>> ListGoals(
        [Description("Limit to one session. Omit for the calling session; pass 'all' for every session.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (CallerSession is { } own)
        {
            return await _backend.ListGoalsAsync(own, cancellationToken).ConfigureAwait(false); // never other sessions
        }

        var target = string.Equals(sessionId, "all", StringComparison.OrdinalIgnoreCase) ? null : sessionId;
        if (target is { Length: > 0 })
        {
            await RequireSessionAsync(target, SessionAccessKind.Subscribe, cancellationToken).ConfigureAwait(false);
            return await _backend.ListGoalsAsync(target, cancellationToken).ConfigureAwait(false);
        }

        // "all" is a catalogue read, so it filters rather than refuses — same rule as list_sessions.
        RequireCaller();
        var goals = await _backend.ListGoalsAsync(null, cancellationToken).ConfigureAwait(false);
        var visible = new List<SessionGoal>(goals.Count);
        foreach (var goal in goals)
        {
            if (await CanSeeAsync(goal.SessionId, cancellationToken).ConfigureAwait(false))
            {
                visible.Add(goal);
            }
        }

        return visible;
    }


    [McpServerTool(Name = "send_user_file")]
    [Description("Send a file to the user to look at — a screenshot, a report, a diagram, a built artifact. "
        + "Use it for deliverables the person would want in front of them rather than merely mentioned; "
        + "do not send routine working files.")]
    public async Task<string> SendUserFile(
        [Description("Absolute path, or a path relative to the working directory, or the /work/… path as seen inside a sandbox")] string path,
        [Description("One line of context shown with the file, e.g. 'before vs after'")] string? caption = null,
        [Description("Omit when called by the agent itself; required with a device token")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = await RequireActingSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var shared = await _backend.ShareFileAsync(target, path, caption, cancellationToken).ConfigureAwait(false);
        return $"Sent {shared.FileName} ({DescribeSize(shared.Size)}) to the user.";
    }

    [McpServerTool(Name = "report_status")]
    [Description("Report your one-line status: what you found, what you are doing now, and how it fits the "
        + "plan. One or two sentences, up to " + Sessions.StatusOptions.DefaultMaxCharsText + " characters; "
        + "the first line only. Call it when you start a new piece of work, when you hit a problem, and about "
        + "every few minutes during long work — not every step.")]
    public async Task<string> ReportStatus(
        [Description("Your status: one or two sentences, up to " + Sessions.StatusOptions.DefaultMaxCharsText
            + " characters, on a single line. A longer report is kept up to the limit rather than refused, "
            + "and the reply tells you what was kept.")] string status,
        [Description("Omit when called by the agent itself; required with a device token")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = await RequireActingSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var recorded = await _backend.ReportStatusAsync(target, status, cancellationToken).ConfigureAwait(false);

        // The acknowledgement is where truncation stops being silent. A model that is told "noted" after
        // having half its sentence thrown away learns nothing and writes the same paragraph next time.
        return Sessions.AgentStatusText.Acknowledge(recorded);
    }

    /// <summary>A human-sized rendering of a byte count for the confirmation the model reads back. Rounded on
    /// purpose: the agent is being told the send worked, not being handed a figure to compute with.</summary>
    private static string DescribeSize(long bytes)
    {
        const long Kb = 1024;
        const long Mb = Kb * 1024;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return bytes switch
        {
            < Kb => string.Create(culture, $"{bytes} bytes"),
            < Mb => string.Create(culture, $"{Math.Round(bytes / (double)Kb)} KB"),
            _ => string.Create(culture, $"{bytes / (double)Mb:0.#} MB"),
        };
    }

    [McpServerTool(Name = "read_session_transcript", ReadOnly = true)]
    [Description("Read a privacy-filtered transcript of a session. By default raw tool-call arguments and file contents/paths are excluded; set forwardRawContext to true only if the user has explicitly opted in to sharing them with this endpoint.")]
    public async Task<McpTranscript> ReadSessionTranscript(
        [Description("The session id to read.")] string sessionId,
        [Description("Opt in to include raw tool-call arguments and file paths/contents. Defaults to false (conservative privacy).")] bool forwardRawContext = false,
        CancellationToken cancellationToken = default)
    {
        await RequireSessionAsync(sessionId, SessionAccessKind.Subscribe, cancellationToken).ConfigureAwait(false);
        return await _backend.ReadSessionTranscriptAsync(sessionId, forwardRawContext, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The result of a state-changing MCP tool.</summary>
public sealed record McpActionResult(bool Ok, string Message)
{
    public static McpActionResult Success(string message) => new(true, message);
}

/// <summary>An open permission request as an MCP client sees it (a clean projection of the host's
/// <c>OpenApproval</c>).</summary>
public sealed record McpOpenApproval(string? SessionId, string RequestId, string Title, string Kind, DateTimeOffset RequestedAt);
