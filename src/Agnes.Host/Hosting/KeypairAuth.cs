using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Host config for keypair auth (bound from <c>Agnes:Auth:Keypair</c>).</summary>
public sealed record KeypairAuthOptions
{
    public bool Enabled { get; init; }

    /// <summary>File of authorized public keys — one base64 SPKI (P-256) per line, optional trailing label.</summary>
    public string AuthorizedKeysFile { get; init; } = "";
}

/// <summary>
/// SSH-<c>authorized_keys</c>-style bootstrap: a client proves possession of a private key whose public
/// key the operator added to the host. The host hands out a single-use, short-lived challenge nonce; the
/// client signs it (ECDSA P-256 / SHA-256, DER) and returns the signature + its public key. If the key is
/// authorized and the signature verifies, an Agnes device token is issued. No shared secret, works offline.
/// </summary>
public sealed class KeypairAuth(KeypairAuthOptions options, ILogger<KeypairAuth>? logger = null)
{
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);

    /// <summary>Enabled and at least one authorized key is present.</summary>
    public bool IsUsable => options.Enabled && LoadAuthorizedKeys().Count > 0;

    /// <summary>Mints a single-use challenge nonce (valid for a couple of minutes).</summary>
    public string IssueChallenge()
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            // opportunistically prune expired nonces so the dictionary can't grow unbounded.
            foreach (var stale in _nonces.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
            {
                _nonces.Remove(stale);
            }

            _nonces[nonce] = now + NonceTtl;
        }

        return nonce;
    }

    /// <summary>
    /// Verifies a signed challenge. Returns the matched key's label if the nonce is valid+unused, the public
    /// key is authorized, and the signature over the nonce checks out; else null. The nonce is consumed
    /// (single-use) regardless of the outcome.
    /// </summary>
    public string? Verify(string? publicKeyB64, string? nonce, string? signatureB64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyB64) || string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(signatureB64))
        {
            return null;
        }

        // Consume the nonce first — a single-use challenge, valid or not.
        lock (_gate)
        {
            if (!_nonces.Remove(nonce, out var expiry) || expiry < DateTimeOffset.UtcNow)
            {
                return null;
            }
        }

        byte[] incomingSpki, signature;
        try
        {
            using var incoming = ECDsa.Create();
            incoming.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyB64.Trim()), out _);
            incomingSpki = incoming.ExportSubjectPublicKeyInfo(); // canonical form for matching
            signature = Convert.FromBase64String(signatureB64);

            var match = LoadAuthorizedKeys().FirstOrDefault(k => k.Spki.AsSpan().SequenceEqual(incomingSpki));
            if (match.Spki is null)
            {
                logger?.LogWarning("Keypair auth: presented key is not authorized");
                return null;
            }

            var ok = incoming.VerifyData(Encoding.UTF8.GetBytes(nonce), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return ok ? match.Label : null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Keypair auth: could not verify the signed challenge");
            return null;
        }
    }

    private List<(byte[] Spki, string Label)> LoadAuthorizedKeys()
    {
        var file = options.AuthorizedKeysFile;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            return [];
        }

        var keys = new List<(byte[] Key, string Label)>();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(parts[0]), out _);
                keys.Add((ecdsa.ExportSubjectPublicKeyInfo(), parts.Length > 1 ? parts[1] : "key"));
            }
            catch
            {
                logger?.LogWarning("Skipping an invalid line in {File}", file);
            }
        }

        return keys;
    }
}
