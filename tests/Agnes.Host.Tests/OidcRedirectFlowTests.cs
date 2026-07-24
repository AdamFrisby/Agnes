using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agnes.Host.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agnes.Host.Tests;

/// <summary>
/// The interactive OIDC authorization-code + PKCE redirect flow (security/02): the piece that obtains the
/// token the validation core consumes. The IdP's discovery, authorize and token endpoints are stubbed via a
/// fake <see cref="HttpMessageHandler"/> — no real IdP, no network. The id_token is signed by a test RSA key
/// whose public JWKS the validator holds (reusing the <see cref="OidcAuthTests"/> key/JWKS pattern), so the
/// callback exercises the real validation core end to end. Covers PKCE correctness, the happy path (mints a
/// device token), CSRF (unknown/expired state), nonce-replay defence, a rejected token, and discovery vs.
/// explicit-config endpoint resolution.
/// </summary>
public class OidcRedirectFlowTests
{
    private const string Issuer = "https://issuer.test/";
    private const string Audience = "agnes-host";
    private const string KeyId = "test-key-1";
    private const string ClientId = "agnes-client";
    private const string RedirectUri = "https://host.example/auth/oidc/callback";
    private const string DiscoveredAuthorize = "https://issuer.test/oauth/authorize";
    private const string DiscoveredToken = "https://issuer.test/oauth/token";

    // One RSA keypair for the class: the private half signs id_tokens, the public half is the published JWKS.
    private static readonly RSA SigningKey = RSA.Create(2048);

    private static string Jwks()
    {
        using var pub = RSA.Create();
        pub.ImportParameters(SigningKey.ExportParameters(includePrivateParameters: false));
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(pub) { KeyId = KeyId });
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.RsaSha256;
        return JsonSerializer.Serialize(new { keys = new[] { new { jwk.Kty, jwk.Kid, jwk.Use, jwk.Alg, jwk.N, jwk.E } } });
    }

    private static string IdToken(string? nonce, DateTime? expires = null, string audience = Audience, string subject = "user-123")
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["preferred_username"] = "alice" };
        if (nonce is not null)
        {
            claims["nonce"] = nonce;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(SigningKey) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static OidcOptions Options(bool discovery = true) => new()
    {
        Enabled = true,
        Issuer = Issuer,
        Audience = Audience,
        JwksJson = Jwks(),
        ClientId = ClientId,
        RedirectUri = RedirectUri,
        Scopes = "openid profile email",
        AuthorizationEndpoint = discovery ? null : "https://explicit.test/authorize",
        TokenEndpoint = discovery ? null : "https://explicit.test/token",
    };

    private static DeviceRegistry Devices()
        => new(null, Path.Combine(Path.GetTempPath(), "agnes-oidc-redirect-" + Guid.NewGuid().ToString("n") + ".json"), pairingEnabled: true);

    // A settable clock so state-TTL expiry is deterministic (token lifetimes use real time in the validator).
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Records the last stashed pending flow so the token stub can echo the generated nonce into the id_token.
    private sealed class CapturingStore(TimeProvider clock) : IOidcStateStore
    {
        private readonly InMemoryOidcStateStore _inner = new(clock);
        public OidcPendingAuth? Last { get; private set; }

        public void Put(string state, OidcPendingAuth pending)
        {
            Last = pending;
            _inner.Put(state, pending);
        }

        public OidcPendingAuth? Take(string state) => _inner.Take(state);
    }

    // Stubs the IdP: discovery document, and a token endpoint whose id_token is built lazily per-request so
    // it can carry the nonce the flow generated. Captures the parsed token-request form for PKCE assertions.
    // Parses an application/x-www-form-urlencoded string (or a URL query) into a case-sensitive map.
    private static Dictionary<string, string> ParseForm(string encoded)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }

        return result;
    }

    private sealed class StubIdp(CapturingStore store, Func<string?, string> tokenBuilder, bool failDiscovery = false) : HttpMessageHandler
    {
        public Dictionary<string, string>? LastTokenForm { get; private set; }
        public bool DiscoveryCalled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                DiscoveryCalled = true;
                if (failDiscovery)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                var doc = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["issuer"] = Issuer,
                    ["authorization_endpoint"] = DiscoveredAuthorize,
                    ["token_endpoint"] = DiscoveredToken,
                    ["jwks_uri"] = "https://issuer.test/jwks",
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(doc) };
            }

            // Token endpoint (POST form): capture the form, then return an id_token carrying the stashed nonce.
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            LastTokenForm = ParseForm(body);
            var idToken = tokenBuilder(store.Last?.Nonce);
            var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["id_token"] = idToken, ["token_type"] = "Bearer" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private static (OidcRedirectFlow Flow, CapturingStore Store, DeviceRegistry Devices, StubIdp Idp) Build(
        Func<string?, string>? tokenBuilder = null,
        bool discovery = true,
        bool failDiscovery = false)
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var store = new CapturingStore(clock);
        var idp = new StubIdp(store, tokenBuilder ?? (nonce => IdToken(nonce)), failDiscovery);
        var http = new HttpClient(idp);
        var devices = Devices();
        var oidc = new OidcIdentity(Options(discovery), http);
        var flow = new OidcRedirectFlow(oidc, devices, http, store, clock);
        return (flow, store, devices, idp);
    }

    private static Dictionary<string, string> AuthorizeQuery(string url)
        => ParseForm(new Uri(url).Query.TrimStart('?'));

    [Fact]
    public async Task Authorize_url_carries_the_expected_pkce_and_oauth_params()
    {
        var (flow, _, _, _) = Build();

        var start = await flow.StartAsync("my-laptop");
        Assert.NotNull(start);
        Assert.StartsWith(DiscoveredAuthorize, start!.AuthorizationUrl, StringComparison.Ordinal);

        var q = AuthorizeQuery(start.AuthorizationUrl);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal(ClientId, q["client_id"]);
        Assert.Equal(RedirectUri, q["redirect_uri"]);
        Assert.Equal("openid profile email", q["scope"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(q["code_challenge"]));
        Assert.Equal(start.State, q["state"]);
        Assert.False(string.IsNullOrEmpty(q["nonce"]));
    }

    [Fact]
    public async Task Code_challenge_is_the_base64url_sha256_of_the_verifier()
    {
        var (flow, store, _, _) = Build();

        var start = await flow.StartAsync(null);
        var challenge = AuthorizeQuery(start!.AuthorizationUrl)["code_challenge"];

        // The verifier is held server-side; recompute the challenge from it and compare to what the URL carried.
        var verifier = store.Last!.CodeVerifier;
        var expected = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(expected, challenge);
    }

    [Fact]
    public async Task Callback_happy_path_validates_the_id_token_and_mints_a_device_token()
    {
        var (flow, store, devices, idp) = Build();

        var start = await flow.StartAsync("my-phone");
        var result = await flow.HandleCallbackAsync("auth-code-xyz", start!.State);

        Assert.True(result.Ok, result.Reason);
        Assert.NotNull(result.Pairing);
        Assert.False(string.IsNullOrEmpty(result.Pairing!.Token));
        Assert.Equal("my-phone", result.Pairing.DeviceName);

        // The token was really issued and is now a valid bearer for the host.
        Assert.True(devices.IsValid(result.Pairing.Token));

        // The token exchange carried the authorization-code grant with the PKCE verifier and no leaked secret.
        Assert.Equal("authorization_code", idp.LastTokenForm!["grant_type"]);
        Assert.Equal("auth-code-xyz", idp.LastTokenForm["code"]);
        Assert.Equal(store.Last!.CodeVerifier, idp.LastTokenForm["code_verifier"]);
        Assert.Equal(ClientId, idp.LastTokenForm["client_id"]);
    }

    [Fact]
    public async Task Unknown_state_is_rejected_as_csrf()
    {
        var (flow, _, _, _) = Build();
        // No StartAsync — the state was never issued by this host.
        var result = await flow.HandleCallbackAsync("some-code", "forged-state-value");
        Assert.False(result.Ok);
        Assert.Null(result.Pairing);
        Assert.Contains("Unknown or expired", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expired_state_is_rejected()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var store = new CapturingStore(clock);
        var idp = new StubIdp(store, nonce => IdToken(nonce));
        var http = new HttpClient(idp);
        var flow = new OidcRedirectFlow(new OidcIdentity(Options(), http), Devices(), http, store, clock);

        var start = await flow.StartAsync(null);
        clock.Now = clock.Now.AddMinutes(11); // past the 10-minute state TTL

        var result = await flow.HandleCallbackAsync("code", start!.State);
        Assert.False(result.Ok);
        Assert.Null(result.Pairing);
    }

    [Fact]
    public async Task A_replayed_state_cannot_be_used_twice()
    {
        var (flow, _, _, _) = Build();
        var start = await flow.StartAsync(null);

        Assert.True((await flow.HandleCallbackAsync("code", start!.State)).Ok);
        // The state was consumed on first use — a second callback with it finds nothing.
        Assert.False((await flow.HandleCallbackAsync("code", start.State)).Ok);
    }

    [Fact]
    public async Task Nonce_mismatch_is_rejected()
    {
        // The token stub signs a token whose nonce is NOT the one the flow stashed (replay of a foreign token).
        var (flow, _, _, _) = Build(tokenBuilder: _ => IdToken(nonce: "a-different-nonce"));

        var start = await flow.StartAsync(null);
        var result = await flow.HandleCallbackAsync("code", start!.State);

        Assert.False(result.Ok);
        Assert.Contains("nonce", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expired_id_token_is_rejected_by_the_validation_core()
    {
        var (flow, _, _, _) = Build(tokenBuilder: nonce => IdToken(nonce, expires: DateTime.UtcNow.AddMinutes(-5)));

        var start = await flow.StartAsync(null);
        var result = await flow.HandleCallbackAsync("code", start!.State);

        Assert.False(result.Ok);
        Assert.Contains("expired", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_audience_id_token_is_rejected_by_the_validation_core()
    {
        var (flow, _, _, _) = Build(tokenBuilder: nonce => IdToken(nonce, audience: "some-other-app"));

        var start = await flow.StartAsync(null);
        var result = await flow.HandleCallbackAsync("code", start!.State);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Discovery_resolves_the_authorize_and_token_endpoints()
    {
        var (flow, _, _, idp) = Build(discovery: true);

        var start = await flow.StartAsync(null);
        Assert.True(idp.DiscoveryCalled);
        Assert.StartsWith(DiscoveredAuthorize, start!.AuthorizationUrl, StringComparison.Ordinal);

        // The token endpoint discovered was used for the exchange too.
        Assert.True((await flow.HandleCallbackAsync("code", start.State)).Ok);
    }

    [Fact]
    public async Task Explicit_endpoint_config_overrides_discovery()
    {
        // failDiscovery would make discovery error — proving it isn't consulted when endpoints are explicit.
        var (flow, _, _, idp) = Build(discovery: false, failDiscovery: true);

        var start = await flow.StartAsync(null);
        Assert.NotNull(start);
        Assert.False(idp.DiscoveryCalled);
        Assert.StartsWith("https://explicit.test/authorize", start!.AuthorizationUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unconfigured_flow_is_not_usable()
    {
        var http = new HttpClient(new StubIdp(new CapturingStore(TimeProvider.System), _ => ""));
        // No ClientId / RedirectUri → the redirect flow is not configured (fail-closed), even though the
        // validation core is usable.
        var oidc = new OidcIdentity(new OidcOptions { Enabled = true, Issuer = Issuer, Audience = Audience, JwksJson = Jwks() }, http);
        var flow = new OidcRedirectFlow(oidc, Devices(), http, new CapturingStore(TimeProvider.System), TimeProvider.System);

        Assert.False(flow.IsConfigured);
        Assert.Null(await flow.StartAsync(null));
        Assert.False((await flow.HandleCallbackAsync("code", "state")).Ok);
    }
}
