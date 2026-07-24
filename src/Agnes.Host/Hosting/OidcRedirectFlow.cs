using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agnes.Host.Hosting;

/// <summary>
/// The server-side state stashed for one in-flight authorization-code flow, keyed by the opaque
/// <c>state</c>. The <see cref="CodeVerifier"/> (PKCE) and <see cref="Nonce"/> never leave the host — they
/// tie the browser round-trip back to a request this host started, defeating code-injection/CSRF and
/// id_token replay respectively. Expires after a short TTL.
/// </summary>
public sealed record OidcPendingAuth(string CodeVerifier, string Nonce, string? DeviceName, DateTimeOffset ExpiresAt);

/// <summary>
/// Holds the short-lived PKCE/nonce state for in-flight OIDC redirect flows, keyed by <c>state</c>. A flow's
/// state is consumed exactly once at the callback (take-and-remove), so a replayed callback finds nothing.
/// Injected so the flow doesn't own ambient state and an alternative store (e.g. distributed) can be dropped
/// in for a multi-instance host.
/// </summary>
public interface IOidcStateStore
{
    /// <summary>Stashes the pending flow under its state key.</summary>
    void Put(string state, OidcPendingAuth pending);

    /// <summary>Atomically removes and returns the pending flow for a state, or null if unknown/already used.</summary>
    OidcPendingAuth? Take(string state);
}

/// <summary>Default in-memory <see cref="IOidcStateStore"/>. Prunes expired entries opportunistically using
/// the injected clock, so an abandoned flow doesn't leak. Fine for a single-instance host; a multi-instance
/// deployment behind a load balancer would swap in a shared store.</summary>
public sealed class InMemoryOidcStateStore(TimeProvider clock) : IOidcStateStore
{
    private readonly ConcurrentDictionary<string, OidcPendingAuth> _pending = new(StringComparer.Ordinal);

    public void Put(string state, OidcPendingAuth pending)
    {
        Prune();
        _pending[state] = pending;
    }

    public OidcPendingAuth? Take(string state)
    {
        Prune();
        return _pending.TryRemove(state, out var pending) ? pending : null;
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, value) in _pending)
        {
            if (value.ExpiresAt < now)
            {
                _pending.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>Outcome of the callback leg: a successfully-minted device token, or a rejection with a reason.</summary>
public sealed record OidcCallbackResult(bool Ok, PairResponse? Pairing, string? Reason)
{
    public static OidcCallbackResult Reject(string reason) => new(false, null, reason);
    public static OidcCallbackResult Accept(PairResponse pairing) => new(true, pairing, null);
}

/// <summary>
/// The interactive OIDC authorization-code + PKCE redirect flow — the piece that <em>obtains</em> the token
/// the <see cref="OidcIdentity"/> validation core consumes. <see cref="StartAsync"/> generates a PKCE
/// verifier/challenge + CSRF <c>state</c> + replay <c>nonce</c>, stashes them server-side, and returns the
/// issuer's authorization URL for the client to open in a browser. <see cref="HandleCallbackAsync"/>
/// validates the returned <c>state</c> (CSRF), exchanges the code at the token endpoint (with the PKCE
/// verifier + optional client secret), then <em>reuses</em> <see cref="OidcIdentity.ValidateAsync"/> to
/// verify the id_token's signature/iss/aud/exp, checks the <c>nonce</c> matches, and mints the same
/// per-device token every other bootstrap method produces via <see cref="DeviceRegistry.IssueDeviceToken"/>.
/// Endpoints (authorize/token/jwks) resolve via OIDC discovery, overridable by explicit config. All crypto
/// is BCL (<see cref="RandomNumberGenerator"/>, <see cref="SHA256"/>) + the existing JWT validator; codes,
/// verifiers and tokens are never logged.
/// </summary>
public sealed class OidcRedirectFlow
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly OidcIdentity _oidc;
    private readonly DeviceRegistry _tokens;
    private readonly HttpClient _http;
    private readonly IOidcStateStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<OidcRedirectFlow>? _logger;

    private readonly object _gate = new();
    private OidcEndpoints? _cachedEndpoints;

    public OidcRedirectFlow(
        OidcIdentity oidc,
        DeviceRegistry tokens,
        HttpClient http,
        IOidcStateStore store,
        TimeProvider clock,
        ILogger<OidcRedirectFlow>? logger = null)
    {
        _oidc = oidc;
        _tokens = tokens;
        _http = http;
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Whether the interactive redirect flow is configured (client id + redirect URI + a usable
    /// validation core). When false, the start/callback endpoints fail closed.</summary>
    public bool IsConfigured => _oidc.Options.RedirectConfigured;

    /// <summary>
    /// Begins a sign-in: generates PKCE + state + nonce, stashes them (short TTL), resolves the issuer's
    /// authorization endpoint (discovery or explicit config), and returns the URL the client opens in a
    /// browser. Null when the flow isn't configured or the endpoint can't be resolved.
    /// </summary>
    public async Task<OidcAuthStart?> StartAsync(string? deviceName, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        OidcEndpoints endpoints;
        try
        {
            endpoints = await ResolveEndpointsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OIDC: could not resolve the issuer's endpoints for sign-in.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(endpoints.AuthorizationEndpoint))
        {
            _logger?.LogWarning("OIDC: the issuer advertised no authorization endpoint.");
            return null;
        }

        // PKCE (RFC 7636): a high-entropy verifier kept server-side; only its SHA-256 challenge leaves the host.
        var codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));

        _store.Put(state, new OidcPendingAuth(codeVerifier, nonce, deviceName, _clock.GetUtcNow() + StateTtl));

        var options = _oidc.Options;
        var query = new (string Key, string? Value)[]
        {
            ("response_type", "code"),
            ("client_id", options.ClientId),
            ("redirect_uri", options.RedirectUri),
            ("scope", options.Scopes),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
            ("state", state),
            ("nonce", nonce),
        };

        var url = BuildUrl(endpoints.AuthorizationEndpoint, query);
        return new OidcAuthStart(url, state);
    }

    /// <summary>
    /// Completes a sign-in from the issuer's redirect: validates <c>state</c> (unknown/expired → CSRF
    /// reject), exchanges the code for an id_token (PKCE verifier + optional secret), validates the token via
    /// the shared <see cref="OidcIdentity"/> core, verifies the <c>nonce</c> (replay defence), then mints a
    /// device token. Never logs the code, verifier or tokens.
    /// </summary>
    public async Task<OidcCallbackResult> HandleCallbackAsync(string? code, string? state, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return OidcCallbackResult.Reject("OIDC sign-in is not enabled on this host.");
        }

        if (string.IsNullOrWhiteSpace(state))
        {
            return OidcCallbackResult.Reject("The sign-in response carried no state.");
        }

        // Take-and-remove: an unknown, already-used, or expired state is rejected (CSRF / replay defence).
        var pending = _store.Take(state);
        if (pending is null)
        {
            return OidcCallbackResult.Reject("Unknown or expired sign-in request.");
        }

        if (pending.ExpiresAt < _clock.GetUtcNow())
        {
            return OidcCallbackResult.Reject("The sign-in request has expired. Please try again.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return OidcCallbackResult.Reject("The issuer returned no authorization code.");
        }

        OidcEndpoints endpoints;
        try
        {
            endpoints = await ResolveEndpointsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OIDC: could not resolve the issuer's token endpoint.");
            return OidcCallbackResult.Reject("Could not reach the issuer to complete sign-in.");
        }

        if (string.IsNullOrWhiteSpace(endpoints.TokenEndpoint))
        {
            return OidcCallbackResult.Reject("The issuer advertised no token endpoint.");
        }

        var idToken = await ExchangeCodeAsync(endpoints.TokenEndpoint, code, pending.CodeVerifier, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return OidcCallbackResult.Reject("The issuer did not return an id_token for this code.");
        }

        // Reuse the validation core: signature against the JWKS + iss/aud/exp. No duplicate crypto.
        var validated = await _oidc.ValidateAsync(idToken, cancellationToken).ConfigureAwait(false);
        if (!validated.Ok || validated.Subject is null)
        {
            return OidcCallbackResult.Reject(validated.Reason ?? "The id_token is invalid.");
        }

        // Bind the token to the request we started: the nonce must match the one we stashed (replay defence).
        if (!NonceMatches(idToken, pending.Nonce))
        {
            return OidcCallbackResult.Reject("The id_token's nonce did not match the sign-in request.");
        }

        var minted = _tokens.IssueDeviceToken(pending.DeviceName, subject: "oidc:" + validated.Subject, kind: "oidc");
        _logger?.LogInformation("OIDC redirect sign-in completed for {Subject}.", validated.Subject);
        return OidcCallbackResult.Accept(new PairResponse(minted.DeviceId, minted.DeviceName, minted.Token));
    }

    private async Task<string?> ExchangeCodeAsync(string tokenEndpoint, string code, string codeVerifier, CancellationToken cancellationToken)
    {
        var options = _oidc.Options;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = options.RedirectUri!,
            ["client_id"] = options.ClientId!,
            ["code_verifier"] = codeVerifier,
        };
        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            form["client_secret"] = options.ClientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Don't log the body — it can echo the code. Status alone is enough to diagnose.
            _logger?.LogWarning("OIDC: token exchange failed ({Status}).", response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        OidcTokenResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OidcTokenResponse>(body, Json);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "OIDC: token endpoint returned a response we couldn't parse.");
            return null;
        }

        return parsed?.IdToken;
    }

    private async Task<OidcEndpoints> ResolveEndpointsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cachedEndpoints is not null)
            {
                return _cachedEndpoints;
            }
        }

        var options = _oidc.Options;

        // Explicit config wins over discovery for each endpoint; discovery fills any gaps.
        var authorize = options.AuthorizationEndpoint;
        var token = options.TokenEndpoint;

        if (string.IsNullOrWhiteSpace(authorize) || string.IsNullOrWhiteSpace(token))
        {
            var discovered = await DiscoverAsync(options.Issuer!, cancellationToken).ConfigureAwait(false);
            authorize = string.IsNullOrWhiteSpace(authorize) ? discovered?.AuthorizationEndpoint : authorize;
            token = string.IsNullOrWhiteSpace(token) ? discovered?.TokenEndpoint : token;
        }

        var endpoints = new OidcEndpoints(authorize, token);
        lock (_gate)
        {
            _cachedEndpoints = endpoints;
        }

        return endpoints;
    }

    private async Task<OidcDiscoveryDocument?> DiscoverAsync(string issuer, CancellationToken cancellationToken)
    {
        // OIDC discovery: {issuer}/.well-known/openid-configuration, joined without doubling the slash.
        var url = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        var body = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<OidcDiscoveryDocument>(body, Json);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "OIDC: discovery document could not be parsed.");
            return null;
        }
    }

    private static bool NonceMatches(string idToken, string expectedNonce)
    {
        // The token's signature was already verified by OidcIdentity, so reading its claim here is safe.
        try
        {
            var token = new JsonWebToken(idToken);
            return token.TryGetPayloadValue<string>("nonce", out var nonce)
                && string.Equals(nonce, expectedNonce, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) => Base64UrlEncoder.Encode(bytes);

    // Appends form-encoded query params to a base URL (which may already carry a query), skipping nulls.
    private static string BuildUrl(string baseUrl, IEnumerable<(string Key, string? Value)> parameters)
    {
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var builder = new StringBuilder(baseUrl);
        foreach (var (key, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(separator).Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }

    private sealed record OidcEndpoints(string? AuthorizationEndpoint, string? TokenEndpoint);
}

/// <summary>Typed view of an OIDC discovery document (<c>/.well-known/openid-configuration</c>) — only the
/// fields Agnes needs. External boundary schema, deserialized immediately into this record.</summary>
public sealed record OidcDiscoveryDocument(
    [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
    [property: JsonPropertyName("jwks_uri")] string? JwksUri,
    [property: JsonPropertyName("issuer")] string? Issuer);

/// <summary>Typed view of the token-endpoint response — only the id_token Agnes validates. External boundary
/// schema, deserialized immediately into this record.</summary>
public sealed record OidcTokenResponse(
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType);
