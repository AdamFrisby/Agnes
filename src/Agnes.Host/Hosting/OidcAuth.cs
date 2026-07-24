using Agnes.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agnes.Host.Hosting;

/// <summary>Host config for OIDC auth (bound from <c>Agnes:Auth:Oidc</c>).</summary>
/// <remarks>
/// This is the offline-verifiable *token-validation* core of OIDC: a client that already holds an
/// id/access token minted by the configured issuer exchanges it for an Agnes device token. The full
/// interactive authorization-code redirect (browser → issuer → code → token) is deferred — see
/// <c>.ideas/security/02-enterprise-auth.md</c>; validating an issuer-signed JWT and minting the same
/// per-device revocable token the other mechanisms produce is the security-critical part.
/// </remarks>
public sealed record OidcOptions
{
    public bool Enabled { get; init; }

    /// <summary>The token issuer (<c>iss</c>). Tokens whose <c>iss</c> differs are rejected.</summary>
    public string? Issuer { get; init; }

    /// <summary>The expected audience (<c>aud</c>). Tokens for another audience are rejected.</summary>
    public string? Audience { get; init; }

    /// <summary>Inline JWKS (RFC 7517 JWK Set) JSON — the issuer's public signing keys. Preferred for
    /// air-gapped/offline config; takes precedence over <see cref="JwksUri"/> when both are set.</summary>
    public string? JwksJson { get; init; }

    /// <summary>URL to fetch the issuer's JWKS from (e.g. a provider's <c>jwks_uri</c>). Used only when
    /// <see cref="JwksJson"/> is empty.</summary>
    public string? JwksUri { get; init; }

    /// <summary>Human-friendly label shown to clients (e.g. "Okta", "Azure AD").</summary>
    public string DisplayName { get; init; } = "OIDC";

    // ---- Interactive authorization-code (PKCE) redirect flow (see .ideas/security/02-enterprise-auth.md).
    // These are only needed for the browser-based "Sign in with <IdP>" flow; the token-validation core
    // (exchange endpoint) works with just Issuer/Audience/JWKS above.

    /// <summary>The OAuth client id registered with the issuer for this Agnes host. Required for the redirect
    /// flow; public (safe to advertise), never a secret.</summary>
    public string? ClientId { get; init; }

    /// <summary>The client secret, for confidential clients. Omitted for public clients, which authenticate
    /// the token exchange with PKCE alone. Never advertised or logged.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>The redirect URI registered with the issuer — the host's own callback
    /// (<c>{baseUrl}/auth/oidc/callback</c>). The issuer sends the authorization code here.</summary>
    public string? RedirectUri { get; init; }

    /// <summary>Space-separated OAuth scopes to request. <c>openid</c> is always required for an id_token;
    /// defaults to a typical <c>openid profile email</c>.</summary>
    public string Scopes { get; init; } = "openid profile email";

    /// <summary>Explicit authorization endpoint, overriding OIDC discovery. Optional — discovery
    /// (<c>{Issuer}/.well-known/openid-configuration</c>) resolves it when unset.</summary>
    public string? AuthorizationEndpoint { get; init; }

    /// <summary>Explicit token endpoint, overriding OIDC discovery. Optional — discovery resolves it when
    /// unset.</summary>
    public string? TokenEndpoint { get; init; }

    /// <summary>Enabled and configured with everything needed to validate a token (fail-closed: an enabled
    /// but incompletely configured issuer is treated as unusable rather than accepting anything).</summary>
    public bool IsUsable => Enabled
        && !string.IsNullOrWhiteSpace(Issuer)
        && !string.IsNullOrWhiteSpace(Audience)
        && (!string.IsNullOrWhiteSpace(JwksJson) || !string.IsNullOrWhiteSpace(JwksUri));

    /// <summary>The interactive authorization-code redirect flow is usable: the validation core is usable
    /// (so the returned id_token can be verified) and a client id + redirect URI are configured. The
    /// authorize/token endpoints resolve via discovery from <see cref="Issuer"/> when not set explicitly, so
    /// they aren't required here. Fail-closed like <see cref="IsUsable"/>.</summary>
    public bool RedirectConfigured => IsUsable
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(RedirectUri);
}

/// <summary>Outcome of validating an OIDC token: accepted (with a subject) or rejected (with a reason).</summary>
public sealed record OidcResult(bool Ok, string? Subject, string? Reason)
{
    public static OidcResult Reject(string reason) => new(false, null, reason);
    public static OidcResult Accept(string subject) => new(true, subject, null);
}

/// <summary>
/// Validates an OIDC-issued JWT (signature against the issuer's JWKS, plus <c>iss</c>/<c>aud</c>/<c>exp</c>)
/// using Microsoft's first-party token stack. A valid token yields a stable subject to record on the
/// minted device token; anything invalid — tampered signature, expired, wrong issuer, wrong audience — is
/// rejected with a clear, distinguishable reason. Pure over its inputs (no ambient state); JWKS material
/// comes from the immutable <see cref="OidcOptions"/> (inline) or is fetched once and cached.
/// </summary>
public sealed class OidcIdentity(OidcOptions options, HttpClient? http = null, ILogger<OidcIdentity>? logger = null)
{
    private readonly object _gate = new();
    private IReadOnlyCollection<SecurityKey>? _cachedKeys;

    public OidcOptions Options { get; } = options;

    public async Task<OidcResult> ValidateAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!Options.IsUsable)
        {
            return OidcResult.Reject("OIDC sign-in is not configured on this host.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return OidcResult.Reject("No token was presented.");
        }

        IReadOnlyCollection<SecurityKey> keys;
        try
        {
            keys = await ResolveSigningKeysAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A JWKS we can't load is a server-side misconfiguration, not a bad credential — fail closed.
            logger?.LogWarning(ex, "OIDC: could not load the issuer's signing keys");
            return OidcResult.Reject("Could not load the issuer's signing keys.");
        }

        if (keys.Count == 0)
        {
            return OidcResult.Reject("The issuer published no usable signing keys.");
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = Options.Issuer,
            ValidAudience = Options.Audience,
            IssuerSigningKeys = keys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        if (!result.IsValid)
        {
            return OidcResult.Reject(DescribeFailure(result.Exception));
        }

        // Prefer a human-meaningful identity for the audit subject, falling back to the opaque `sub`.
        var identity = result.ClaimsIdentity;
        var subject = identity?.FindFirst("preferred_username")?.Value
            ?? identity?.FindFirst("email")?.Value
            ?? identity?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? identity?.FindFirst("sub")?.Value;

        return string.IsNullOrWhiteSpace(subject)
            ? OidcResult.Reject("The token carried no subject claim.")
            : OidcResult.Accept(subject);
    }

    private async Task<IReadOnlyCollection<SecurityKey>> ResolveSigningKeysAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cachedKeys is { Count: > 0 })
            {
                return _cachedKeys;
            }
        }

        string json;
        if (!string.IsNullOrWhiteSpace(Options.JwksJson))
        {
            json = Options.JwksJson;
        }
        else if (http is not null && !string.IsNullOrWhiteSpace(Options.JwksUri))
        {
            json = await http.GetStringAsync(Options.JwksUri, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // No inline JWKS and no way to fetch one — nothing to validate signatures against.
            return [];
        }

        var keys = JsonWebKeySet.Create(json).GetSigningKeys().ToArray();
        lock (_gate)
        {
            _cachedKeys = keys;
        }

        return keys;
    }

    private static string DescribeFailure(Exception? exception) => exception switch
    {
        // The 8.x async validation path reports lifetime failures as SecurityTokenInvalidLifetimeException
        // (expired or not-yet-valid); the legacy SecurityTokenExpiredException is handled too for safety.
        SecurityTokenExpiredException => "The token has expired.",
        SecurityTokenInvalidLifetimeException => "The token has expired or is not yet valid.",
        SecurityTokenInvalidIssuerException => "The token was issued by an untrusted issuer.",
        SecurityTokenInvalidAudienceException => "The token is not intended for this host.",
        SecurityTokenSignatureKeyNotFoundException => "The token was signed by an unknown key.",
        SecurityTokenInvalidSignatureException => "The token's signature is invalid.",
        _ => "The token is invalid.",
    };
}

/// <summary>Built-in: OIDC token exchange, backed by <see cref="OidcIdentity"/>. Advertises the issuer so a
/// client knows which provider to authenticate against (public — never a secret).</summary>
public sealed class OidcAuthMethodProvider(OidcIdentity oidc) : IAuthMethodProvider
{
    public string MethodId => "oidc";
    public string DisplayName => string.IsNullOrWhiteSpace(oidc.Options.DisplayName) ? "OIDC" : oidc.Options.DisplayName;
    public bool IsEnabled => oidc.Options.IsUsable;
    public IReadOnlyDictionary<string, string> ClientMetadata
    {
        get
        {
            if (!oidc.Options.IsUsable)
            {
                return new Dictionary<string, string>();
            }

            var metadata = new Dictionary<string, string>
            {
                ["issuer"] = oidc.Options.Issuer!,
                ["audience"] = oidc.Options.Audience!,
            };

            // When the interactive authorization-code redirect is configured, advertise its start path so a
            // client can offer "Sign in with <IdP>" (open a browser) rather than only the token-exchange path.
            if (oidc.Options.RedirectConfigured)
            {
                metadata["startPath"] = "/auth/oidc/start";
            }

            return metadata;
        }
    }

    // Adding a human-facing device by signing in against the org's IdP — the "add this device" bucket.
    public AuthFlowKind Kind => AuthFlowKind.NewDevice;
}
