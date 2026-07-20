namespace Agnes.Sandbox.Credentials;

/// <summary>
/// A credential request as it arrives from a sandboxed agent's git credential helper: the host it
/// wants to authenticate to and (when <c>credential.useHttpPath</c> is on) the repository path, so a
/// source can mint a credential scoped to exactly that repo.
/// </summary>
/// <param name="Protocol">e.g. "https".</param>
/// <param name="Host">e.g. "github.com".</param>
/// <param name="Repo">Normalised "owner/repo" (no ".git", no slashes), or null if git didn't send a path.</param>
/// <param name="Operation">The git credential operation — only "get" reaches the broker.</param>
public sealed record CredentialRequest(string Protocol, string Host, string? Repo, string Operation);

/// <summary>A resolved git credential. For GitHub app tokens <see cref="Username"/> is "x-access-token".</summary>
public sealed record GitCredential(string Username, string Password, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Supplies a git credential for a host — the host-side secret never leaves this process; the broker
/// hands only the resolved (ideally short-lived, repo-scoped) credential to the sandbox at push time.
/// Implementations: a stored fine-grained PAT, or a GitHub App that mints installation tokens.
/// </summary>
public interface ICredentialSource
{
    /// <summary>Whether this source can serve credentials for the given host (e.g. "github.com").</summary>
    bool Handles(string host);

    /// <summary>Resolves (or mints) a credential for the request, or null if it can't/won't.</summary>
    Task<GitCredential?> ResolveAsync(CredentialRequest request, CancellationToken cancellationToken = default);
}
