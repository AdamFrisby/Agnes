using System.Collections.Concurrent;
using System.Security.Cryptography;
using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// One-time, high-entropy pairing secrets minted by an already-paired device for a new one.
///
/// The short bootstrap code exists to be read off a screen and typed, which caps its entropy at about
/// forty bits — acceptable only as a one-shot bootstrap behind rate limiting, and the reason it rotates
/// after a handful of bad attempts. A grant is carried in a QR and never typed, so nothing caps it:
/// 256 bits from the CSPRNG, single-use, and valid for minutes rather than for the host's lifetime.
///
/// Grants are held in memory only. A restart invalidating outstanding grants is correct — they're
/// seconds-to-minutes old by design, and persisting a bearer secret to disk to save a re-scan would be a
/// poor trade.
/// </summary>
public sealed class PairingGrants
{
    /// <summary>How long a grant stays redeemable. Long enough to walk a phone to a screen, short enough
    /// that a QR left on a monitor stops being a credential.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Grant secrets are this many bytes of CSPRNG output before base64url encoding.</summary>
    private const int SecretBytes = 32;

    private readonly ConcurrentDictionary<string, Entry> _grants = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    public PairingGrants(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    private sealed record Entry(DateTimeOffset ExpiresAt, string? SessionId);

    /// <summary>
    /// Mints a grant. Callers must already be authenticated — that authentication *is* the vouching:
    /// only a device the host already trusts can invite another one.
    /// </summary>
    /// <param name="sessionId">Optional session to hand over alongside the host, so a scanned QR can land
    /// the new device directly in the session it was generated from.</param>
    public PairingGrant Mint(
        string reachableAddress, string? sessionId = null, IReadOnlyList<string>? addresses = null)
    {
        Sweep();

        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        var expires = _now() + Lifetime;
        _grants[secret] = new Entry(expires, sessionId);

        return new PairingGrant(
            secret, PairingReachability.BuildDeepLink(reachableAddress, secret, sessionId), expires, addresses);
    }

    /// <summary>
    /// Redeems a grant, returning the session it carried (if any). Single-use: a redeemed secret is gone
    /// whether or not the caller goes on to succeed, so a leaked QR can't be replayed.
    /// </summary>
    public bool TryRedeem(string? secret, out string? sessionId)
    {
        sessionId = null;
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        Sweep();

        // Compare against the stored key set in constant time. A dictionary probe alone would leak
        // whether a prefix matched through timing; grants are high-entropy enough that this is
        // belt-and-braces, but the cost is nil.
        var match = _grants.Keys.FirstOrDefault(k => FixedTimeEquals(k, secret.Trim()));
        if (match is null || !_grants.TryRemove(match, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= _now())
        {
            return false;
        }

        sessionId = entry.SessionId;
        return true;
    }

    /// <summary>Drops a grant early — used when the operator hides a displayed QR.</summary>
    public void Revoke(string secret) => _grants.TryRemove(secret, out _);

    /// <summary>How many grants are outstanding (for the host's status surface and tests).</summary>
    public int OutstandingCount
    {
        get
        {
            Sweep();
            return _grants.Count;
        }
    }

    private void Sweep()
    {
        var now = _now();
        foreach (var (key, entry) in _grants)
        {
            if (entry.ExpiresAt <= now)
            {
                _grants.TryRemove(key, out _);
            }
        }
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    /// <summary>base64url without padding — safe in a URL query, which is where a grant travels.</summary>
    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
