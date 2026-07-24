using Agnes.Abstractions;
using Agnes.Wrap.Pty;

namespace Agnes.Wrap;

/// <summary>
/// The agent adapter for a locally-wrapped coding CLI (<c>agnes claude</c>): each instance is bound to one
/// invocation's command + passthrough arguments and, on <see cref="StartSessionAsync"/>, spawns that command
/// in a real PTY and returns a <see cref="WrappedCliSession"/>. Registering it with a host is what makes the
/// wrapped terminal a first-class Agnes session — it flows through the exact same open/track/persist path as
/// a host-started session, so nothing bespoke is needed for cataloguing, snapshot/tail, or multi-client sync.
///
/// It declares <see cref="HandoffSupport.Replay"/> so the session can be moved to another host via the
/// connectivity/03 handoff path: Agnes owns the transcript (the terminal log), so the target host reseeds
/// from that log — no cooperation from the underlying CLI is required.
/// </summary>
public sealed class WrappedCliAdapter : IAgentAdapter, IHandoffCapableAdapter
{
    /// <summary>The stable adapter id a wrapped local CLI session is catalogued under.</summary>
    public const string AdapterId = "wrapped-cli";

    private readonly string _command;
    private readonly IReadOnlyList<string> _arguments;
    private readonly IPtySpawner _spawner;

    public WrappedCliAdapter(
        string command,
        IReadOnlyList<string>? arguments = null,
        IPtySpawner? spawner = null,
        string id = AdapterId,
        string? displayName = null)
    {
        _command = command;
        _arguments = arguments ?? [];
        _spawner = spawner ?? new PortaPtySpawner();
        Descriptor = new AgentDescriptor { Id = id, DisplayName = displayName ?? $"Wrapped CLI ({command})" };
    }

    /// <inheritdoc/>
    public AgentDescriptor Descriptor { get; }

    /// <summary>The most recently started session, exposed so the local tee (and tests) can drive its PTY
    /// I/O directly — the host only sees the abstract <see cref="IAgentSession"/>.</summary>
    public WrappedCliSession? LastSession { get; private set; }

    HandoffSupport IHandoffCapableAdapter.Support => HandoffSupport.Replay;

    // Replay reseeds from Agnes's own transcript, so there is no CLI-native resume token to export.
    Task<string> IHandoffCapableAdapter.ExportHandoffStateAsync(IAgentSession session, CancellationToken ct)
        => Task.FromResult(string.Empty);

    /// <inheritdoc/>
    public bool IsAvailable() => AgentCommand.IsOnPath(_command);

    /// <inheritdoc/>
    public async Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        var launch = new PtyLaunch(_command, _arguments, options.WorkingDirectory, options.Environment);
        var pty = await _spawner.SpawnAsync(launch, cancellationToken).ConfigureAwait(false);
        var session = new WrappedCliSession(pty);
        LastSession = session;
        return session;
    }
}
