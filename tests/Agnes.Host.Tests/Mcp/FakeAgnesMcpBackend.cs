using Agnes.Host.Mcp;
using Agnes.Protocol;

namespace Agnes.Host.Tests.Mcp;

/// <summary>A recording fake of <see cref="IAgnesMcpBackend"/> — lets the tool layer be exercised offline and
/// asserts that a tool reached the backend with the right arguments.</summary>
internal sealed class FakeAgnesMcpBackend : IAgnesMcpBackend
{
    public List<McpSessionSummary> Sessions { get; } = [];
    public List<OpenApproval> Approvals { get; } = [];
    public McpSessionStatus? Status { get; set; }
    public McpTranscript Transcript { get; set; } = new([], false);

    public (string SessionId, string Text)? SentPrompt { get; private set; }
    public (string SessionId, string RequestId, string OptionId)? RespondedPermission { get; private set; }
    public (string SessionId, string ModeId)? SetModeCall { get; private set; }
    public (string SessionId, bool ForwardRawContext)? TranscriptRequest { get; private set; }

    public Task<IReadOnlyList<McpSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<McpSessionSummary>>(Sessions);

    public Task<McpSessionStatus?> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(Status);

    public Task SendPromptAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        SentPrompt = (sessionId, text);
        return Task.CompletedTask;
    }

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId, CancellationToken cancellationToken = default)
    {
        RespondedPermission = (sessionId, requestId, optionId);
        return Task.CompletedTask;
    }

    public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
    {
        SetModeCall = (sessionId, modeId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OpenApproval>> ListOpenApprovalsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OpenApproval>>(Approvals);

    public Task<McpTranscript> ReadSessionTranscriptAsync(string sessionId, bool forwardRawContext, CancellationToken cancellationToken = default)
    {
        TranscriptRequest = (sessionId, forwardRawContext);
        return Task.FromResult(Transcript);
    }

    // ---- goals ----
    public List<ArmGoalRequest> Armed { get; } = [];
    public List<(string GoalId, string Reason)> Disarmed { get; } = [];
    public List<SessionGoal> Goals { get; } = [];

    public Task<SessionGoal> ArmGoalAsync(ArmGoalRequest request, CancellationToken cancellationToken = default)
    {
        Armed.Add(request);
        var goal = new SessionGoal(
            "goal-" + Armed.Count, request.SessionId, request.Goal, request.IdleSeconds,
            request.MaxProds, 0, true, DateTimeOffset.UnixEpoch);
        Goals.Add(goal);
        return Task.FromResult(goal);
    }

    public Task<SessionGoal?> DisarmGoalAsync(string goalId, string reason, CancellationToken cancellationToken = default)
    {
        Disarmed.Add((goalId, reason));
        var existing = Goals.FirstOrDefault(g => g.Id == goalId);
        return Task.FromResult<SessionGoal?>(existing is null ? null : existing with { Armed = false, DisarmedReason = reason });
    }

    public Task<IReadOnlyList<SessionGoal>> ListGoalsAsync(string? sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SessionGoal>>(
            sessionId is { Length: > 0 } id ? [.. Goals.Where(g => g.SessionId == id)] : Goals);
}

/// <summary>A fixed-token caller source for offline tool tests.</summary>
internal sealed class FixedTokenSource : IMcpCallerTokenSource
{
    public FixedTokenSource(string? token) => CurrentToken = token;

    public string? CurrentToken { get; }
}

/// <summary>An authenticator that accepts one known token and resolves it to a fixed caller id.</summary>
internal sealed class FakeMcpAuthenticator : IMcpDeviceAuthenticator
{
    private readonly string _validToken;
    private readonly string _callerId;

    public FakeMcpAuthenticator(string validToken, string callerId = "device-1")
    {
        _validToken = validToken;
        _callerId = callerId;
    }

    public string? ResolveCaller(string? token) => token == _validToken ? _callerId : null;
}
