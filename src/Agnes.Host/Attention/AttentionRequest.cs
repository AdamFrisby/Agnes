namespace Agnes.Host.Attention;

/// <summary>Lifecycle of an external attention request: waiting on a human, answered, or timed out.</summary>
public enum AttentionStatus
{
    Pending,
    Answered,
    Expired,
}

/// <summary>
/// A durable, resumable "please ask a human" request created by an external system (a CI pipeline, a
/// long-running script, an automation node) over the public REST API — the same primitive as an internal
/// permission request, differing only in how the answer is delivered back (an HTTP callback and/or polling
/// rather than an in-session method call). Immutable: every state transition produces a new record.
/// </summary>
/// <param name="Id">Host-assigned opaque id, echoed to the caller and used as the poll key.</param>
/// <param name="Source">Free-text caller label (never an enum of known integrations) — surfaced in the inbox.</param>
/// <param name="Question">The human-facing question.</param>
/// <param name="Options">The answer choices offered to the human.</param>
/// <param name="CallbackUrl">Where the answer is POSTed when recorded; null ⇒ poll-only delivery.</param>
/// <param name="TimeoutSeconds">If set, the request auto-expires this many seconds after <see cref="CreatedAt"/>.</param>
/// <param name="CreatedAt">Host-stamped creation time (from the injected clock).</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Answer">The recorded answer once <see cref="AttentionStatus.Answered"/>; null otherwise.</param>
/// <param name="OwnerCallerId">The authenticated caller (device/token id) that created it — the only caller
/// permitted to read it back. Answering is done by any Agnes human client, not scoped to the owner.</param>
public sealed record AttentionRequest(
    string Id,
    string Source,
    string Question,
    IReadOnlyList<string> Options,
    string? CallbackUrl,
    int? TimeoutSeconds,
    DateTimeOffset CreatedAt,
    AttentionStatus Status,
    string? Answer,
    string OwnerCallerId);
