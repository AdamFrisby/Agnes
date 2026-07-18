using System.Security.Cryptography;

namespace Agnes.Host.Hosting;

/// <summary>
/// Minimal v1 device-token store: validates bearer tokens presented by clients. A dev
/// pairing token is generated at startup (or taken from config) and logged. This is a
/// placeholder for the full TLS + device-pairing flow (short code / QR, per-device tokens).
/// </summary>
public sealed class DeviceTokenStore
{
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);

    public DeviceTokenStore(string? configuredToken)
    {
        PairingToken = string.IsNullOrWhiteSpace(configuredToken) ? GenerateToken() : configuredToken;
        _tokens.Add(PairingToken);
    }

    /// <summary>The dev pairing token a client must present to connect.</summary>
    public string PairingToken { get; }

    public bool IsValid(string? token) => token is not null && _tokens.Contains(token);

    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
