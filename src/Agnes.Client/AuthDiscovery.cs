using System.Net.Http;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// Asks a host which sign-in methods it offers (<c>GET /auth/methods</c>) so a client shows only the
/// enabled ones. Falls back to pairing-only for older hosts (or an unreachable one), which is what every
/// host supported before this endpoint existed.
/// </summary>
public static class AuthDiscovery
{
    private static readonly AuthMethods PairingOnly = new(Pairing: true, GitHub: false, GitHubClientId: null, Keypair: false);

    public static async Task<AuthMethods> GetMethodsAsync(
        string hostUrl, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            var url = hostUrl.TrimEnd('/') + "/auth/methods";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return PairingOnly;
            }

            return await response.Content.ReadFromJsonAsync<AuthMethods>(cancellationToken).ConfigureAwait(false)
                   ?? PairingOnly;
        }
        catch
        {
            return PairingOnly; // legacy / unreachable host — assume the original pairing bootstrap.
        }
        finally
        {
            if (httpClient is null)
            {
                client.Dispose();
            }
        }
    }
}
