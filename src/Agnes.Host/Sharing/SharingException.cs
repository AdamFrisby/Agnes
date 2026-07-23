namespace Agnes.Host.Sharing;

/// <summary>
/// Raised when a sharing action is refused for a <em>domain</em> reason — a blank recipient, or (the security
/// invariant this feature exists to guarantee) an attempt to attach permission-approval rights to a share that
/// must never have them: a view-only share, or a share on an inactive session. A public link can't even reach
/// this class because its create path has no approval or level parameter at all — the impossibility there is
/// structural in the type, not a runtime check. This is a normal, surfaced outcome, not an infrastructure fault.
/// </summary>
public sealed class SharingException(string message) : Exception(message);
