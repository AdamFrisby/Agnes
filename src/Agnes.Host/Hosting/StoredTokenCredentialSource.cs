using Agnes.Sandbox.Credentials;

namespace Agnes.Host.Hosting;

/// <summary>
/// A credential source backed by a single stored token (e.g. a fine-grained PAT the user pasted). It's
/// the low-setup fallback: the token is already repo-scoped by whoever created it, so the source just
/// returns it for its host. Prefer <c>GitHubAppCredentialSource</c> where possible — those tokens are
/// minted per-repo and expire in ~1h; a stored PAT is static.
/// </summary>
public sealed class StoredTokenCredentialSource : ICredentialSource
{
    private readonly string _host;
    private readonly string _username;
    private readonly string _token;

    /// <param name="host">The host this token authenticates to, e.g. "github.com".</param>
    /// <param name="token">The token (used as the git password).</param>
    /// <param name="username">The git username; GitHub accepts "x-access-token" with a token password.</param>
    public StoredTokenCredentialSource(string host, string token, string username = "x-access-token")
    {
        _host = host;
        _token = token;
        _username = string.IsNullOrWhiteSpace(username) ? "x-access-token" : username;
    }

    public bool Handles(string host) => string.Equals(host, _host, StringComparison.OrdinalIgnoreCase);

    public Task<GitCredential?> ResolveAsync(CredentialRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult<GitCredential?>(Handles(request.Host) ? new GitCredential(_username, _token) : null);
}
