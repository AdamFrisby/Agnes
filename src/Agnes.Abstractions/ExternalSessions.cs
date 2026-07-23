namespace Agnes.Abstractions;

/// <summary>
/// A conversation a coding CLI created on its own, <b>outside</b> Agnes (e.g. someone ran <c>claude</c>
/// directly at a terminal), discovered from that CLI's own on-disk logs. This is the "Direct" ownership
/// model from <c>.ideas/sessions/02-direct-vs-synced-sessions.md</c>: the CLI's log is the source of truth,
/// not Agnes's event store. Purely descriptive metadata for a picker — attaching turns it into a live,
/// read-only Agnes session (see <see cref="IExternalSessionSource.AttachExternalSessionAsync"/>).
/// </summary>
/// <param name="ExternalId">An adapter-scoped, opaque handle that <see cref="IExternalSessionSource"/> can
/// later resolve back to this exact conversation (e.g. the path of its on-disk transcript).</param>
/// <param name="AdapterId">The agent adapter that owns/produced this session.</param>
/// <param name="WorkspaceDirectory">The working directory the CLI session ran in.</param>
/// <param name="Preview">A short human-readable preview (typically the first user message).</param>
/// <param name="LastActivity">When the conversation was last written to.</param>
/// <param name="MessageCount">Number of conversation turns/messages recorded so far.</param>
public sealed record ExternalSessionInfo(
    string ExternalId,
    string AdapterId,
    string WorkspaceDirectory,
    string Preview,
    DateTimeOffset LastActivity,
    int MessageCount);

/// <summary>
/// The result of attaching to an external session: a live, read-only <see cref="IAgentSession"/> that tails
/// the CLI's own log (emitting normalized <see cref="SessionEvent"/>s but never sending anything back to the
/// CLI), plus the working directory that session ran in so the host can catalogue the watch.
/// </summary>
public sealed record ExternalSessionAttachment(IAgentSession Session, string WorkspaceDirectory);

/// <summary>
/// Optional capability an <see cref="IAgentAdapter"/> may implement (checked via <c>is IExternalSessionSource</c>,
/// like <see cref="IMcpDiscoveryAdapter"/>) when it can find and read sessions the underlying CLI created on its
/// own, outside Agnes, from that CLI's on-disk logs. Adapter-specific by necessity — every CLI's log format
/// differs, so a reader lives with the agent it's specific to. An adapter that can't read its CLI's logs simply
/// doesn't implement this interface, so nothing is surfaced (graceful). Discovery must be non-throwing (a
/// missing/malformed log yields nothing, never an exception) and scoped to what the host process itself can
/// already read — this introduces no new privilege boundary, only exposes existing local files.
/// </summary>
public interface IExternalSessionSource
{
    /// <summary>The externally-created sessions this CLI has on disk for <paramref name="workspaceDirectory"/>.
    /// Empty when none / the logs are unreadable — never throws.</summary>
    Task<IReadOnlyList<ExternalSessionInfo>> DiscoverAsync(string workspaceDirectory, CancellationToken ct = default);

    /// <summary>
    /// Opens a live, <b>read-only</b> view of the external session identified by <paramref name="externalId"/>
    /// (from <see cref="ExternalSessionInfo.ExternalId"/>): the returned session tails the CLI's own log and
    /// emits normalized <see cref="SessionEvent"/>s as it grows, but must never send input to, or otherwise
    /// disturb, the underlying CLI. This is the "watch" half; folding a watch into a fully-owned writable
    /// session ("adoption") is a separate, deliberately-later step.
    /// </summary>
    Task<ExternalSessionAttachment> AttachExternalSessionAsync(string externalId, CancellationToken ct = default);
}
