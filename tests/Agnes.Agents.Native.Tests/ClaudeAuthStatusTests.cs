using System.Globalization;
using Agnes.Agents.Native;

namespace Agnes.Agents.Native.Tests;

/// <summary>The Claude adapter reads its on-disk OAuth credentials file to report machine-local login state
/// (ProviderAuthStatus) for the agent picker — see ClaudeCodeNative.ProbeAuthStatus.</summary>
public class ClaudeAuthStatusTests
{
    private static string CredentialsJson(string accessToken, long? expiresAtMs)
    {
        var expires = expiresAtMs is { } e
            ? $",\"expiresAt\":{e.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        return $"{{\"claudeAiOauth\":{{\"accessToken\":\"{accessToken}\"{expires}}}}}";
    }

    private static string WriteTemp(string contents)
    {
        // Build the path from GetTempPath (PH2080 forbids hardcoded absolute literals).
        var path = Path.Combine(Path.GetTempPath(), $"agnes-claude-creds-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void Logged_in_when_token_present_and_not_expired()
    {
        var future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        var path = WriteTemp(CredentialsJson("valid-token", future));
        try
        {
            var status = ClaudeCodeNative.ProbeAuthStatus(path);

            Assert.True(status.IsLoggedIn);
            Assert.Equal("OAuth", status.Identity);
            Assert.Equal("OAuth", status.Method);
            Assert.Null(status.Issue);
            Assert.True(status.CheckedAt <= DateTimeOffset.UtcNow);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Logged_in_when_token_present_without_an_expiry()
    {
        var path = WriteTemp(CredentialsJson("valid-token", expiresAtMs: null));
        try
        {
            var status = ClaudeCodeNative.ProbeAuthStatus(path);
            Assert.True(status.IsLoggedIn);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Not_logged_in_when_token_expired()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var path = WriteTemp(CredentialsJson("stale-token", past));
        try
        {
            var status = ClaudeCodeNative.ProbeAuthStatus(path);

            Assert.False(status.IsLoggedIn);
            Assert.False(string.IsNullOrWhiteSpace(status.Issue));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Not_logged_in_when_credentials_file_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-claude-missing-{Guid.NewGuid():n}.json");
        Assert.False(File.Exists(path));

        var status = ClaudeCodeNative.ProbeAuthStatus(path);

        Assert.False(status.IsLoggedIn);
        Assert.False(string.IsNullOrWhiteSpace(status.Issue));
    }

    [Fact]
    public void Not_logged_in_when_token_absent()
    {
        var path = WriteTemp("{\"claudeAiOauth\":{}}");
        try
        {
            var status = ClaudeCodeNative.ProbeAuthStatus(path);
            Assert.False(status.IsLoggedIn);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
