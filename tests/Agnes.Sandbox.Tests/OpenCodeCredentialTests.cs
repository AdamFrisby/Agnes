using Agnes.Sandbox.Credentials;

namespace Agnes.Sandbox.Tests;

/// <summary>
/// What crosses into a sandbox for OpenCode. The stakes are asymmetric: forwarding too little doesn't fail
/// loudly, it makes OpenCode silently run a model the user didn't pick (only the credential-free providers
/// are reachable without a key), so these pin the shape rather than leaving it to a live run to discover.
/// </summary>
public sealed class OpenCodeCredentialTests
{
    private const string Auth = """{"opencode-go":{"type":"api","key":"sk-test"}}""";
    private const string Account = """{"version":1,"accounts":{},"active":"me"}""";

    [Fact]
    public void Handles_only_the_opencode_adapter()
    {
        var provider = new OpenCodeCredentialProvider(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenCodeCredentialProvider>.Instance);

        Assert.True(provider.Handles("opencode"));
        Assert.False(provider.Handles("claude-code"));
        Assert.False(provider.Handles("claude-code-native"));
        Assert.False(provider.Handles("codex"));
    }

    [Fact]
    public void Auth_travels_as_inline_env_not_a_file()
    {
        // The env route rides the root-owned tmpfs agent-env file, so the key never lands in the guest's
        // filesystem where the agent (or anything it runs) could read it back off disk.
        var credential = OpenCodeCredentialProvider.Build(Auth, accountJson: null, apiKey: null);

        Assert.Equal(Auth, credential.EnvironmentVariables["OPENCODE_AUTH_CONTENT"]);
        Assert.DoesNotContain(credential.Files, f => f.HomeRelativePath.Contains("auth.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Account_travels_as_a_file_because_it_has_no_env_counterpart()
    {
        var credential = OpenCodeCredentialProvider.Build(Auth, Account, apiKey: null);

        var file = Assert.Single(credential.Files);
        Assert.Equal(".local/share/opencode/account.json", file.HomeRelativePath);
        Assert.Equal(Account, file.Contents);
    }

    [Fact]
    public void A_host_exported_api_key_is_forwarded()
    {
        var credential = OpenCodeCredentialProvider.Build(authJson: null, accountJson: null, apiKey: "sk-from-env");

        Assert.Equal("sk-from-env", credential.EnvironmentVariables["OPENCODE_API_KEY"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]          // valid JSON, wrong shape
    public void Absent_or_malformed_credentials_contribute_nothing(string? raw)
    {
        // Forwarding junk would produce a confusing in-guest failure rather than the clean "no credential"
        // state, which the availability probe can then report accurately.
        var credential = OpenCodeCredentialProvider.Build(raw, raw, apiKey: null);

        Assert.Empty(credential.EnvironmentVariables);
        Assert.Empty(credential.Files);
    }

    [Fact]
    public void Nothing_at_all_yields_an_empty_credential()
    {
        var credential = OpenCodeCredentialProvider.Build(null, null, null);

        Assert.Empty(credential.EnvironmentVariables);
        Assert.Empty(credential.Files);
    }
}
