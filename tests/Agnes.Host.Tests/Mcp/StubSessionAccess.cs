using Agnes.Host.Sharing;

namespace Agnes.Host.Tests.Mcp;

/// <summary>
/// Stands in for the real sharing stack in the MCP tool tests: what these tests care about is that each tool
/// <em>asks</em>, and asks for the right <see cref="SessionAccessKind"/> — not how the sharing layer reaches
/// its answer, which has its own tests. Constructed the same way the display channel's stub is
/// (<c>base(null!, null!, null!)</c>): the base fields are never touched because both entry points are
/// overridden.
/// </summary>
internal sealed class StubSessionAccess : SessionAccessDecider
{
    private readonly Func<string, SessionAccessKind, bool> _decide;

    private StubSessionAccess(Func<string, SessionAccessKind, bool> decide)
        : base(null!, null!, null!)
        => _decide = decide;

    /// <summary>Every session, every verb — the shape of a host owner.</summary>
    public static StubSessionAccess AllowAll() => new((_, _) => true);

    /// <summary>Access to exactly one session, as a Member has to the session it started.</summary>
    public static StubSessionAccess OnlyFor(string sessionId)
        => new((id, _) => string.Equals(id, sessionId, StringComparison.Ordinal));

    /// <summary>Access to one session, but only up to a given verb — a view-only share.</summary>
    public static StubSessionAccess OnlyFor(string sessionId, params SessionAccessKind[] kinds)
        => new((id, kind) => string.Equals(id, sessionId, StringComparison.Ordinal) && kinds.Contains(kind));

    /// <summary>What each tool was asked, in order — so a test can pin the verb, not merely the outcome.</summary>
    public List<(string SessionId, SessionAccessKind Kind)> Asked { get; } = [];

    public override SharingCaller CallerFor(string? token) => new("device-a", null, IsOwner: false);

    public override Task<bool> DecideAsync(
        string sessionId, SessionAccessKind kind, SharingCaller caller, CancellationToken cancellationToken = default)
    {
        Asked.Add((sessionId, kind));
        return Task.FromResult(_decide(sessionId, kind));
    }
}
