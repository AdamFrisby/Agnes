using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>The Tailscale transport exposes the host on the operator's tailnet via the <c>tailscale</c> CLI
/// (<c>.ideas/connectivity/01-relay-and-tunneling.md</c>). Tests stub the CLI runner — no real tailscale.</summary>
public class TailscaleTransportProviderTests
{
    private const string RunningStatusJson =
        """{"BackendState":"Running","Self":{"DNSName":"myhost.tail1a2b3.ts.net.","TailscaleIPs":["100.101.102.103"]}}""";

    /// <summary>Records every CLI invocation and answers <c>status</c> vs the expose/teardown verbs from canned results.</summary>
    private sealed class StubTailscaleCli : ITailscaleCli
    {
        private readonly TailscaleCommandResult _statusResult;
        private readonly TailscaleCommandResult _commandResult;

        public StubTailscaleCli(TailscaleCommandResult statusResult, TailscaleCommandResult commandResult)
        {
            _statusResult = statusResult;
            _commandResult = commandResult;
        }

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args);
            var result = args.Count > 0 && args[0] == "status" ? _statusResult : _commandResult;
            return Task.FromResult(result);
        }

        public IReadOnlyList<string>? FirstCallStartingWith(string verb, string flagOrArg)
            => Calls.FirstOrDefault(c => c.Count > 0 && c[0] == verb && c.Contains(flagOrArg));
    }

    private static readonly TailscaleCommandResult Ok = new(0, string.Empty, string.Empty);
    private static readonly HostExposureContext Bound = new(["https://0.0.0.0:5001"]);

    [Fact]
    public async Task Expose_default_mode_issues_tailscale_serve_and_returns_the_tailnet_address()
    {
        var cli = new StubTailscaleCli(new TailscaleCommandResult(0, RunningStatusJson, string.Empty), Ok);
        var provider = new TailscaleTransportProvider(cli);

        var endpoint = await provider.ExposeAsync(Bound);

        // serve (tailnet-only), not funnel; proxies the derived hub port; on the default HTTPS port.
        var serve = cli.Calls.Single(c => c[0] == "serve");
        Assert.Contains("--bg", serve);
        Assert.Contains("--https=443", serve);
        Assert.Contains("https+insecure://localhost:5001", serve);
        Assert.DoesNotContain(cli.Calls, c => c[0] == "funnel");

        // Address is the MagicDNS name from the status fixture (trailing dot stripped, default port omitted).
        Assert.Equal(["https://myhost.tail1a2b3.ts.net"], endpoint.ClientAddresses);
        Assert.Equal("tailnet-only", endpoint.DisplayHint);
    }

    [Fact]
    public async Task Funnel_mode_issues_tailscale_funnel()
    {
        var cli = new StubTailscaleCli(new TailscaleCommandResult(0, RunningStatusJson, string.Empty), Ok);
        var provider = new TailscaleTransportProvider(cli, new TailscaleTransportOptions { Funnel = true });

        var endpoint = await provider.ExposeAsync(Bound);

        var funnel = cli.Calls.Single(c => c[0] == "funnel");
        Assert.Contains("--https=443", funnel);
        Assert.Contains("https+insecure://localhost:5001", funnel);
        Assert.DoesNotContain(cli.Calls, c => c[0] == "serve");
        Assert.Equal(["https://myhost.tail1a2b3.ts.net"], endpoint.ClientAddresses);
        Assert.Equal("public via Tailscale Funnel", endpoint.DisplayHint);
    }

    [Fact]
    public async Task Absent_or_not_logged_in_cli_fails_with_a_clear_error_and_no_fallback()
    {
        // Runner returns non-zero (binary missing / errored) → status query fails.
        var cli = new StubTailscaleCli(new TailscaleCommandResult(-1, string.Empty, "tailscale: command not found"), Ok);
        var provider = new TailscaleTransportProvider(cli);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExposeAsync(Bound));
        Assert.Contains("tailscale", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Actionable guidance, and no attempt to expose (no silent fallback to an unintended transport — AC6).
        Assert.Contains("tailscale up", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cli.Calls, c => c[0] is "serve" or "funnel");
    }

    [Fact]
    public async Task Not_connected_backend_state_fails_with_a_clear_error()
    {
        var stopped = new TailscaleCommandResult(0, """{"BackendState":"NeedsLogin","Self":{"DNSName":"x.ts.net."}}""", string.Empty);
        var cli = new StubTailscaleCli(stopped, Ok);
        var provider = new TailscaleTransportProvider(cli);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExposeAsync(Bound));
        Assert.Contains("tailscale up", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cli.Calls, c => c[0] is "serve" or "funnel");
    }

    [Fact]
    public async Task Expose_failure_after_status_throws_actionable_error()
    {
        // status OK, but `tailscale serve` itself fails (e.g. serve not permitted) → clear error, no fake success.
        var cli = new StubTailscaleCli(
            new TailscaleCommandResult(0, RunningStatusJson, string.Empty),
            new TailscaleCommandResult(1, string.Empty, "serve not allowed"));
        var provider = new TailscaleTransportProvider(cli);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExposeAsync(Bound));
        Assert.Contains("serve", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopAsync_issues_the_teardown_command()
    {
        var cli = new StubTailscaleCli(new TailscaleCommandResult(0, RunningStatusJson, string.Empty), Ok);
        var provider = new TailscaleTransportProvider(cli);

        await provider.ExposeAsync(Bound);
        await provider.StopAsync();

        var off = cli.Calls.Last();
        Assert.Equal(["serve", "--https=443", "off"], off);
    }

    [Fact]
    public async Task StopAsync_before_expose_is_a_no_op()
    {
        var cli = new StubTailscaleCli(new TailscaleCommandResult(0, RunningStatusJson, string.Empty), Ok);
        var provider = new TailscaleTransportProvider(cli);

        await provider.StopAsync();

        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task Advertised_address_is_the_magicdns_name_never_a_lan_ip()
    {
        var cli = new StubTailscaleCli(new TailscaleCommandResult(0, RunningStatusJson, string.Empty), Ok);
        var provider = new TailscaleTransportProvider(cli);

        Assert.True(provider.RequiresOutboundOnly); // outbound-only overlay: no inbound port to open.

        var endpoint = await provider.ExposeAsync(Bound);
        var address = Assert.Single(endpoint.ClientAddresses);
        Assert.EndsWith(".ts.net", address, StringComparison.Ordinal);
        // Never advertises the node's tailnet 100.x IP or a LAN address (supports AC5 — a reachable name).
        Assert.DoesNotContain("100.101.102.103", address, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", address, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_metadata_matches_the_transport_contract()
    {
        var provider = new TailscaleTransportProvider(
            new StubTailscaleCli(Ok, Ok), new TailscaleTransportOptions { Funnel = true });

        Assert.Equal("tailscale", provider.Id);
        Assert.True(provider.RequiresOutboundOnly);
        Assert.Equal("Tailscale Funnel", provider.DisplayName);
    }
}
