using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Cli;

namespace Agnes.Cli.Tests;

/// <summary>
/// Pairing with a host whose certificate we do not yet pin.
///
/// Before this existed a client with no fingerprint could not reach a self-signed host at all: the handshake
/// failed before any request was sent, so pairing could not be attempted and the failure read as "host
/// unreachable". These tests pin the behaviour that replaced it — SSH's: show the fingerprint, require a
/// human yes, and refuse outright when a key we already recorded has changed.
/// </summary>
public sealed class TrustOnFirstUseTests
{
    private const string Pin = "01f9d48913f5a642cc0b350b182c960adc48348c73828b11f1ad68a43b71bfeb";
    private const string Other = "ffffffff13f5a642cc0b350b182c960adc48348c73828b11f1ad68a43b71bfeb";

    private static CliApp App(
        TestConsole console, IHostRegistry hosts, string observed,
        Action<string?>? capturePin = null)
        => new(
            new SimulatedConnector(), console, hosts, new InMemorySessionRegistry(), TimeProvider.System,
            pair: (url, code, name, fingerprint, _) =>
            {
                capturePin?.Invoke(fingerprint);
                return Task.FromResult(new Agnes.Protocol.PairResponse("device-1", name, "issued-token"));
            },
            probe: (_, _) => Task.FromResult(observed));

    [Fact]
    public async Task An_unknown_host_is_shown_and_pairs_once_the_operator_types_yes()
    {
        var console = new TestConsole("yes");
        var hosts = new InMemoryHostRegistry();
        string? pinned = null;
        var app = App(console, hosts, Pin, p => pinned = p);

        var exit = await app.RunAsync(["auth", "login", "--host", "https://box:5081", "--code", "CODE-1"]);

        Assert.Equal(0, exit);
        Assert.Equal(Pin, pinned);                              // pinned what it actually saw
        Assert.Equal(Pin, Assert.Single(hosts.Hosts).Fingerprint);
        Assert.Contains(console.ErrorLines, l => l.Contains("not yet known", StringComparison.Ordinal));
        Assert.Contains(console.ErrorLines, l => l.Contains("SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Anything_other_than_yes_pairs_nothing_and_trusts_nothing()
    {
        var console = new TestConsole("y");   // deliberately not "yes"
        var hosts = new InMemoryHostRegistry();
        var app = App(console, hosts, Pin);

        var exit = await app.RunAsync(["auth", "login", "--host", "https://box:5081", "--code", "CODE-1"]);

        Assert.NotEqual(0, exit);
        Assert.Empty(hosts.Hosts);
    }

    [Fact]
    public async Task A_CHANGED_certificate_is_refused_outright_and_never_prompts()
    {
        // The one case that must not be a question: indistinguishable from interception.
        var console = new TestConsole("yes");
        var hosts = new InMemoryHostRegistry();
        hosts.Upsert(new HostEntry("box", "https://box:5081", "old-token", Pin));
        var app = App(console, hosts, Other);

        var exit = await app.RunAsync(["auth", "login", "--host", "https://box:5081", "--code", "CODE-1"]);

        Assert.NotEqual(0, exit);
        Assert.Contains(console.ErrorLines, l => l.Contains("CHANGED", StringComparison.Ordinal));
        Assert.Equal(Pin, Assert.Single(hosts.Hosts).Fingerprint);   // the old pin is left intact
    }

    [Fact]
    public async Task A_scripted_setup_states_the_fingerprint_and_it_is_checked_not_trusted()
    {
        var console = new TestConsole();            // no interactive answer available
        var hosts = new InMemoryHostRegistry();
        string? pinned = null;
        var app = App(console, hosts, Pin, p => pinned = p);

        var exit = await app.RunAsync(
            ["auth", "login", "--host", "https://box:5081", "--code", "CODE-1", "--accept-fingerprint", Pin]);

        Assert.Equal(0, exit);
        Assert.Equal(Pin, pinned);
    }

    [Fact]
    public async Task A_scripted_setup_whose_stated_fingerprint_is_wrong_pairs_nothing()
    {
        var console = new TestConsole();
        var hosts = new InMemoryHostRegistry();
        var app = App(console, hosts, Other);

        var exit = await app.RunAsync(
            ["auth", "login", "--host", "https://box:5081", "--code", "CODE-1", "--accept-fingerprint", Pin]);

        Assert.NotEqual(0, exit);
        Assert.Empty(hosts.Hosts);
    }

    [Fact]
    public async Task An_explicit_fingerprint_still_wins_and_never_probes()
    {
        // The QR/deep-link path is stronger than first-use and must not be downgraded to a prompt.
        var console = new TestConsole();
        var hosts = new InMemoryHostRegistry();
        var app = new CliApp(
            new SimulatedConnector(), console, hosts, new InMemorySessionRegistry(), TimeProvider.System,
            pair: (url, code, name, fingerprint, _) =>
                Task.FromResult(new Agnes.Protocol.PairResponse("device-1", name, "issued-token")),
            probe: (_, _) => throw new InvalidOperationException("must not probe when a pin was supplied"));

        var exit = await app.RunAsync(
            ["auth", "login", "--host", "https://box:5081", "--code", "CODE-1", "--fingerprint", Pin]);

        Assert.Equal(0, exit);
        Assert.Equal(Pin, Assert.Single(hosts.Hosts).Fingerprint);
    }

    [Fact]
    public void A_fingerprint_is_displayed_in_readable_groups()
    {
        // 64 unbroken hex characters is how a mismatch gets waved through.
        var shown = Agnes.Client.HostFingerprint.ForDisplay(Pin);

        Assert.Contains(' ', shown);
        Assert.Equal(Pin, shown.Replace(" ", "", StringComparison.Ordinal));
    }
}
