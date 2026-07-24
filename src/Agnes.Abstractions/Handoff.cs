namespace Agnes.Abstractions;

/// <summary>
/// How far an agent kind can move a live session to a <em>different</em> host (session-handoff,
/// see <c>.ideas/connectivity/03-session-handoff.md</c>). Ordered from least to most faithful.
/// </summary>
public enum HandoffSupport
{
    /// <summary>The agent exposes no way to resume its conversation elsewhere — handoff is refused
    /// with a clear, typed error rather than silently no-op'ing (AC5).</summary>
    Unsupported,

    /// <summary>The universal fallback: seed a fresh session on the target host by replaying this
    /// session's own <see cref="SessionEvent"/> log (the same log Agnes keeps for every session).
    /// Works for any agent because Agnes owns the transcript — it does not need the CLI's cooperation.</summary>
    Replay,

    /// <summary>The agent's own protocol exposes a native resume/fork primitive, so the target host
    /// resumes from the CLI's authoritative token — exact by construction rather than reconstructed.</summary>
    NativeFork,
}

/// <summary>
/// An <see cref="IAgentAdapter"/> that can also describe how to resume one of its sessions on a different
/// host. Purely additive: an adapter that does not implement this is treated as
/// <see cref="HandoffSupport.Unsupported"/>. Adapters that keep enough state to resume exactly implement
/// this and report <see cref="HandoffSupport.NativeFork"/>; the universal <see cref="HandoffSupport.Replay"/>
/// path needs no adapter cooperation (the host replays the event log), but an adapter may still declare
/// <see cref="HandoffSupport.Replay"/> to opt in explicitly.
/// </summary>
public interface IHandoffCapableAdapter
{
    /// <summary>Whether — and how faithfully — this agent kind can resume a session on another machine.</summary>
    HandoffSupport Support { get; }

    /// <summary>
    /// Produces a portable resume token capturing what the target host's copy of this adapter needs to pick
    /// the conversation back up (fed to <see cref="AgentSessionOptions.ResumeSessionId"/> there). Only
    /// meaningful for <see cref="HandoffSupport.NativeFork"/>; a <see cref="HandoffSupport.Replay"/> adapter
    /// may return an empty string because the host reconstructs the seed from the event log instead.
    /// </summary>
    Task<string> ExportHandoffStateAsync(IAgentSession session, CancellationToken ct = default);
}
