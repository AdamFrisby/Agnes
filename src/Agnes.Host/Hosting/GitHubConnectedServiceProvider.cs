using Agnes.Abstractions;
using Agnes.Sandbox.Credentials;

namespace Agnes.Host.Hosting;

/// <summary>
/// The first REAL <see cref="IConnectedServiceProvider"/> (.ideas/providers/02): materialises a short-lived
/// GitHub credential for a named <see cref="ConnectedServiceProfile"/>, reusing the SAME host-side GitHub
/// credential machinery the sandbox git path already uses — it adds NO new GitHub OAuth flow of its own.
/// </summary>
/// <remarks>
/// <para>Resolution order mirrors the sandbox git broker's "prefer the short-lived, least-privilege token":</para>
/// <list type="number">
/// <item>A linked <b>GitHub App</b> (<see cref="GitHubAppCredentialSource"/>) mints a short-lived installation
/// access token with its real expiry — the App private key that signs the minting JWT stays inside that
/// source and is NEVER handed back.</item>
/// <item>Else a configured <b>stored token</b> (<see cref="StoredTokenCredentialSource"/> — a fine-grained PAT
/// or OAuth token) is returned as the credential, with whatever expiry it carries (often none).</item>
/// <item>Else it throws (fail-loud) — never a silent unauthenticated resolve that would let a CLI launch
/// masquerade as authenticated.</item>
/// </list>
/// <para>Both sources are injected (as <see cref="ICredentialSource"/> factories) so the provider is fully
/// testable offline — no network, no vendor SDK. Only the resolved token ever crosses the seam: it is placed
/// in <see cref="ResolvedServiceCredential.Value"/> and mirrored into <c>GITHUB_TOKEN</c> so a consuming agent
/// picks it up via env. The App private key and any refresh material are NEVER forwarded.</para>
/// </remarks>
public sealed class GitHubConnectedServiceProvider : IConnectedServiceProvider
{
    /// <summary>The provider id matched against <see cref="ConnectedServiceProfile.ProviderId"/>.</summary>
    public const string ProviderId = "github";

    /// <summary>The env var a consuming agent reads the resolved token from.</summary>
    public const string TokenEnvVar = "GITHUB_TOKEN";

    /// <summary>GitHub is the only host this provider serves credentials for.</summary>
    public const string GitHubHost = "github.com";

    private readonly Func<ICredentialSource?> _appSource;
    private readonly Func<ICredentialSource?> _storedSource;

    /// <param name="appSource">
    /// Supplies the GitHub App installation-token source (<see cref="GitHubAppCredentialSource"/>) when an App
    /// is linked, or null when none is. Evaluated per resolve so an App linked after startup is picked up.
    /// </param>
    /// <param name="storedSource">
    /// Supplies the stored-token source (<see cref="StoredTokenCredentialSource"/>) when a PAT/OAuth token is
    /// configured, or null when none is. Also evaluated per resolve.
    /// </param>
    public GitHubConnectedServiceProvider(
        Func<ICredentialSource?>? appSource = null,
        Func<ICredentialSource?>? storedSource = null)
    {
        _appSource = appSource ?? (static () => null);
        _storedSource = storedSource ?? (static () => null);
    }

    public string Id => ProviderId;

    public string DisplayName => "GitHub";

    public async Task<ResolvedServiceCredential> ResolveAsync(ConnectedServiceProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // AccountLabel selects which linked GitHub account/installation when more than one is possible, and
        // (when it names an "owner/repo") scopes a minted App token to that repo. A single-account setup leaves
        // it blank and the underlying source falls back to its only linked account. It is a routing hint only —
        // never a secret — carried through as the credential request path.
        var repo = string.IsNullOrWhiteSpace(profile.AccountLabel) ? null : profile.AccountLabel.Trim();
        var request = new CredentialRequest("https", GitHubHost, repo, "get");

        // 1) GitHub App installation token — short-lived, minted host-side from the App private key. Only the
        //    resolved token (GitCredential.Password) crosses this seam; the private key never leaves the source.
        if (_appSource() is { } app
            && await app.ResolveAsync(request, ct).ConfigureAwait(false) is { Password.Length: > 0 } appCred)
        {
            return ForToken(appCred.Password, appCred.ExpiresAt);
        }

        // 2) Stored token (a fine-grained PAT / OAuth token) — returned as-is with its known expiry, if any.
        if (_storedSource() is { } stored
            && await stored.ResolveAsync(request, ct).ConfigureAwait(false) is { Password.Length: > 0 } storedCred)
        {
            return ForToken(storedCred.Password, storedCred.ExpiresAt);
        }

        // 3) Fail loud — never a silent, unauthenticated resolve.
        throw new InvalidOperationException(
            $"Connected-service profile '{profile.Id}' (account '{profile.AccountLabel}') cannot resolve a " +
            $"GitHub credential for provider '{Id}': no GitHub App is linked and no stored token is configured " +
            "(or neither could mint a token for that account). Link GitHub or store a token before using it.");
    }

    /// <summary>Wraps the resolved token so ONLY the token leaves the host — never a refresh token or private
    /// key — with the token also mirrored into <c>GITHUB_TOKEN</c> for a consuming agent's environment.</summary>
    private static ResolvedServiceCredential ForToken(string token, DateTimeOffset? expiresAt)
        => new(
            Value: token,
            ExpiresAt: expiresAt,
            Env: new Dictionary<string, string>(StringComparer.Ordinal) { [TokenEnvVar] = token });
}
