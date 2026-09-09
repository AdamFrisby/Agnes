using System.Reflection;
using System.Runtime.CompilerServices;
using Agnes.Host.Hosting;
using Agnes.Host.Sharing;
using Agnes.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace Agnes.Host.Tests;

/// <summary>
/// The terminal hub methods must refuse a caller who has no write access to the session.
/// <para>
/// This was a real gap: <c>OpenTerminal</c>, <c>WriteTerminal</c>, <c>ResizeTerminal</c> and
/// <c>OpenAgentConsole</c> reached <c>SessionManager</c> with no check at all, so a paired device with no
/// share on a session — and every public-link viewer, who is supposed to be structurally read-only — could
/// spawn a PTY in that session's working directory. Auditing every path that reaches a session for the
/// display channel is what surfaced it.
/// </para>
/// <remarks>
/// The hub takes two dozen collaborators, so it is built uninitialized and given only the three fields the
/// authorization path actually reads. That is deliberate: the test is about the gate, and constructing the
/// whole host to prove a method calls one guard would test the host instead.
/// </remarks>
/// </summary>
public class TerminalAuthorizationTests
{
    private sealed class DenyingDecider : SessionAccessDecider
    {
        public DenyingDecider()
            : base(null!, null!, null!)
        {
        }

        public override SharingCaller CallerFor(string? token) => new("device-a", null, IsOwner: false);

        public override Task<bool> DecideAsync(
            string sessionId, SessionAccessKind kind, SharingCaller caller, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class StubCallerContext : HubCallerContext
    {
        public override string ConnectionId => "conn-1";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features
            => new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort()
        {
        }
    }

    private static AgnesHub BuildHub()
    {
        var hub = (AgnesHub)RuntimeHelpers.GetUninitializedObject(typeof(AgnesHub));
        Set(hub, "_decider", new DenyingDecider());
        Set(hub, "_publicViewers", new PublicViewerTracker());
        hub.Context = new StubCallerContext();
        return hub;
    }

    private static void Set(AgnesHub hub, string field, object value)
        => typeof(AgnesHub).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hub, value);

    [Fact]
    public async Task Opening_a_terminal_needs_write_access()
    {
        var hub = BuildHub();
        var refused = await Assert.ThrowsAsync<HubException>(
            () => hub.OpenTerminal("sess-1", new OpenTerminalRequest("bash", [], null, 80, 24)));
        Assert.Contains("permission", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Writing_to_a_terminal_needs_write_access()
    {
        var hub = BuildHub();
        await Assert.ThrowsAsync<HubException>(() => hub.WriteTerminal("sess-1", "term-1", [1, 2, 3]));
    }

    [Fact]
    public async Task Resizing_a_terminal_needs_write_access()
    {
        var hub = BuildHub();
        await Assert.ThrowsAsync<HubException>(() => hub.ResizeTerminal("sess-1", "term-1", 120, 40));
    }

    [Fact]
    public async Task Opening_the_agent_console_needs_write_access()
    {
        var hub = BuildHub();
        await Assert.ThrowsAsync<HubException>(() => hub.OpenAgentConsole("sess-1", 120, 40));
    }
}
