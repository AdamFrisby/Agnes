using Agnes.Agents.Native;

namespace Agnes.Agents.Native.Tests;

/// <summary>The Claude adapter classifies its own recoverable credential faults (an expired/revoked OAuth
/// token), so the host doesn't pattern-match error text — see IAgentAdapter.IsRecoverableCredentialFault.</summary>
public class ClaudeCredentialFaultTests
{
    [Theory]
    [InlineData("API Error: 401 OAuth access token has been revoked")]
    [InlineData("authentication_error: invalid bearer token")]
    [InlineData("Request failed with 401 (token expired)")]
    public void Recognizes_recoverable_token_faults(string message)
        => Assert.True(ClaudeCodeNative.IsRecoverableCredentialFault(message));

    [Theory]
    [InlineData("I can't help with that request.")]
    [InlineData("rate limit exceeded")]
    [InlineData("500 internal server error")]
    public void Ignores_non_credential_errors(string message)
        => Assert.False(ClaudeCodeNative.IsRecoverableCredentialFault(message));
}
