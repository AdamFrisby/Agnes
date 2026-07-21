using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// Client side of keypair auth (SSH-<c>authorized_keys</c> style). Generates/loads a P-256 key, prints the
/// public line to add on the host, and authenticates by signing the host's challenge nonce. Like
/// <see cref="DevicePairing"/>, it returns the durable device token to store and connect with.
/// </summary>
public static class KeypairEnrollment
{
    private static string HomeAgnes =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes");

    /// <summary>Default private-key location (PKCS#8 DER).</summary>
    public static string DefaultKeyPath => Path.Combine(HomeAgnes, "client_key.p8");

    /// <summary>Loads the client's P-256 key, generating and persisting one on first use.</summary>
    public static ECDsa LoadOrCreateKey(string? keyPath = null)
    {
        keyPath ??= DefaultKeyPath;
        if (File.Exists(keyPath))
        {
            var loaded = ECDsa.Create();
            loaded.ImportPkcs8PrivateKey(File.ReadAllBytes(keyPath), out _);
            return loaded;
        }

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, key.ExportPkcs8PrivateKey());
        TryRestrictPermissions(keyPath);
        return key;
    }

    /// <summary>The line to add to the host's <c>authorized_keys</c>: base64 SPKI public key + a label.</summary>
    public static string PublicKeyLine(ECDsa key, string? label = null)
        => $"{Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())} {label ?? "agnes-" + Environment.MachineName}";

    /// <summary>Authenticates to the host by signing its challenge; returns the issued device token.</summary>
    public static async Task<PairResponse> AuthenticateAsync(
        string hostUrl, string deviceName, string? keyPath = null,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        using var key = LoadOrCreateKey(keyPath);
        var client = httpClient ?? new HttpClient();
        try
        {
            var baseUrl = hostUrl.TrimEnd('/');
            var challenge = await client.GetFromJsonAsync<KeypairChallenge>(baseUrl + "/auth/keypair/challenge", cancellationToken)
                                .ConfigureAwait(false)
                            ?? throw new InvalidOperationException("The host returned no challenge.");

            var signature = key.SignData(Encoding.UTF8.GetBytes(challenge.Nonce),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

            var request = new KeypairAuthRequest(
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                challenge.Nonce,
                Convert.ToBase64String(signature),
                deviceName);

            using var response = await client.PostAsJsonAsync(baseUrl + "/auth/keypair", request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Keypair sign-in was rejected ({(int)response.StatusCode}). Is this key in the host's authorized_keys?");
            }

            return await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("The host returned no token.");
        }
        finally
        {
            if (httpClient is null)
            {
                client.Dispose();
            }
        }
    }

    private static void TryRestrictPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
        }
        catch
        {
            // best-effort — the key is still usable if we can't tighten perms.
        }
    }
}
