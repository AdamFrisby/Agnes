namespace Agnes.Abstractions;

/// <summary>
/// A named, persistent connected-service profile: a user's decision to link one account of one provider,
/// stored so it can be selected later per session. This is the multi-profile model — the same
/// <see cref="ProviderId"/> can back several profiles (a personal Claude subscription and a work one; a
/// personal API key and a company-issued one), each distinguished by its own <see cref="Id"/> and
/// <see cref="AccountLabel"/> — replacing the older "one account per provider, whatever's in the
/// environment" assumption (see <c>Agnes.Sandbox.Credentials.ClaudeCredentialProvider</c>).
/// </summary>
/// <remarks>
/// A profile is deliberately just an identity/routing record: it carries <em>no</em> secret. The real
/// secret (API key, OAuth refresh token, …) stays host-side in whatever store the matching
/// <see cref="IConnectedServiceProvider"/> owns; a profile only says <em>which</em> provider and
/// <em>which</em> of its accounts, so <see cref="IConnectedServiceProvider.ResolveAsync"/> can materialise
/// a short-lived credential just-in-time. This mirrors the sandbox git-credential broker
/// (<c>ICredentialSource</c>/<c>SandboxCredential</c>): host holds the real secret, only a narrow resolved
/// credential is ever handed out.
/// </remarks>
/// <param name="Id">Stable unique profile id (e.g. a slug or GUID) — the key sessions reference.</param>
/// <param name="ProviderId">The <see cref="IConnectedServiceProvider.Id"/> that resolves this profile (e.g. "github", "linear", "template").</param>
/// <param name="DisplayName">Human-friendly name shown in a picker (e.g. "GitHub").</param>
/// <param name="AccountLabel">Which account within the provider (e.g. "personal", "work") — a label only, never a credential.</param>
public sealed record ConnectedServiceProfile(string Id, string ProviderId, string DisplayName, string AccountLabel);

/// <summary>
/// A short-lived credential materialised just-in-time for a single agent launch. This is the only thing
/// that ever leaves the host-side broker — never the underlying long-lived secret, and in particular
/// <em>never</em> a refresh token (Anthropic's, for one, are single-use, so forwarding one would race the
/// host's own CLI and invalidate one side). Prefer values that carry an <see cref="ExpiresAt"/> so a stale
/// credential fails loudly rather than silently.
/// </summary>
/// <param name="Value">The resolved credential (e.g. an access token or API key) usable by the launched process.</param>
/// <param name="ExpiresAt">When the credential stops being valid, or null if it doesn't expire / is unknown.</param>
/// <param name="Env">Optional extra environment variables to set alongside the credential (e.g. a provider-specific base URL). Never carries a second secret unrelated to this profile.</param>
public sealed record ResolvedServiceCredential(string Value, DateTimeOffset? ExpiresAt, IReadOnlyDictionary<string, string>? Env = null);

/// <summary>
/// A plugin that knows how to resolve one kind of connected-service credential. One implementation per
/// provider/flow (a real GitHub OAuth app, a Linear API key, …); the built-in
/// <c>TemplateConnectedServiceProvider</c> is the reference stub to copy. Registered as an
/// <see cref="IPluginRegistry{T}"/> plugin point, so a new provider is a new implementation — not an edit
/// to the broker or any core routing.
/// </summary>
/// <remarks>
/// The contract is deliberately a pure function of its input: given a <see cref="ConnectedServiceProfile"/>
/// (identity only), return a <see cref="ResolvedServiceCredential"/> (short-lived secret). Where the real
/// OAuth exchange / token store lives is entirely the implementation's business — the host holds the real
/// secret, and only the resolved credential is handed back. Implementations must throw a clear, actionable
/// exception when a credential can't be resolved (expired and unrefreshable, not connected, revoked), never
/// return a silently-empty credential that would let an unauthenticated CLI launch look like success.
/// </remarks>
public interface IConnectedServiceProvider
{
    /// <summary>Stable provider id matched against <see cref="ConnectedServiceProfile.ProviderId"/> (e.g. "github", "linear", "template").</summary>
    string Id { get; }

    /// <summary>Human-friendly provider name for a picker (e.g. "GitHub").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Materialises a short-lived credential for <paramref name="profile"/> just-in-time. Implementations
    /// perform their real token exchange / lookup here and return only the resolved credential — never the
    /// refresh token or any other long-lived secret. Throws when a credential cannot be resolved.
    /// </summary>
    Task<ResolvedServiceCredential> ResolveAsync(ConnectedServiceProfile profile, CancellationToken ct = default);
}
