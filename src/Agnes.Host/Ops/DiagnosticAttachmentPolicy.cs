namespace Agnes.Host.Ops;

/// <summary>
/// Decides whether a bug report may carry the sensitive host-log diagnostic bundle. Two independent gates
/// must BOTH hold: the host operator has enabled the capability at all (config
/// <c>Agnes:BugReports:AttachDiagnostics</c>, off by default), and the submitting caller is the host
/// owner/operator. Attachment additionally requires that the user opted in for that specific report — see
/// <see cref="ShouldAttach"/>. Pure over its inputs (a flag + an owner predicate injected by DI), so the
/// gating is testable without a live host and never depends on ambient state.
/// </summary>
public sealed class DiagnosticAttachmentPolicy
{
    private readonly bool _enabled;
    private readonly Func<string?, bool> _isOwner;

    public DiagnosticAttachmentPolicy(bool enabled, Func<string?, bool> isOwner)
    {
        _enabled = enabled;
        _isOwner = isOwner;
    }

    /// <summary>Whether the caller is even permitted to attach diagnostics (capability enabled AND owner).
    /// Independent of any per-report opt-in — used to decide whether to offer the control to a client.</summary>
    public bool CanAttach(string? callerId) => _enabled && _isOwner(callerId);

    /// <summary>Whether to attach the bundle to THIS report: the user opted in AND the caller is permitted.
    /// When false, the report's payload stays null exactly as on the default path.</summary>
    public bool ShouldAttach(bool requestedByUser, string? callerId) => requestedByUser && CanAttach(callerId);
}
