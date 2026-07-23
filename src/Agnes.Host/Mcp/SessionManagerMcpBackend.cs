using Agnes.Abstractions;
using Agnes.Host.Sessions;
using Agnes.Protocol;

namespace Agnes.Host.Mcp;

/// <summary>
/// The real <see cref="IAgnesMcpBackend"/>: every tool routes through the very same
/// <see cref="SessionManager"/> methods a paired client's SignalR hub calls, so an MCP-driven action carries
/// no new authority and is subject to the identical session/permission logic. The privacy filter is applied
/// here (host-side) when assembling a transcript, so raw tool-call args / file paths cannot leave the host
/// unless a caller explicitly opts in.
/// </summary>
public sealed class SessionManagerMcpBackend : IAgnesMcpBackend
{
    private readonly SessionManager _sessions;
    private readonly TranscriptPrivacyFilter _privacy;

    public SessionManagerMcpBackend(SessionManager sessions, TranscriptPrivacyFilter privacy)
    {
        _sessions = sessions;
        _privacy = privacy;
    }

    public async Task<IReadOnlyList<McpSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _sessions.ListSessionSummariesAsync(cancellationToken).ConfigureAwait(false);
        return entries
            .Select(e => new McpSessionSummary(e.SessionId, e.AdapterId, e.Title, e.Status, e.HeadSequence))
            .ToArray();
    }

    public async Task<McpSessionStatus?> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var entries = await _sessions.ListSessionSummariesAsync(cancellationToken).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        // The session's current modes come from its live snapshot; open approvals are counted from the same
        // cross-session aggregation a client's approvals inbox uses.
        var snapshot = await _sessions.GetSnapshotAsync(sessionId, sinceSequence: long.MaxValue, cancellationToken).ConfigureAwait(false);
        var modes = snapshot.Session.Modes?.Select(m => m.Id).ToArray() ?? [];
        var approvals = await _sessions.GetOpenApprovalsAsync(cancellationToken).ConfigureAwait(false);
        var openForSession = approvals.Count(a => string.Equals(a.SessionId, sessionId, StringComparison.Ordinal));

        return new McpSessionStatus(
            entry.SessionId, entry.AdapterId, entry.Status, entry.CurrentModeId, modes, entry.HeadSequence, openForSession);
    }

    public Task SendPromptAsync(string sessionId, string text, CancellationToken cancellationToken = default)
        => _sessions.PromptAsync(sessionId, [new TextContent(text)]);

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId, CancellationToken cancellationToken = default)
        => _sessions.RespondPermissionAsync(sessionId, requestId, optionId);

    public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
        => _sessions.SetModeAsync(sessionId, modeId);

    public Task<IReadOnlyList<OpenApproval>> ListOpenApprovalsAsync(CancellationToken cancellationToken = default)
        => _sessions.GetOpenApprovalsAsync(cancellationToken);

    public async Task<McpTranscript> ReadSessionTranscriptAsync(string sessionId, bool forwardRawContext, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessions.GetSnapshotAsync(sessionId, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        return _privacy.Build(snapshot.Events, forwardRawContext);
    }
}
