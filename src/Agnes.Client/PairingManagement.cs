using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// The stronger pairing paths, from an already-paired device's side: mint a QR grant for a new device,
/// and see/answer requests from devices asking to be vouched for.
///
/// All of these require this client's own device token — that authentication *is* the vouching. A host
/// that predates them answers 404, which surfaces as an empty list or a null grant rather than an
/// error, so a client can offer the feature without knowing the host's version up front.
/// </summary>
public static class PairingManagement
{
    /// <summary>
    /// Mints a one-time 256-bit grant to encode as a QR, optionally carrying the session to open once
    /// the new device has paired. Null when the host is too old to offer grants, or has no
    /// externally-reachable address to advertise.
    /// </summary>
    public static async Task<PairingGrant?> MintGrantAsync(
        string hostUrl, string token, string? sessionId = null,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var url = hostUrl.TrimEnd('/') + "/pair/grant"
            + (string.IsNullOrWhiteSpace(sessionId) ? string.Empty : "?session=" + Uri.EscapeDataString(sessionId));

        return await SendAsync<PairingGrant>(HttpMethod.Post, url, token, httpClient, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Destroys a grant before it expires — what hiding a displayed QR calls, so a secret that was on a
    /// screen stops working the moment it stops being visible rather than lingering for its full life.
    /// </summary>
    public static async Task RevokeGrantAsync(
        string hostUrl, string token, string secret,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => await SendAsync<object>(
            HttpMethod.Delete,
            hostUrl.TrimEnd('/') + "/pair/grant/" + Uri.EscapeDataString(secret),
            token, httpClient, cancellationToken).ConfigureAwait(false);

    /// <summary>Devices asking to be vouched for. Empty on a host that predates approval pairing.</summary>
    public static async Task<IReadOnlyList<PendingPairApproval>> PendingAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => await SendAsync<List<PendingPairApproval>>(
               HttpMethod.Get, hostUrl.TrimEnd('/') + "/pair/pending", token, httpClient, cancellationToken)
               .ConfigureAwait(false)
           ?? [];

    /// <summary>Approves a request — only after a human has compared the six digits on both screens.</summary>
    public static Task ApproveAsync(
        string hostUrl, string token, string requestId,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SendAsync<object>(
            HttpMethod.Post,
            hostUrl.TrimEnd('/') + "/pair/approve/" + Uri.EscapeDataString(requestId),
            token, httpClient, cancellationToken);

    public static Task DenyAsync(
        string hostUrl, string token, string requestId,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SendAsync<object>(
            HttpMethod.Post,
            hostUrl.TrimEnd('/') + "/pair/deny/" + Uri.EscapeDataString(requestId),
            token, httpClient, cancellationToken);

    private static async Task<T?> SendAsync<T>(
        HttpMethod method, string url, string token, HttpClient? httpClient, CancellationToken cancellationToken)
        where T : class
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A host that predates these endpoints 404s; that's "not offered", not "broken".
                return null;
            }

            if (typeof(T) == typeof(object) || response.Content.Headers.ContentLength is 0 or null)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
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
