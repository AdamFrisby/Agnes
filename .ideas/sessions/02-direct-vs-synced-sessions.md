# Direct vs. Synced sessions (browse/import/take-over)

| | |
|---|---|
| **Category** | Sessions |
| **Plugin surface** | Optional capability interface on `IAgentAdapter` |
| **Priority** | P2 |
| **Rough effort** | M |

## Background

Agnes's pitch is running a coding CLI remotely with none of a raw terminal's limits — but that only covers sessions started *through* Agnes. Nothing stops a user from SSHing into the same machine and running `claude` (or another supported CLI) directly, outside Agnes entirely — for a quick one-off, out of habit, or because they were already at that terminal. Those CLIs generally keep their own on-disk conversation logs regardless of who launched them.

That creates a real gap: a session that exists and is progressing, that the user would like to see or continue from a client, but that Agnes has no idea exists because it never started it. Two things are worth supporting here:

1. **Finding out such a session exists at all** — listing sessions on a given machine/working directory that a CLI created on its own.
2. **Doing something useful once you've found one** — at minimum, watching it live without disturbing it; ideally, folding it into Agnes so it becomes a normal, fully-featured Agnes session (multi-client, scrollback-forever, forkable, etc.) going forward.

This matters specifically because Agnes's value (unlimited scrollback, multi-client consistency, all the session features in this backlog) only applies to sessions living in Agnes's own event-sourced log. A session that only exists in the CLI's own log file gets none of that until it's brought in.

## Current state in Agnes

Every Agnes session today is implicitly what this doc calls **Synced**: Agnes is the source of truth, because the only way a session exists is via `IAgentAdapter.StartSessionAsync`, with every update appended into Agnes's own per-session SQLite event log. There is no discovery of, or attachment to, a session that a CLI created on its own outside Agnes — Agnes simply doesn't look.

## Proposed design

Introduce a second ownership model, **Direct**, for a session Agnes did not start: the provider CLI's own on-disk log (on a specific machine) is the source of truth, Agnes reads it live, and the session is unavailable if that machine is offline — an intentional, honestly-represented limitation rather than something Agnes should try to paper over.

```csharp
/// <summary>Optional: an adapter that can discover sessions the underlying CLI created on its
/// own (outside Agnes), and attach to them read-only or take them over.</summary>
public interface IExternalSessionDiscovery
{
    Task<IReadOnlyList<ExternalSessionInfo>> ListExternalSessionsAsync(string workingDirectory, CancellationToken ct = default);

    /// <summary>Opens a live, read-only view backed by the provider's own logs — no Agnes event store yet.</summary>
    Task<IAgentSession> AttachDirectAsync(string externalSessionId, CancellationToken ct = default);

    /// <summary>Promotes a Direct session to Synced: imports its transcript into a new Agnes-owned
    /// event-sourced session and, if the CLI supports it, resumes it under Agnes's control from here on.</summary>
    Task<IAgentSession> TakeOverAsync(string externalSessionId, CancellationToken ct = default);
}

public sealed record ExternalSessionInfo(string Id, string WorkingDirectory, DateTimeOffset LastActivity, bool IsProviderResumable);
```

`SessionManager` treats a Direct-mode session as a distinct, lighter-weight session kind — no event-store append loop, just a live tail of whatever `AttachDirectAsync` streams — until `TakeOverAsync` promotes it, at which point it becomes a normal Agnes-owned session going forward. This is a natural reuse of the `ResumeSessionId` mechanic already in `AgentSessionOptions`: taking over is, mechanically, starting a new Agnes-owned session that resumes the CLI's existing conversation id, plus importing its prior transcript into the new session's event log so scrollback isn't lost at the seam.

Keeping Direct as a distinct, cheaper read-only state before commiting to take-over is a deliberate design choice, not just a faithful carry-over: importing a transcript means parsing that CLI's own on-disk log format and re-emitting it as `SessionEvent`s, which can be lossy for anything Agnes's normalized model doesn't have a slot for. Letting a user watch the live session first, confirm it's the one they want, and only then commit to import avoids surprising, possibly-lossy imports of the wrong session. The read-only view is also cheap to build well — it's a live tail, no new storage — so there's little reason to skip straight to import-or-nothing.

One thing worth being deliberate about: `ListExternalSessionsAsync` should only ever discover sessions belonging to the same OS user Agnes's host process runs as, on that machine's normal file permissions. Agnes isn't introducing a new privilege boundary here — it's exposing an existing local file, which is fine as long as it stays scoped to what the host process could already read.

## Acceptance criteria

- Given a CLI session started directly at a terminal (not via Agnes) on a machine Agnes hosts, `ListExternalSessionsAsync` for that machine/working directory includes it.
- Opening a discovered session in Direct mode shows its live transcript updating in near-real-time as the terminal session progresses, without sending anything to the underlying CLI or otherwise disturbing it.
- Taking over a Direct session produces a normal Agnes-owned session: it appears in session lists like any other, has full scrollback of the prior conversation, and supports the rest of Agnes's session features (forking, read state, etc.) from that point on.
- If the host machine goes offline while a session is open in Direct mode, the client is told clearly that the session is unavailable rather than showing a stale or silently-frozen transcript.
- Taking over a session whose CLI does not support resuming a prior conversation id still succeeds at *importing* the transcript, and is clearly presented to the user as import-only (no further live agent turns possible) rather than silently pretending to be fully synced.
- Listing external sessions never surfaces sessions belonging to a different OS user than the one Agnes's host process runs as.

## Open questions

- Each adapter needs its own knowledge of that CLI's on-disk log format to implement `ListExternalSessionsAsync` — this is real, adapter-specific work per CLI, not something a shared layer can do generically.
- What exactly gets lost in translation when importing a CLI's own log format into Agnes's normalized `SessionEvent` model (tool calls, permission history, etc.)? Worth a spike per adapter before committing to "full-fidelity import" as an acceptance bar.
