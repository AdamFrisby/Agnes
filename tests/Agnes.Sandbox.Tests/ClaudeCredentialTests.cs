using System.Text.Json;
using Agnes.Sandbox.Credentials;

namespace Agnes.Sandbox.Tests;

public class ClaudeCredentialTests
{
    [Fact]
    public void Sanitised_bundle_keeps_access_token_and_expiry_but_drops_refresh_token()
    {
        var raw = """
            {"claudeAiOauth":{"accessToken":"sk-access-abc","refreshToken":"sk-refresh-SECRET","expiresAt":1893456000000,"scopes":["a"]}}
            """;

        Assert.True(ClaudeCredentialProvider.TrySanitise(raw, out var bundle));

        using var doc = JsonDocument.Parse(bundle);
        var oauth = doc.RootElement.GetProperty("claudeAiOauth");
        Assert.Equal("sk-access-abc", oauth.GetProperty("accessToken").GetString());
        Assert.Equal(1893456000000, oauth.GetProperty("expiresAt").GetInt64());

        // The single-use refresh token must never enter the sandbox bundle.
        Assert.DoesNotContain("refreshToken", bundle);
        Assert.DoesNotContain("SECRET", bundle);
        Assert.False(oauth.TryGetProperty("scopes", out _));
    }

    [Fact]
    public void Sanitise_also_surfaces_the_raw_access_token_for_the_env_var()
    {
        // The in-VM claude CLI authenticates via CLAUDE_CODE_OAUTH_TOKEN (it ignores a materialised
        // .credentials.json), so the provider must be able to extract the raw token.
        var raw = """
            {"claudeAiOauth":{"accessToken":"sk-ant-oat01-xyz","refreshToken":"secret","expiresAt":1893456000000}}
            """;

        Assert.True(ClaudeCredentialProvider.TrySanitise(raw, out _, out var accessToken));
        Assert.Equal("sk-ant-oat01-xyz", accessToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"claudeAiOauth\":{}}")]
    public void Sanitise_rejects_missing_access_token(string raw)
        => Assert.False(ClaudeCredentialProvider.TrySanitise(raw, out _));
}
