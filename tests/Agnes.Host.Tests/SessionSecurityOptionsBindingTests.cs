using Agnes.Host.Sessions;
using Microsoft.Extensions.Configuration;

namespace Agnes.Host.Tests;

/// <summary>
/// Every <c>Agnes:Security:*</c> key reaches the field it names.
/// </summary>
/// <remarks>
/// Written after a live run found <see cref="SessionSecurityOptions.AllowGraphicalSandboxes"/> was never
/// bound: the property existed, <c>appsettings.json</c> defaulted it, two documents named it as "the
/// switch", and <c>SessionManager</c> enforced it — but the composition root built the record without it,
/// so setting it did nothing and graphical sandboxes could not be enabled on any real host at all. Every
/// unit test passed, because they all construct this record by hand. The lesson generalises past that one
/// key, so this asserts the whole section rather than just the one that was broken.
/// </remarks>
public class SessionSecurityOptionsBindingTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void Binds_every_security_key()
    {
        var options = SessionSecurityOptions.FromConfiguration(
            Config(
                ("Agnes:Security:WorkloadTrust", "Untrusted"),
                ("Agnes:Security:AcknowledgeSharedKernelRisk", "true"),
                ("Agnes:Security:AllowedSessionRoots:0", "/srv/work"),
                ("Agnes:Security:AllowedSessionRoots:1", "/srv/other"),
                ("Agnes:Security:RequireSandbox", "true"),
                ("Agnes:Security:RequirePermissionPrompts", "true"),
                ("Agnes:Security:AllowUnsandboxedSkipPermissions", "true"),
                ("Agnes:Security:AllowGraphicalSandboxes", "true"),
                ("Agnes:Security:AllowedHostMcpServers:0", "github"),
                ("Agnes:Security:HostMcpPolicy", "AllowList"),
                ("Agnes:Security:SessionIsolation", "PerUser"),
                ("Agnes:Security:RestrictConfigToOwner", "true"),
                ("Agnes:Security:MaxConcurrentSandboxes", "4"),
                ("Agnes:Security:TranscriptRetentionDays", "30")),
            isDevelopment: false);

        Assert.Equal(WorkloadTrust.Untrusted, options.WorkloadTrust);
        Assert.True(options.AcknowledgeSharedKernelRisk);
        Assert.Equal(["/srv/work", "/srv/other"], options.AllowedSessionRoots);
        Assert.True(options.RequireSandbox);
        Assert.True(options.RequirePermissionPrompts);
        Assert.True(options.AllowUnsandboxedSkipPermissions);
        Assert.True(options.AllowGraphicalSandboxes);
        Assert.Equal(["github"], options.AllowedHostMcpServers);
        Assert.Equal(HostMcpPolicy.AllowList, options.HostMcpPolicy);
        Assert.Equal(SessionIsolation.PerUser, options.SessionIsolation);
        Assert.True(options.RestrictConfigToOwner);
        Assert.Equal(4, options.MaxConcurrentSandboxes);
        Assert.Equal(30, options.TranscriptRetentionDays);
    }

    /// <summary>The switch that was missing, on its own: on when set, and off by default.</summary>
    [Fact]
    public void Graphical_sandboxes_are_off_until_the_operator_turns_them_on()
    {
        Assert.False(SessionSecurityOptions.FromConfiguration(Config(), isDevelopment: false).AllowGraphicalSandboxes);
        Assert.True(SessionSecurityOptions
            .FromConfiguration(Config(("Agnes:Security:AllowGraphicalSandboxes", "true")), isDevelopment: false)
            .AllowGraphicalSandboxes);
    }

    [Fact]
    public void An_empty_section_keeps_the_permissive_defaults()
    {
        var options = SessionSecurityOptions.FromConfiguration(Config(), isDevelopment: false);

        Assert.Empty(options.AllowedSessionRoots);
        Assert.Empty(options.AllowedHostMcpServers);
        Assert.False(options.RequireSandbox);
        Assert.False(options.RequirePermissionPrompts);
        Assert.False(options.AllowUnsandboxedSkipPermissions);
        Assert.False(options.RestrictConfigToOwner);
        Assert.Equal(HostMcpPolicy.Legacy, options.HostMcpPolicy);
        Assert.Equal(SessionIsolation.Shared, options.SessionIsolation);
        Assert.Equal(0, options.MaxConcurrentSandboxes);
        Assert.Equal(0, options.TranscriptRetentionDays);
    }

    /// <summary>Development is the one place the two isolation defaults differ.</summary>
    [Fact]
    public void Development_does_not_enforce_isolation_and_trusts_the_workload()
    {
        var development = SessionSecurityOptions.FromConfiguration(Config(), isDevelopment: true);
        Assert.False(development.EnforceIsolationPolicy);
        Assert.Equal(WorkloadTrust.Trusted, development.WorkloadTrust);

        var production = SessionSecurityOptions.FromConfiguration(Config(), isDevelopment: false);
        Assert.True(production.EnforceIsolationPolicy);
        Assert.Equal(WorkloadTrust.Untrusted, production.WorkloadTrust);
    }
}
