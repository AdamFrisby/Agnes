using System.Security.Cryptography;
using System.Text.Json;
using Agnes.Host.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agnes.Host.Tests;

/// <summary>
/// OIDC token-validation core (AC1/AC5): a JWT signed by a test RSA key whose public JWKS the host is
/// configured with is accepted and yields a subject; tampered, expired, wrong-issuer and wrong-audience
/// tokens are each rejected with a distinct reason. Fully in-process — no IdP, no network.
/// </summary>
public class OidcAuthTests
{
    private const string Issuer = "https://issuer.test/";
    private const string Audience = "agnes-host";
    private const string KeyId = "test-key-1";

    // One RSA keypair for the whole class: the private half signs test tokens, the public half is published
    // as the JWKS the host validates against.
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

    private static string Token(
        string issuer = Issuer,
        string audience = Audience,
        DateTime? expires = null,
        string subject = "user-123",
        string? preferredUsername = "alice")
    {
        var signingKey = new RsaSecurityKey(SigningKey) { KeyId = KeyId };
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (preferredUsername is not null)
        {
            claims["preferred_username"] = preferredUsername;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static OidcIdentity Host(string? jwks = null, bool enabled = true, string issuer = Issuer, string audience = Audience)
        => new(new OidcOptions
        {
            Enabled = enabled,
            Issuer = issuer,
            Audience = audience,
            JwksJson = jwks ?? Jwks(),
        });

    [Fact]
    public async Task Valid_token_is_accepted_and_carries_the_preferred_username_subject()
    {
        var result = await Host().ValidateAsync(Token());
        Assert.True(result.Ok);
        Assert.Equal("alice", result.Subject);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task Valid_token_without_preferred_username_falls_back_to_sub()
    {
        var result = await Host().ValidateAsync(Token(preferredUsername: null, subject: "sub-999"));
        Assert.True(result.Ok);
        Assert.Equal("sub-999", result.Subject);
    }

    [Fact]
    public async Task Tampered_signature_is_rejected()
    {
        var token = Token();
        // Flip a character in the signature segment (the last dot-delimited part).
        var lastDot = token.LastIndexOf('.');
        var tampered = token[..(lastDot + 1)] + Flip(token[(lastDot + 1)..]);

        var result = await Host().ValidateAsync(tampered);
        Assert.False(result.Ok);
        Assert.Null(result.Subject);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task Expired_token_is_rejected_with_an_expiry_reason()
    {
        var result = await Host().ValidateAsync(Token(expires: DateTime.UtcNow.AddMinutes(-5)));
        Assert.False(result.Ok);
        Assert.Contains("expired", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_issuer_is_rejected()
    {
        var result = await Host().ValidateAsync(Token(issuer: "https://evil.test/"));
        Assert.False(result.Ok);
        Assert.Contains("issuer", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        var result = await Host().ValidateAsync(Token(audience: "some-other-app"));
        Assert.False(result.Ok);
        Assert.Contains("host", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Token_signed_by_an_unrelated_key_is_rejected()
    {
        // Publish a JWKS for a *different* key than the one that signed the token.
        using var other = RSA.Create(2048);
        var otherJwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(other) { KeyId = KeyId });
        var otherJwks = JsonSerializer.Serialize(new { keys = new[] { new { otherJwk.Kty, otherJwk.Kid, Use = "sig", Alg = "RS256", otherJwk.N, otherJwk.E } } });

        var result = await Host(jwks: otherJwks).ValidateAsync(Token());
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Disabled_or_misconfigured_host_rejects_without_validating()
    {
        Assert.False(new OidcAuthMethodProvider(Host(enabled: false)).IsEnabled);
        var result = await Host(enabled: false).ValidateAsync(Token());
        Assert.False(result.Ok);

        // Enabled but no audience → fail-closed (unusable), rejected.
        var noAudience = new OidcIdentity(new OidcOptions { Enabled = true, Issuer = Issuer, Audience = "", JwksJson = Jwks() });
        Assert.False(noAudience.Options.IsUsable);
        Assert.False((await noAudience.ValidateAsync(Token())).Ok);
    }

    [Fact]
    public void Provider_advertises_the_issuer_only_when_usable()
    {
        var provider = new OidcAuthMethodProvider(Host());
        Assert.True(provider.IsEnabled);
        Assert.Equal(Issuer, provider.ClientMetadata["issuer"]);
        Assert.Equal(Audience, provider.ClientMetadata["audience"]);

        Assert.Empty(new OidcAuthMethodProvider(Host(enabled: false)).ClientMetadata);
    }

    private static string Flip(string s)
    {
        var chars = s.ToCharArray();
        var i = chars.Length / 2;
        chars[i] = chars[i] == 'A' ? 'B' : 'A';
        return new string(chars);
    }
}
