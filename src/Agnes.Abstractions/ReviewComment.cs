namespace Agnes.Abstractions;

/// <summary>
/// A human's review comment anchored to a file + line of a diff, scoped to the <em>project</em> (not the
/// session), so feedback survives past the conversation it was left in. <see cref="LineHash"/> is a stable
/// hash of the anchored line's content at comment time — a mismatch against the current line flags the
/// comment as stale (its anchor has moved or changed) rather than silently misattributing it.
/// </summary>
public sealed record ReviewComment(
    string Id,
    string ProjectId,
    string FilePath,
    int LineNumber,
    string LineHash,
    string Text,
    DateTimeOffset CreatedAt);
