using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Host.Sessions;
using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class CredentialBrokerTests
{
    private static CredentialSourceRegistry Sources(string token = "secret-token")
    {
        var reg = new CredentialSourceRegistry();
        reg.Set(new StoredTokenCredentialSource("github.com", token));
        return reg;
    }

    [Theory]
    [InlineData("github.com", null, "github.com", "a/b", true)]      // any-repo grant on the host
    [InlineData("github.com", "a/b", "github.com", "a/b", true)]     // exact repo
    [InlineData("github.com", "a/b", "github.com", "a/c", false)]    // wrong repo
    [InlineData("github.com", "a/b", "gitlab.com", "a/b", false)]    // wrong host
    [InlineData("*", null, "example.com", "x/y", true)]              // wildcard host
    [InlineData("github.com", "a/b", "github.com", null, false)]     // scoped, but git sent no path
    public void Grant_covers_only_its_scope(string hostPat, string? repoPat, string host, string? repo, bool expected)
    {
        var grant = new CredentialGrant("s1", hostPat, repoPat, "Trust");
        Assert.Equal(expected, grant.Covers(new CredentialRequest("https", host, repo, "get")));
    }

    [Theory]
    [InlineData("AdamFrisby/Agnes.git", "AdamFrisby/Agnes")]
    [InlineData("/AdamFrisby/Agnes.git", "AdamFrisby/Agnes")]
    [InlineData("owner/repo", "owner/repo")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalise_repo_strips_slashes_and_dot_git(string? path, string? expected)
        => Assert.Equal(expected, CredentialBrokerListener.NormaliseRepo(path));

    [Fact]
    public void Registry_grants_resolve_and_revoke()
    {
        var reg = new CredentialBrokerRegistry();
        var token = reg.Register(new CredentialGrant("s1", "github.com", "a/b", "Ask"));

        Assert.NotNull(reg.Resolve(token));
        Assert.Equal("s1", reg.SessionFor(token));
        Assert.Null(reg.Resolve("bad"));

        reg.Unregister(token);
        Assert.Null(reg.Resolve(token));
    }

    [Fact]
    public async Task Listener_issues_a_credential_for_a_covering_grant()
    {
        var grants = new CredentialBrokerRegistry();
        var token = grants.Register(new CredentialGrant("s1", "github.com", "AdamFrisby/Agnes", "Trust"));
        await using var listener = new CredentialBrokerListener(grants, Sources(), IPAddress.Loopback, 0, "127.0.0.1", NullLogger<CredentialBrokerListener>.Instance);
        listener.Start();

        var reply = await AskAsync(listener.Port, new { token, protocol = "https", host = "github.com", path = "AdamFrisby/Agnes.git" });

        Assert.Equal("x-access-token", reply.GetProperty("username").GetString());
        Assert.Equal("secret-token", reply.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Listener_denies_out_of_scope_repo()
    {
        var grants = new CredentialBrokerRegistry();
        var token = grants.Register(new CredentialGrant("s1", "github.com", "AdamFrisby/Agnes", "Trust"));
        await using var listener = new CredentialBrokerListener(grants, Sources(), IPAddress.Loopback, 0, "127.0.0.1", NullLogger<CredentialBrokerListener>.Instance);
        listener.Start();

        var reply = await AskAsync(listener.Port, new { token, protocol = "https", host = "github.com", path = "someone/else.git" });

        Assert.True(reply.TryGetProperty("error", out _));
        Assert.False(reply.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Listener_respects_the_authorize_gate_and_audits()
    {
        var grants = new CredentialBrokerRegistry();
        var token = grants.Register(new CredentialGrant("s1", "github.com", null, "Ask"));
        var audited = new List<bool>();
        await using var listener = new CredentialBrokerListener(grants, Sources(), IPAddress.Loopback, 0, "127.0.0.1", NullLogger<CredentialBrokerListener>.Instance)
        {
            OnAuthorize = (_, _) => Task.FromResult(false), // user denies
            OnUse = (_, _, allowed) => audited.Add(allowed),
        };
        listener.Start();

        var reply = await AskAsync(listener.Port, new { token, protocol = "https", host = "github.com", path = "a/b.git" });

        Assert.True(reply.TryGetProperty("error", out _));
        Assert.Contains(false, audited); // the denied attempt was recorded
    }

    [Fact]
    public async Task Listener_errors_when_no_source_can_resolve_the_host()
    {
        var grants = new CredentialBrokerRegistry();
        var token = grants.Register(new CredentialGrant("s1", "*", null, "Trust"));
        await using var listener = new CredentialBrokerListener(grants, new CredentialSourceRegistry(), IPAddress.Loopback, 0, "127.0.0.1", NullLogger<CredentialBrokerListener>.Instance);
        listener.Start();

        var reply = await AskAsync(listener.Port, new { token, protocol = "https", host = "github.com", path = "a/b.git" });

        Assert.Equal("no credential", reply.GetProperty("error").GetString());
    }

    [Fact]
    public void Has_source_for_reflects_linked_accounts()
    {
        var grants = new CredentialBrokerRegistry();
        var linked = new CredentialBrokerListener(grants, Sources(), IPAddress.Loopback, 0, "127.0.0.1", NullLogger<CredentialBrokerListener>.Instance);
        var unlinked = new CredentialBrokerListener(grants, new CredentialSourceRegistry(), IPAddress.Loopback, 0, "127.0.0.1", NullLogger<CredentialBrokerListener>.Instance);

        Assert.True(linked.HasSourceFor("github.com"));
        Assert.False(unlinked.HasSourceFor("github.com"));
    }

    [Fact]
    public async Task Broad_grant_serves_any_repo_on_the_host()
    {
        // The ask-once-per-repo model registers a host-wide "*" grant, so cloning ANY private repo the
        // account can access works — the listener issues a credential for whatever repo git names.
        var grants = new CredentialBrokerRegistry();
        var token = grants.Register(new CredentialGrant("s1", "github.com", "*", "Trust"));
        await using var listener = new CredentialBrokerListener(grants, Sources(), IPAddress.Loopback, 0, "127.0.0.1", NullLogger<CredentialBrokerListener>.Instance);
        listener.Start();

        foreach (var repo in new[] { "AdamFrisby/Agnes", "someone/entirely-different" })
        {
            var reply = await AskAsync(listener.Port, new { token, protocol = "https", host = "github.com", path = repo + ".git" });
            Assert.Equal("secret-token", reply.GetProperty("password").GetString());
        }
    }

    [Fact]
    public async Task Consent_is_asked_once_per_repo_and_remembered()
    {
        var cache = new GitConsentCache();
        var asks = new List<string?>();
        Task<bool> Ask(string? repo) { asks.Add(repo); return Task.FromResult(true); }

        // First touch of each repo prompts; subsequent touches of the same repo don't.
        Assert.True(await cache.DecideAsync("s1", "github.com", "a/b", "Ask", () => Ask("a/b")));
        Assert.True(await cache.DecideAsync("s1", "github.com", "a/b", "Ask", () => Ask("a/b")));
        Assert.True(await cache.DecideAsync("s1", "github.com", "c/d", "Ask", () => Ask("c/d")));
        Assert.Equal(new[] { "a/b", "c/d" }, asks); // asked once per distinct repo

        // A denial is remembered too (no re-nagging).
        Assert.False(await cache.DecideAsync("s2", "github.com", "x/y", "Ask", () => Task.FromResult(false)));
        var reAsked = false;
        Assert.False(await cache.DecideAsync("s2", "github.com", "x/y", "Ask", () => { reAsked = true; return Task.FromResult(true); }));
        Assert.False(reAsked);

        // Trust never prompts; Forget makes a re-opened session ask again.
        var trustAsked = false;
        Assert.True(await cache.DecideAsync("s3", "github.com", "a/b", "Trust", () => { trustAsked = true; return Task.FromResult(false); }));
        Assert.False(trustAsked);

        cache.Forget("s1");
        Assert.True(await cache.DecideAsync("s1", "github.com", "a/b", "Ask", () => Ask("a/b")));
        Assert.Equal(new[] { "a/b", "c/d", "a/b" }, asks); // s1/a/b prompts again after Forget
    }

    [Fact]
    public void Git_config_wires_the_helper_and_identity()
    {
        var config = GitCredentialHelper.GitConfig("/home/agnes", "Ada", "ada@example.com");
        Assert.Contains("helper = !python3 /home/agnes/.agnes/git-credential-agnes", config);
        Assert.Contains("useHttpPath = true", config);
        Assert.Contains("name = Ada", config);
        Assert.Contains("email = ada@example.com", config);
    }

    [Fact]
    public async Task Real_python_helper_brokers_a_credential_end_to_end()
    {
        if (!AgentCommand.IsOnPath("python3"))
        {
            return;
        }

        var grants = new CredentialBrokerRegistry();
        var token = grants.Register(new CredentialGrant("s1", "github.com", "AdamFrisby/Agnes", "Trust"));
        await using var listener = new CredentialBrokerListener(grants, Sources(), IPAddress.Loopback, 0, "127.0.0.1", NullLogger<CredentialBrokerListener>.Instance);
        listener.Start();

        var helperPath = Path.Combine(Path.GetTempPath(), $"git-cred-agnes-{Guid.NewGuid():n}.py");
        await File.WriteAllTextAsync(helperPath, GitCredentialHelper.Script);
        try
        {
            var psi = new ProcessStartInfo("python3")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(helperPath);
            psi.ArgumentList.Add("get");
            psi.Environment["AGNES_GIT_HOST"] = "127.0.0.1";
            psi.Environment["AGNES_GIT_PORT"] = listener.Port.ToString();
            psi.Environment["AGNES_GIT_TOKEN"] = token;
            using var helper = Process.Start(psi)!;

            // git feeds the helper the request on stdin, terminated by a blank line.
            await helper.StandardInput.WriteAsync("protocol=https\nhost=github.com\npath=AdamFrisby/Agnes.git\n\n");
            await helper.StandardInput.FlushAsync();
            helper.StandardInput.Close();

            var output = await helper.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("username=x-access-token", output);
            Assert.Contains("password=secret-token", output);
        }
        finally
        {
            File.Delete(helperPath);
        }
    }

    private static async Task<JsonElement> AskAsync(int port, object request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"));

        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (n == 0 || one[0] == (byte)'\n')
            {
                break;
            }

            sb.Append((char)one[0]);
        }

        return JsonSerializer.Deserialize<JsonElement>(sb.ToString());
    }
}
