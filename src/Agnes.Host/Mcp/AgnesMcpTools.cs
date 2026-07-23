using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Agnes.Host.Mcp;

/// <summary>
/// Agnes exposed AS an MCP server: each tool is a thin, authorized wrapper over an existing host action
/// (<see cref="IAgnesMcpBackend"/>), so an MCP caller — the OpenAI Realtime voice endpoint, or any MCP
/// client — can drive Agnes without any new server-side authority. Every call first resolves the caller from
/// the request's Agnes device token (the same token a paired client uses); an unauthenticated call is
/// rejected. This is the "Agnes as MCP server" seam; voice is one consumer of it.
/// </summary>
[McpServerToolType]
public sealed class AgnesMcpTools
{
    private readonly IAgnesMcpBackend _backend;
    private readonly IMcpDeviceAuthenticator _authenticator;
    private readonly IMcpCallerTokenSource _tokenSource;

    public AgnesMcpTools(IAgnesMcpBackend backend, IMcpDeviceAuthenticator authenticator, IMcpCallerTokenSource tokenSource)
    {
        _backend = backend;
        _authenticator = authenticator;
        _tokenSource = tokenSource;
    }

    /// <summary>Authenticates the current request and returns the caller id, or throws so the tool call is
    /// rejected. Called at the top of every tool — actions never run for an unauthenticated request.</summary>
    private string RequireCaller()
    {
        var caller = _authenticator.ResolveCaller(_tokenSource.CurrentToken);
        if (caller is null)
        {
            throw new McpUnauthenticatedException("A valid Agnes device token is required to use this MCP endpoint.");
        }

        return caller;
    }

    [McpServerTool(Name = "list_sessions", ReadOnly = true)]
    [Description("List the coding-agent sessions on this Agnes host (id, title, and coarse status: working, idle, or dormant).")]
    public async Task<IReadOnlyList<McpSessionSummary>> ListSessions(CancellationToken cancellationToken = default)
    {
        RequireCaller();
        return await _backend.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_session_status", ReadOnly = true)]
    [Description("Get the current status of one session: its coarse status, current mode, available modes, and how many permission requests are open.")]
    public async Task<McpSessionStatus> GetSessionStatus(
        [Description("The session id to inspect.")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        RequireCaller();
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
        RequireCaller();
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
        RequireCaller();
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
        RequireCaller();
        await _backend.SetModeAsync(sessionId, modeId, cancellationToken).ConfigureAwait(false);
        return McpActionResult.Success($"Session {sessionId} switched to mode {modeId}.");
    }

    [McpServerTool(Name = "list_open_approvals", ReadOnly = true)]
    [Description("List permission requests across all sessions that are still waiting on a human decision.")]
    public async Task<IReadOnlyList<McpOpenApproval>> ListOpenApprovals(CancellationToken cancellationToken = default)
    {
        RequireCaller();
        var approvals = await _backend.ListOpenApprovalsAsync(cancellationToken).ConfigureAwait(false);
        return approvals
            .Select(a => new McpOpenApproval(a.SessionId, a.RequestId, a.Title, a.Kind.ToString(), a.RequestedAt))
            .ToArray();
    }

    [McpServerTool(Name = "read_session_transcript", ReadOnly = true)]
    [Description("Read a privacy-filtered transcript of a session. By default raw tool-call arguments and file contents/paths are excluded; set forwardRawContext to true only if the user has explicitly opted in to sharing them with this endpoint.")]
    public async Task<McpTranscript> ReadSessionTranscript(
        [Description("The session id to read.")] string sessionId,
        [Description("Opt in to include raw tool-call arguments and file paths/contents. Defaults to false (conservative privacy).")] bool forwardRawContext = false,
        CancellationToken cancellationToken = default)
    {
        RequireCaller();
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
