using Agnes.Host.Hosting;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agnes.Integration.Tests;

/// <summary>
/// Choosing which address a pairing QR points at.
///
/// The host can only guess, and for a QR it guesses wrong in a way nothing else notices: a host bound to
/// loopback is perfectly reachable from the desktop client running beside it and unreachable from the
/// phone holding the camera. So the host offers what it knows and the human picks — and because the
/// grant is minted by the host and redeemed wherever the device reaches it, switching is a local
/// re-encode rather than a new credential.
/// </summary>
public sealed class PairingAddressChoiceTests
{
    private const string Token = "test-token";

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agnes:PairingToken"] = Token,
                }));
            return base.CreateHost(builder);
        }
    }

    /// <summary>Stands in for a host that only ever bound loopback — the case that prompted this.</summary>
    private static Factory LoopbackBoundHost()
    {
        var factory = new Factory();
        using var _ = factory.CreateClient(); // force the host to build
        var reach = factory.Services.GetRequiredService<HostReachability>();
        reach.Endpoint = new TransportEndpoint(["https://127.0.0.1:5099"], "direct");
        reach.BoundAddresses = ["https://127.0.0.1:5099"];
        return factory;
    }

    [Fact]
    public async Task A_grant_carries_the_addresses_the_host_knows_it_answers_on()
    {
        using var factory = LoopbackBoundHost();
        using var http = factory.CreateClient();

        var grant = await Agnes.Client.PairingManagement.MintGrantAsync("http://localhost", Token, null, http);

        Assert.NotNull(grant);
        Assert.NotNull(grant!.Addresses);
        Assert.NotEmpty(grant.Addresses!);

        // Loopback is offered — the desktop beside the host can use it — but never first, because it is
        // the one address that cannot work for the device being paired.
        Assert.Contains(grant.Addresses!, HostAddresses.IsLoopback);
        Assert.False(HostAddresses.IsLoopback(grant.Addresses![0]),
            $"loopback should not lead the list, got: {string.Join(", ", grant.Addresses!)}");
    }

    [Fact]
    public async Task A_loopback_only_host_still_advertises_something_routable()
    {
        using var factory = LoopbackBoundHost();
        using var http = factory.CreateClient();

        var grant = await Agnes.Client.PairingManagement.MintGrantAsync("http://localhost", Token, null, http);

        // The encoded address is what the phone will actually dial, so it must not be 127.0.0.1 just
        // because that is all the transport could resolve.
        var encoded = PairingLink.HostOf(grant!.DeepLink);
        Assert.NotNull(encoded);
        Assert.False(HostAddresses.IsLoopback(encoded!),
            $"a QR encoding loopback can never be scanned successfully, got: {encoded}");
    }

    [Fact]
    public async Task Switching_address_re_encodes_the_same_grant_and_keeps_the_session()
    {
        using var factory = LoopbackBoundHost();
        using var http = factory.CreateClient();

        var vm = new ConnectQrViewModel(
            () => ("http://localhost", Token), () => "sess-7", ImmediateDispatcher.Instance, http);

        await vm.ShowCommand.ExecuteAsync(null);

        Assert.True(vm.IsVisible);
        Assert.NotEmpty(vm.Addresses);
        var grantSecret = ParseGrant(vm.DeepLink);
        Assert.False(string.IsNullOrEmpty(grantSecret));

        var before = vm.Matrix;
        vm.Address = "https://100.101.102.103:5099"; // e.g. what Tailscale hands out

        Assert.Equal("agnes://pair?host=https%3A%2F%2F100.101.102.103%3A5099"
            + $"&grant={grantSecret}&session=sess-7", vm.DeepLink);
        Assert.NotSame(before, vm.Matrix);              // the drawn code actually changed
        Assert.Equal(grantSecret, ParseGrant(vm.DeepLink)); // ...against the same credential
    }

    [Fact]
    public async Task An_address_the_host_never_offered_is_still_allowed()
    {
        // A port-forward or a MagicDNS name is something the host cannot enumerate but the human knows.
        using var factory = LoopbackBoundHost();
        using var http = factory.CreateClient();

        var vm = new ConnectQrViewModel(
            () => ("http://localhost", Token), () => null, ImmediateDispatcher.Instance, http);
        await vm.ShowCommand.ExecuteAsync(null);

        vm.Address = "https://studio.tail1234.ts.net";

        Assert.Equal("https://studio.tail1234.ts.net", PairingLink.HostOf(vm.DeepLink));
        Assert.DoesNotContain("session=", vm.DeepLink, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hiding_forgets_the_addresses_along_with_the_code()
    {
        using var factory = LoopbackBoundHost();
        using var http = factory.CreateClient();

        var vm = new ConnectQrViewModel(
            () => ("http://localhost", Token), () => "sess-7", ImmediateDispatcher.Instance, http);
        await vm.ShowCommand.ExecuteAsync(null);
        await vm.HideCommand.ExecuteAsync(null);

        Assert.Empty(vm.Addresses);
        Assert.Equal(string.Empty, vm.Address);

        // And a stale address can't quietly re-encode a revoked grant.
        vm.Address = "https://192.168.1.20:5099";
        Assert.Equal(string.Empty, vm.DeepLink);
    }

    private static string ParseGrant(string deepLink)
    {
        foreach (var pair in new Uri(deepLink).Query.TrimStart('?').Split('&'))
        {
            var split = pair.Split('=', 2);
            if (split.Length == 2 && split[0] == "grant")
            {
                return split[1];
            }
        }

        return string.Empty;
    }
}
