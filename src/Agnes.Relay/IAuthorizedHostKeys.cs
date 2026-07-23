using System.Security.Cryptography;
using Agnes.Relay.Protocol;

namespace Agnes.Relay;

/// <summary>The set of host public keys the relay will let claim a host-id (an allow-list).</summary>
public interface IAuthorizedHostKeys
{
    /// <summary>True if the given canonical SPKI (P-256) is on the allow-list.</summary>
    bool IsAuthorized(ReadOnlySpan<byte> spki);
}

/// <summary>
/// Verifies a host's per-host key: the presented public key must be on the allow-list, and the
/// presented signature must check out over the challenge nonce bound to the claimed host-id.
/// All crypto is BCL <see cref="ECDsa"/> (P-256 / SHA-256, DER) — never hand-rolled.
/// </summary>
public static class HostKeyVerifier
{
    /// <summary>The exact bytes a host signs to prove a key controls a host-id (shared with the host transport).</summary>
    public static byte[] ChallengePayload(string nonce, string hostId) =>
        RelayChallenge.Payload(nonce, hostId);

    /// <summary>
    /// Returns the canonical SPKI of the presented key iff the key is authorized and its signature
    /// over <c>nonce\nhostId</c> verifies; otherwise <c>null</c>. Any malformed input fails closed.
    /// </summary>
    public static byte[]? Verify(IAuthorizedHostKeys authorized, string nonce, string hostId,
        string publicKeyB64, string signatureB64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyB64.Trim()), out _);
            byte[] spki = ecdsa.ExportSubjectPublicKeyInfo(); // canonical form for matching.

            if (!authorized.IsAuthorized(spki))
            {
                return null;
            }

            byte[] signature = Convert.FromBase64String(signatureB64);
            bool ok = ecdsa.VerifyData(ChallengePayload(nonce, hostId), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return ok ? spki : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>File-backed <see cref="IAuthorizedHostKeys"/> (re-read on each check, like the host's own keypair auth).</summary>
public sealed class FileAuthorizedHostKeys(string authorizedKeysFile, IRelayLog? log = null) : IAuthorizedHostKeys
{
    private readonly IRelayLog _log = log ?? NullRelayLog.Instance;

    public bool IsAuthorized(ReadOnlySpan<byte> spki)
    {
        foreach (byte[] key in Load())
        {
            if (key.AsSpan().SequenceEqual(spki))
            {
                return true;
            }
        }

        return false;
    }

    private List<byte[]> Load()
    {
        var keys = new List<byte[]>();
        if (string.IsNullOrWhiteSpace(authorizedKeysFile) || !File.Exists(authorizedKeysFile))
        {
            return keys;
        }

        foreach (string raw in File.ReadAllLines(authorizedKeysFile))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string b64 = line.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(b64), out _);
                keys.Add(ecdsa.ExportSubjectPublicKeyInfo());
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                _log.Warn($"Skipping an invalid authorized-host-key line in {authorizedKeysFile}");
            }
        }

        return keys;
    }
}

/// <summary>In-memory allow-list — convenient for composing a fixed set (and for tests).</summary>
public sealed class InMemoryAuthorizedHostKeys : IAuthorizedHostKeys
{
    private readonly List<byte[]> _keys = [];

    public InMemoryAuthorizedHostKeys(IEnumerable<byte[]> spkiKeys)
    {
        foreach (byte[] k in spkiKeys)
        {
            _keys.Add(k.ToArray());
        }
    }

    public bool IsAuthorized(ReadOnlySpan<byte> spki)
    {
        foreach (byte[] key in _keys)
        {
            if (key.AsSpan().SequenceEqual(spki))
            {
                return true;
            }
        }

        return false;
    }
}
