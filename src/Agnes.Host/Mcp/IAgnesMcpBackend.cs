using Agnes.Protocol;

namespace Agnes.Host.Mcp;

/// <summary>
/// The narrow set of Agnes actions the MCP server exposes as tools, each a thin wrapper over the exact same
/// host paths a paired client drives (<see cref="Sessions.SessionManager"/>). Modelling the surface as an
/// interface (rather than binding the tools straight to <c>SessionManager</c>) keeps the tool layer pure and
/// unit-testable against a fake, and keeps "Agnes as an MCP server" a reusable seam in its own right — the
/// voice path is just one consumer.
/// </summary>
public interface IAgnesMcpBackend
{
    /// <summary>Open/known sessions (id, title, status) — the voice/automation "what can I act on" list.</summary>
    Task<IReadOnlyList<McpSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>A single session's live status, or null when the id is unknown.</summary>
    Task<McpSessionStatus?> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Relays a spoken/typed instruction to the target session via the same path a typed prompt uses.</summary>
    Task SendPromptAsync(string sessionId, string text, CancellationToken cancellationToken = default);

    /// <summary>Answers an outstanding permission request through the real approval path.</summary>
    Task RespondPermissionAsync(string sessionId, string requestId, string optionId, CancellationToken cancellationToken = default);

    /// <summary>Switches the session's mode (Ask / Code / …).</summary>
    Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default);

    /// <summary>Every permission request across sessions still waiting on a human.</summary>
    Task<IReadOnlyList<OpenApproval>> ListOpenApprovalsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A privacy-filtered transcript of a session. With <paramref name="forwardRawContext"/> false (the
    /// default), raw tool-call arguments and file contents/paths are structurally excluded before they can
    /// leave the host — see <see cref="TranscriptPrivacyFilter"/>. Only an explicit opt-in includes them.
    /// </summary>
    Task<McpTranscript> ReadSessionTranscriptAsync(string sessionId, bool forwardRawContext, CancellationToken cancellationToken = default);
}

/// <summary>An open/known session as an MCP client sees it.</summary>
public sealed record McpSessionSummary(string SessionId, string AdapterId, string? Title, string Status, long HeadSequence);

/// <summary>A single session's status snapshot.</summary>
public sealed record McpSessionStatus(
    string SessionId,
    string AdapterId,
    string Status,
    string? CurrentModeId,
    IReadOnlyList<string> AvailableModes,
    long HeadSequence,
    int OpenApprovals);

/// <summary>
/// A privacy-filtered session transcript. <see cref="IncludedRawContext"/> records whether raw tool-call
/// arguments / file paths were allowed in (only on explicit opt-in), so a consumer can never mistake a
/// summarized transcript for a verbatim one.
/// </summary>
public sealed record McpTranscript(IReadOnlyList<string> Lines, bool IncludedRawContext);
