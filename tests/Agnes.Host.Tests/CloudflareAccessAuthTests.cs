using System.Security.Cryptography;
using System.Text.Json;
using Agnes.Host.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agnes.Host.Tests;

public sealed class CloudflareAccessAuthTests
{
    private const string TeamDomain = "team.cloudflareaccess.test";
    private const string Issuer = "https://" + TeamDomain;
    private const string Audience = "agnes-access";
    private const string KeyId = "cloudflare-test-key";
    private static readonly RSA SigningKey = RSA.Create(2048);

    [Fact]
    public async Task Signed_assertion_for_allowed_domain_is_accepted()
    {
        var result = await Identity("sinewavecompany.com").ValidateAsync(Token("stefan@sinewavecompany.com"));

        Assert.True(result.Ok);
        Assert.Equal("stefan@sinewavecompany.com", result.Email);
        Assert.Equal("person-123", result.Subject);
    }

    [Fact]
    public async Task Signed_assertion_for_other_domain_is_rejected()
    {
        var result = await Identity("sinewavecompany.com").ValidateAsync(Token("attacker@example.test"));

        Assert.False(result.Ok);
        Assert.Null(result.Email);
    }

    [Fact]
    public async Task Tampered_assertion_is_rejected()
    {
        var token = Token("stefan@sinewavecompany.com");
        var lastDot = token.LastIndexOf('.');
        var tampered = token[..(lastDot + 1)] + Flip(token[(lastDot + 1)..]);

        var result = await Identity("sinewavecompany.com").ValidateAsync(tampered);

        Assert.False(result.Ok);
    }

    private static CloudflareAccessIdentity Identity(params string[] allowedDomains) => new(new CloudflareAccessOptions
    {
        Enabled = true,
        TeamDomain = TeamDomain,
        Audience = Audience,
        JwksJson = Jwks(),
        AllowedEmailDomains = allowedDomains,
    });

    private static string Token(string email)
    {
        var signingKey = new RsaSecurityKey(SigningKey) { KeyId = KeyId };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "person-123",
                ["email"] = email,
            },
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static string Jwks()
    {
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(SigningKey.ExportParameters(includePrivateParameters: false));
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(publicKey) { KeyId = KeyId });
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.RsaSha256;
        return JsonSerializer.Serialize(new { keys = new[] { new { jwk.Kty, jwk.Kid, jwk.Use, jwk.Alg, jwk.N, jwk.E } } });
    }

    private static string Flip(string value)
    {
        var chars = value.ToCharArray();
        var index = chars.Length / 2;
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        return new string(chars);
    }
}
