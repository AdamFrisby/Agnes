using System.Threading.Channels;
using Agnes.Abstractions;

namespace Agnes.Host.Sessions;

/// <summary>
/// A no-op <see cref="IAgentSession"/> backing a provider-login "scratch" session (platform/03). It never
/// talks to a real agent: the session exists only so the login terminal is a first-class, client-visible
/// session, carrying the login PTY's <see cref="TerminalOutputEvent"/>s to clients through the very same
/// <see cref="HostSession"/> snapshot/tail path as the in-session terminal (and letting keystrokes route back
/// to the PTY). Its own event stream is empty (immediately completed), so the host pump has nothing to relay;
/// the login output is appended out-of-band by the <see cref="SessionManager"/> via
/// <see cref="HostSession.AppendTerminalOutputAsync"/>.
/// </summary>
internal sealed class LoginTerminalSession : IAgentSession
{
    // A bounded channel we complete immediately — the login session produces no agent events of its own.
    private readonly Channel<SessionEvent> _events = Channel.CreateBounded<SessionEvent>(1);

    public LoginTerminalSession(string loginId)
    {
        AgentSessionId = loginId;
        _events.Writer.Complete();
    }

    public string AgentSessionId { get; }

    public ChannelReader<SessionEvent> Events => _events.Reader;

    public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
        => Task.FromResult(StopReason.EndTurn);

    public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
