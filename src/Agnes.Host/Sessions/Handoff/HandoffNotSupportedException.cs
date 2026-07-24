namespace Agnes.Host.Sessions.Handoff;

/// <summary>
/// Thrown when a handoff is attempted for a session whose agent adapter reports
/// <see cref="Agnes.Abstractions.HandoffSupport.Unsupported"/> — a clear, typed "not supported for this agent"
/// signal so a client gets a definite answer rather than a timeout, a silent no-op, or a generic failure (AC5).
/// </summary>
public sealed class HandoffNotSupportedException(string message) : InvalidOperationException(message);
