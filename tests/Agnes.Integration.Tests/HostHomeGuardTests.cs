using System.Runtime.CompilerServices;
using Agnes.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>
/// Arms the guard for this whole test assembly, before any test runs.
/// <para>
/// A module initializer rather than a fixture, because a fixture only covers the classes that ask for it and
/// the whole point here is to cover the class somebody writes next year without reading this file. The
/// runtime runs this before the first type in the assembly is touched, so it is in place however xunit
/// decides to order things.
/// </para>
/// </summary>
internal static class HostHomeGuard
{
    [ModuleInitializer]
    internal static void Arm() => IsolatedHostHome.RefuseDefaultHome();
}

/// <summary>
/// The guard itself: a host that has not been told where its home is must fail to start rather than write
/// into <c>~/.agnes</c>.
/// <para>
/// This is the regression test for a real incident. The integration suite booted the host through
/// <see cref="WebApplicationFactory{TEntryPoint}"/> with no home override, so every run appended live device
/// records to the developer's own registry — a hundred-odd "Pixel 9" and <c>key:my-laptop</c> fixtures — and
/// under the old "the earliest-paired device is the owner" rule the oldest of those fixtures took ownership
/// of the host away from its human. A comment asking people to remember would not have prevented it; a host
/// that refuses to start does.
/// </para>
/// </summary>
public sealed class HostHomeGuardTests
{
    private sealed class UnisolatedFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Deliberately sets everything EXCEPT Agnes:Home — the mistake this guard is here to catch.
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agnes:PairingToken"] = "test-token",
                }));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public void A_host_booted_without_an_isolated_home_refuses_to_start()
    {
        using var factory = new UnisolatedFactory();

        var refused = Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(IsolatedHostHome.HomeKey, refused.Message, StringComparison.Ordinal);
        Assert.Contains(IsolatedHostHome.RefuseDefaultVariable, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guard_is_armed_for_this_assembly()
    {
        // If this ever reads null, every other test in the suite is one forgotten override away from writing
        // into somebody's real host state — so it is asserted rather than assumed.
        Assert.Equal("1", Environment.GetEnvironmentVariable(IsolatedHostHome.RefuseDefaultVariable));
    }
}
