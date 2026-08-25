using Agnes.Cli;
using Agnes.Client;
using Agnes.Client.Simulation;

namespace Agnes.Cli.Tests;

public sealed class CliAppTests
{
    private const string HostUrl = "sim://demo";

    private static (CliApp App, TestConsole Console, InMemorySessionRegistry Sessions) NewApp(
        IEnumerable<SessionEntry>? sessions = null)
    {
        var hosts = new InMemoryHostRegistry([new HostEntry("laptop", HostUrl, "token")]);
        var sessionRegistry = new InMemorySessionRegistry(sessions);
        var console = new TestConsole();
        var app = new CliApp(new SimulatedConnector(), console, hosts, sessionRegistry, TimeProvider.System);
        return (app, console, sessionRegistry);
    }

    private static string SpawnDir => Path.Combine(Path.GetTempPath(), "agnes-cli-tests");

    [Fact]
    public async Task Spawn_prints_a_session_id_and_records_it()
    {
        var (app, console, sessions) = NewApp();

        var code = await app.RunAsync(["spawn", "--host", "lap", "--path", SpawnDir, "--agent", "claude-code"]);

        Assert.Equal(0, code);
        Assert.Single(console.OutLines);
        var id = console.OutLines[0];
        Assert.StartsWith("sim-", id);
        Assert.Contains(sessions.Sessions, s => s.SessionId == id);
    }

    [Fact]
    public async Task Spawn_with_json_emits_the_id_as_parseable_json()
    {
        var (app, console, _) = NewApp();

        var code = await app.RunAsync(["spawn", "--host", "laptop", "--path", SpawnDir, "--agent", "claude-code", "--json"]);

        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(console.OutLines[0]);
        Assert.StartsWith("sim-", doc.RootElement.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Status_reports_a_fresh_session_as_idle()
    {
        var (app, console, _) = NewApp();
        await app.RunAsync(["spawn", "--host", "laptop", "--path", SpawnDir, "--agent", "claude-code"]);
        var id = console.OutLines[0];
        console.OutLines.Clear();

        var code = await app.RunAsync(["status", id, "--json"]);

        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(console.OutLines[0]);
        Assert.Equal("idle", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Ambiguous_session_prefix_errors_and_lists_candidates()
    {
        var (app, console, _) = NewApp(
        [
            new SessionEntry("sim-0001", HostUrl, "claude-code"),
            new SessionEntry("sim-0002", HostUrl, "claude-code"),
        ]);

        var code = await app.RunAsync(["status", "sim-000"]);

        Assert.Equal(1, code);
        Assert.Contains(console.ErrorLines, l => l.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
            && l.Contains("sim-0001") && l.Contains("sim-0002"));
    }

    [Fact]
    public async Task Unknown_session_prefix_exits_nonzero()
    {
        var (app, console, _) = NewApp();

        var code = await app.RunAsync(["status", "nope"]);

        Assert.Equal(1, code);
        Assert.Contains(console.ErrorLines, l => l.Contains("no known session", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Send_with_wait_streams_the_reply_and_exits_zero()
    {
        var (app, console, _) = NewApp();
        await app.RunAsync(["spawn", "--host", "laptop", "--path", SpawnDir, "--agent", "claude-code"]);
        var id = console.OutLines[0];
        console.OutLines.Clear();

        var code = await app.RunAsync(["send", id, "hello there", "--wait", "--timeout", "20"]);

        Assert.Equal(0, code);
        Assert.Contains("simulated response", console.OutText);
    }

    [Fact]
    public async Task Send_without_wait_accepts_and_returns_immediately()
    {
        var (app, console, _) = NewApp();
        await app.RunAsync(["spawn", "--host", "laptop", "--path", SpawnDir, "--agent", "claude-code"]);
        var id = console.OutLines[0];
        console.OutLines.Clear();
        console.ErrorLines.Clear();

        var code = await app.RunAsync(["send", id, "hello"]);

        Assert.Equal(0, code);
        Assert.Empty(console.OutLines); // no reply text is printed without --wait
        Assert.Contains(console.ErrorLines, l => l.Contains("accepted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Machines_json_lists_the_paired_host()
    {
        var (app, console, _) = NewApp();

        var code = await app.RunAsync(["machines", "--json"]);

        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(console.OutLines[0]);
        Assert.Equal("laptop", doc.RootElement[0].GetProperty("id").GetString());
        Assert.True(doc.RootElement[0].GetProperty("reachable").GetBoolean());
    }

    [Fact]
    public async Task Ambiguous_host_prefix_errors()
    {
        var hosts = new InMemoryHostRegistry(
        [
            new HostEntry("prod-a", HostUrl, "t"),
            new HostEntry("prod-b", HostUrl, "t"),
        ]);
        var console = new TestConsole();
        var app = new CliApp(new SimulatedConnector(), console, hosts, new InMemorySessionRegistry(), TimeProvider.System);

        var code = await app.RunAsync(["spawn", "--host", "prod", "--path", SpawnDir, "--agent", "claude-code"]);

        Assert.Equal(1, code);
        Assert.Contains(console.ErrorLines, l => l.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }

    // ---- pairing with a self-signed host ----

    [Fact]
    public async Task Auth_login_takes_a_pairing_link_and_keeps_its_certificate_fingerprint()
    {
        // The whole `agnes://pair?...` link is a valid --host. Its fingerprint is the only way the CLI can
        // complete a TLS handshake with a self-signed host, so it must reach the pairing call AND be stored:
        // if it were dropped after pairing, every later connect would fail the handshake instead.
        const string pin = "aa11bb22cc33dd44ee55ff6600112233445566778899aabbccddeeff00112233";
        var link = Agnes.Protocol.PairingLink.Build("https://box:5099", grant: "GRANT-1", fingerprint: pin);
        var hosts = new InMemoryHostRegistry();
        (string Url, string Code, string? Pin) seen = default;
        var app = new CliApp(
            new SimulatedConnector(), new TestConsole(), hosts, new InMemorySessionRegistry(), TimeProvider.System,
            pair: (url, code, name, fingerprint, _) =>
            {
                seen = (url, code, fingerprint);
                return Task.FromResult(new Agnes.Protocol.PairResponse("device-1", name, "issued-token"));
            });

        var exit = await app.RunAsync(["auth", "login", "--host", link, "--name", "cli"]);

        Assert.Equal(0, exit);
        Assert.Equal(("https://box:5099", "GRANT-1", pin), seen);
        var stored = Assert.Single(hosts.Hosts);
        Assert.Equal(pin, stored.Fingerprint);
        Assert.Equal("https://box:5099", stored.Url);
    }

    [Fact]
    public async Task A_plain_url_now_pins_what_the_host_presents_and_an_explicit_pin_still_wins()
    {
        // Behaviour change: a plain URL used to pair with NO pin at all, which only worked against a
        // CA-issued host and silently produced an unpinned entry against a self-signed one. It now goes
        // through trust-on-first-use — see TrustOnFirstUseTests for the confirmation and mismatch paths.
        const string pin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string presented = "01f9d48913f5a642cc0b350b182c960adc48348c73828b11f1ad68a43b71bfeb";
        var hosts = new InMemoryHostRegistry();
        string? seenPin = "unset";
        CliApp Build(params string[] input) => new(
            new SimulatedConnector(), new TestConsole(input), hosts, new InMemorySessionRegistry(), TimeProvider.System,
            pair: (_, _, name, fingerprint, _) =>
            {
                seenPin = fingerprint;
                return Task.FromResult(new Agnes.Protocol.PairResponse("device-1", name, "issued-token"));
            },
            probe: (_, _) => Task.FromResult(presented));

        await Build("yes").RunAsync(["auth", "login", "--host", "https://box:5099", "--code", "C", "--name", "a"]);
        Assert.Equal(presented, seenPin);

        await Build().RunAsync(["auth", "login", "--host", "https://box:5099", "--code", "C", "--name", "b", "--fingerprint", pin]);
        Assert.Equal(pin, seenPin);
    }
}
