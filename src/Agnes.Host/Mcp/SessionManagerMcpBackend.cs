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
    private readonly SessionGoalManager _goals;

    public SessionManagerMcpBackend(SessionManager sessions, TranscriptPrivacyFilter privacy, SessionGoalManager goals)
    {
        _sessions = sessions;
        _privacy = privacy;
        _goals = goals;
    }

    public Task<SessionGoal> ArmGoalAsync(ArmGoalRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(_goals.Arm(request, DateTimeOffset.UtcNow));

    public Task<SessionGoal?> DisarmGoalAsync(string goalId, string reason, CancellationToken cancellationToken = default)
        => Task.FromResult(_goals.Disarm(goalId, string.IsNullOrWhiteSpace(reason) ? "cancelled" : reason));

    public Task<IReadOnlyList<SessionGoal>> ListGoalsAsync(string? sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(sessionId is { Length: > 0 } id ? _goals.ListFor(id) : _goals.List());

    public async Task<IReadOnlyList<McpSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _sessions.ListSessionSummariesAsync(cancellationToken).ConfigureAwait(false);
        return entries
            .Select(e => new McpSessionSummary(e.SessionId, e.AdapterId, e.Title, StatusText(e.State), e.HeadSequence))
            .ToArray();
    }

    /// <summary>The MCP tools' status vocabulary (<c>working</c>/<c>idle</c>/<c>dormant</c>), which is part of
    /// their published contract and so is spelled out here rather than taken from the enum's name.</summary>
    private static string StatusText(SessionRunState state) => state switch
    {
        SessionRunState.Working => "working",
        SessionRunState.Idle => "idle",
        _ => "dormant",
    };

    public async Task<McpSessionStatus?> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var entries = await _sessions.ListSessionSummariesAsync(cancellationToken).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        // The session's current modes come from its live snapshot; the waiting-on-a-human count comes from the
        // summary, which already folds in the same cross-session approvals aggregation the inbox uses — asking
        // for it a second time here would re-scan every live session's log for nothing.
        var snapshot = await _sessions.GetSnapshotAsync(sessionId, sinceSequence: long.MaxValue, cancellationToken).ConfigureAwait(false);
        var modes = snapshot.Session.Modes?.Select(m => m.Id).ToArray() ?? [];

        return new McpSessionStatus(
            entry.SessionId, entry.AdapterId, StatusText(entry.State), entry.CurrentModeId, modes, entry.HeadSequence, entry.OpenApprovals);
    }

    /// <summary>Submits rather than prompts: an MCP caller has no way to know whether a turn is in flight,
    /// and PromptAsync is the unconditional immediate send — used here it starts a second concurrent
    /// session/prompt on the same session, which shows up as two turn ends for one turn. Submitting applies
    /// the session's send policy, so a message arriving mid-turn queues behind it instead.</summary>
    public Task SendPromptAsync(string sessionId, string text, CancellationToken cancellationToken = default)
        => _sessions.SubmitAsync(sessionId, [new TextContent(text)]);

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
