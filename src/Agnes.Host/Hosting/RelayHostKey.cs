using System.Security.Cryptography;
using Agnes.Relay.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The host's per-host relay key: a persistent ECDSA P-256 key pair whose <b>public</b> key the operator
/// adds to the relay's authorized-hosts allow-list. During registration the host signs the relay's
/// challenge nonce (bound to the claimed host-id) to prove possession. All crypto is BCL — never
/// hand-rolled. See <c>.ideas/connectivity/01-relay-and-tunneling.md</c>.
/// </summary>
public interface IRelayHostKey
{
    /// <summary>Base64 SPKI (P-256) — the exact line the operator pastes into the relay's authorized-hosts file.</summary>
    string PublicKeyBase64 { get; }

    /// <summary>Signs <c>nonce\nhostId</c> (ECDSA/SHA-256, DER) and returns the base64 signature.</summary>
    string SignChallenge(string nonce, string hostId);
}

/// <summary>
/// File-backed <see cref="IRelayHostKey"/>: the private key is generated once (PKCS#8) and reused across
/// restarts, so the host keeps the same relay identity (and stays on the operator's allow-list) after a
/// restart.
/// </summary>
public sealed class FileRelayHostKey : IRelayHostKey, IDisposable
{
    private readonly ECDsa _ecdsa;

    /// <param name="keyPath">Where the PKCS#8 private key (PEM) is persisted.</param>
    public FileRelayHostKey(string keyPath, ILogger<FileRelayHostKey>? logger = null)
    {
        _ecdsa = LoadOrCreate(keyPath, logger);
        PublicKeyBase64 = Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <inheritdoc />
    public string PublicKeyBase64 { get; }

    /// <inheritdoc />
    public string SignChallenge(string nonce, string hostId)
    {
        byte[] signature = _ecdsa.SignData(
            RelayChallenge.Payload(nonce, hostId), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(signature);
    }

    private static ECDsa LoadOrCreate(string keyPath, ILogger<FileRelayHostKey>? logger)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(keyPath))
        {
            try
            {
                ecdsa.ImportFromPem(File.ReadAllText(keyPath));
                return ecdsa;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                logger?.LogWarning(ex, "Existing relay host key at {Path} could not be loaded — regenerating.", keyPath);
            }
        }

        try
        {
            string? dir = Path.GetDirectoryName(keyPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(keyPath, ecdsa.ExportPkcs8PrivateKeyPem());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not persist the relay host key to {Path}; using an in-memory key this run.", keyPath);
        }

        return ecdsa;
    }

    public void Dispose() => _ecdsa.Dispose();
}

/// <summary>In-memory <see cref="IRelayHostKey"/> — convenient for composing a fixed key (and for tests).</summary>
public sealed class InMemoryRelayHostKey : IRelayHostKey, IDisposable
{
    private readonly ECDsa _ecdsa;

    public InMemoryRelayHostKey(ECDsa? ecdsa = null)
    {
        _ecdsa = ecdsa ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PublicKeyBase64 = Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The canonical SPKI bytes — handy for seeding an allow-list directly in a test.</summary>
    public byte[] Spki => _ecdsa.ExportSubjectPublicKeyInfo();

    public string PublicKeyBase64 { get; }

    public string SignChallenge(string nonce, string hostId)
        => Convert.ToBase64String(_ecdsa.SignData(
            RelayChallenge.Payload(nonce, hostId), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    public void Dispose() => _ecdsa.Dispose();
}
