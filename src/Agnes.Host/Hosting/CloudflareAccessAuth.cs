namespace Agnes.Host.Hosting;

/// <summary>Configuration for exchanging a signed Cloudflare Access assertion for an Agnes device token.</summary>
public sealed record CloudflareAccessOptions
{
    public bool Enabled { get; init; }
    public string? TeamDomain { get; init; }
    public string? Audience { get; init; }
    public string? JwksJson { get; init; }
    public string? JwksUri { get; init; }
    public string[] AllowedEmailDomains { get; init; } = [];

    public bool IsUsable => Enabled
        && !string.IsNullOrWhiteSpace(TeamDomain)
        && !string.IsNullOrWhiteSpace(Audience)
        && AllowedEmailDomains.Any(domain => !string.IsNullOrWhiteSpace(domain));

    internal OidcOptions ToValidationOptions()
    {
        var teamDomain = TeamDomain?.Trim().TrimEnd('/') ?? string.Empty;
        return new OidcOptions
        {
            Enabled = Enabled,
            Issuer = $"https://{teamDomain}",
            Audience = Audience,
            // Cloudflare publishes Access signing keys at this stable endpoint. An explicit
            // value supports tightly controlled/air-gapped deployments and deterministic tests.
            JwksJson = JwksJson,
            JwksUri = JwksUri ?? $"https://{teamDomain}/cdn-cgi/access/certs",
            DisplayName = "Cloudflare Access",
        };
    }
}

/// <summary>Result of validating a Cloudflare Access assertion.</summary>
public sealed record CloudflareAccessResult(bool Ok, string? Email, string? Subject, string? Reason)
{
    public static CloudflareAccessResult Reject(string reason) => new(false, null, null, reason);
    public static CloudflareAccessResult Accept(string email, string subject) => new(true, email, subject, null);
}

/// <summary>
/// Validates only Cloudflare's signed Access JWT, never an email convenience header. Successful identities
/// are also checked against an application-side exact email-domain allowlist before a device token is minted.
/// </summary>
public sealed class CloudflareAccessIdentity
{
    public const string AssertionHeaderName = "Cf-Access-Jwt-Assertion";
    private const int MaxAssertionLength = 16 * 1024;
    private readonly OidcIdentity _validator;

    public CloudflareAccessIdentity(CloudflareAccessOptions options, HttpClient? http = null)
    {
        Options = options;
        _validator = new OidcIdentity(options.ToValidationOptions(), http);
    }

    public CloudflareAccessOptions Options { get; }

    public async Task<CloudflareAccessResult> ValidateAsync(string? assertion, CancellationToken cancellationToken = default)
    {
        if (!Options.IsUsable)
        {
            return CloudflareAccessResult.Reject("Cloudflare Access sign-in is not configured on this host.");
        }

        if (string.IsNullOrWhiteSpace(assertion))
        {
            return CloudflareAccessResult.Reject("No Cloudflare Access assertion was presented.");
        }

        if (assertion.Length > MaxAssertionLength)
        {
            return CloudflareAccessResult.Reject("The Cloudflare Access assertion is too large.");
        }

        var validated = await _validator.ValidateAsync(assertion, cancellationToken).ConfigureAwait(false);
        if (!validated.Ok || string.IsNullOrWhiteSpace(validated.Email) || string.IsNullOrWhiteSpace(validated.TokenSubject))
        {
            return CloudflareAccessResult.Reject(validated.Reason ?? "The Cloudflare Access assertion is invalid.");
        }

        var at = validated.Email.LastIndexOf('@');
        if (at <= 0 || at == validated.Email.Length - 1)
        {
            return CloudflareAccessResult.Reject("The Cloudflare Access assertion contained no valid email address.");
        }

        var domain = validated.Email[(at + 1)..];
        var allowed = Options.AllowedEmailDomains
            .Select(value => value.Trim().TrimStart('@'))
            .Where(value => value.Length > 0)
            .Any(value => string.Equals(value, domain, StringComparison.OrdinalIgnoreCase));
        return allowed
            ? CloudflareAccessResult.Accept(validated.Email, validated.TokenSubject)
            : CloudflareAccessResult.Reject("This Cloudflare Access identity is not authorized for this host.");
    }
}
