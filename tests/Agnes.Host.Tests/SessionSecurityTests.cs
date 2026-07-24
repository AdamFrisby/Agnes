using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Agnes.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class SessionSecurityTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    /// <summary>A provider that must never be invoked — used to prove a refused session never touches a sandbox.</summary>
    private sealed class UnusedSandboxProvider : ISandboxProvider
    {
        public string Name => "unused";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
            => throw new Xunit.Sdk.XunitException("CreateAsync must not be called for a refused session.");
        public Task<IReadOnlyList<SandboxInfo>> ListManagedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInfo>>([]);
        public Task<ISandbox> AttachAsync(string vmName, SandboxSpec spec, bool start, CancellationToken cancellationToken = default)
            => throw new Xunit.Sdk.XunitException("AttachAsync must not be called for a refused session.");
    }

    private static SessionManager Manager(ScriptedAgentAdapter adapter, SessionSecurityOptions security, ISandboxProvider? sandbox = null)
        => new(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance,
            sandbox is null ? null : TestPluginRegistries.Sandboxes(sandbox),
            security: security);

    // ---- pure policy ----

    [Fact]
    public void Empty_allowlist_permits_any_directory()
    {
        Assert.True(SessionDirectoryPolicy.IsWithinAllowedRoots("/anywhere/at/all", []));
        Assert.True(SessionDirectoryPolicy.IsWithinAllowedRoots("/", []));
    }

    [Theory]
    [InlineData("/srv/work", "/srv/work", true)]          // the root itself
    [InlineData("/srv/work", "/srv/work/proj", true)]     // a descendant
    [InlineData("/srv/work", "/srv/work/a/b/c", true)]    // a deep descendant
    [InlineData("/srv/work", "/srv/work-evil", false)]    // sibling sharing a prefix — must NOT match
    [InlineData("/srv/work", "/srv/other", false)]        // unrelated
    [InlineData("/srv/work", "/etc/passwd", false)]       // outside
    [InlineData("/srv/work", "/srv/work/../etc", false)]  // traversal collapses out of the root
    [InlineData("/srv/work/", "/srv/work/proj/", true)]   // trailing separators are irrelevant
    public void Boundary_aware_containment(string root, string candidate, bool expected)
        => Assert.Equal(expected, SessionDirectoryPolicy.IsWithinAllowedRoots(candidate, [root]));

    [Fact]
    public void Multiple_roots_any_match_admits()
    {
        string[] roots = ["/srv/a", "/srv/b"];
        Assert.True(SessionDirectoryPolicy.IsWithinAllowedRoots("/srv/b/proj", roots));
        Assert.False(SessionDirectoryPolicy.IsWithinAllowedRoots("/srv/c/proj", roots));
    }

    [Fact]
    public void Null_or_blank_candidate_is_rejected_when_restricted()
    {
        Assert.False(SessionDirectoryPolicy.IsWithinAllowedRoots(null, ["/srv/work"]));
        Assert.False(SessionDirectoryPolicy.IsWithinAllowedRoots("   ", ["/srv/work"]));
    }

    // ---- allowlist enforcement through the open path ----

    [Fact]
    public async Task Open_inside_an_allowed_root_succeeds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agnes-allow-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);
        try
        {
            var adapter = new ScriptedAgentAdapter();
            await using var manager = Manager(adapter, new SessionSecurityOptions { AllowedSessionRoots = [root] });

            await manager.OpenSessionAsync("scripted", Path.Combine(root, "proj"));

            Assert.NotNull(adapter.LastOptions); // the agent actually launched
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Open_outside_every_allowed_root_is_refused_and_launches_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agnes-allow-{Guid.NewGuid():n}");
        var adapter = new ScriptedAgentAdapter();
        await using var manager = Manager(adapter, new SessionSecurityOptions { AllowedSessionRoots = [root] });

        var ex = await Assert.ThrowsAsync<SessionSecurityException>(
            () => manager.OpenSessionAsync("scripted", "/etc"));

        Assert.Contains("allowed session roots", ex.Message);
        Assert.Null(adapter.LastOptions); // never reached the adapter
    }

    // ---- require-sandbox enforcement ----

    [Fact]
    public async Task Require_sandbox_with_no_provider_refuses_every_session()
    {
        var adapter = new ScriptedAgentAdapter();
        await using var manager = Manager(adapter, new SessionSecurityOptions { RequireSandbox = true });

        var ex = await Assert.ThrowsAsync<SessionSecurityException>(
            () => manager.OpenSessionAsync("scripted", "/tmp/work"));

        Assert.Contains("no sandbox provider is configured", ex.Message);
        Assert.Null(adapter.LastOptions);
    }

    [Fact]
    public async Task Require_sandbox_refuses_a_session_that_opts_out()
    {
        var adapter = new ScriptedAgentAdapter();
        // A provider IS configured, but the request asks for useSandbox: false — still refused, and the
        // provider is never touched.
        await using var manager = Manager(adapter, new SessionSecurityOptions { RequireSandbox = true }, new UnusedSandboxProvider());

        var ex = await Assert.ThrowsAsync<SessionSecurityException>(
            () => manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false));

        Assert.Contains("requires every session to run in a sandbox", ex.Message);
        Assert.Null(adapter.LastOptions);
    }

    [Fact]
    public async Task SandboxRequired_is_surfaced_from_options()
    {
        await using var required = Manager(new ScriptedAgentAdapter(), new SessionSecurityOptions { RequireSandbox = true });
        await using var optional = Manager(new ScriptedAgentAdapter(), new SessionSecurityOptions());

        Assert.True(required.SandboxRequired);
        Assert.False(optional.SandboxRequired);
    }
}
