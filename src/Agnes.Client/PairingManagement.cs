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

    /// <summary>
    /// Approves a request — only after a human has compared the six digits on both screens.
    ///
    /// <paramref name="role"/> is the role the device is admitted with. Only an Owner may grant
    /// <see cref="DeviceRole.Owner"/>; a host that predates roles ignores the body and admits as it
    /// always did, which is why the default here is the cautious one.
    /// </summary>
    public static Task ApproveAsync(
        string hostUrl, string token, string requestId, DeviceRole role = DeviceRole.Member,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SendAsync<object>(
            HttpMethod.Post,
            hostUrl.TrimEnd('/') + "/pair/approve/" + Uri.EscapeDataString(requestId),
            token, httpClient, cancellationToken, new PairApprovalDecision(role));

    /// <summary>
    /// What this device itself is on the host (<c>GET /devices/me</c>) — the answer to "why does this
    /// device see nothing". Null on a host too old to say, which callers read as "explain nothing".
    /// </summary>
    public static async Task<DeviceInfo?> MeAsync(
        string hostUrl, string token, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendAsync<DeviceInfo>(
                HttpMethod.Get, hostUrl.TrimEnd('/') + "/devices/me", token, httpClient, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Asking who we are is never worth failing a connect over.
            return null;
        }
    }

    /// <summary>Promotes or demotes a device (<c>PUT /devices/{id}/role</c>, Owner only). False when the
    /// host refused: too old, not an owner, or this would demote the last owner.</summary>
    public static Task<bool> SetRoleAsync(
        string hostUrl, string token, string deviceId, DeviceRole role,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SendOkAsync(
            HttpMethod.Put,
            $"{hostUrl.TrimEnd('/')}/devices/{Uri.EscapeDataString(deviceId)}/role",
            token, httpClient, cancellationToken, new DeviceRoleRequest(role));

    /// <summary>Removes devices not seen for <paramref name="unusedForDays"/> days
    /// (<c>POST /devices/prune</c>, Owner only). False when the host refused.</summary>
    public static Task<bool> PruneAsync(
        string hostUrl, string token, int unusedForDays = 30,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SendOkAsync(
            HttpMethod.Post, hostUrl.TrimEnd('/') + "/devices/prune",
            token, httpClient, cancellationToken, new DevicePruneRequest(unusedForDays));

    private static async Task<bool> SendOkAsync(
        HttpMethod method, string url, string token, HttpClient? httpClient,
        CancellationToken cancellationToken, object body)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = JsonContent.Create(body, body.GetType()),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        finally
        {
            if (httpClient is null)
            {
                client.Dispose();
            }
        }
    }

    public static Task DenyAsync(
        string hostUrl, string token, string requestId,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SendAsync<object>(
            HttpMethod.Post,
            hostUrl.TrimEnd('/') + "/pair/deny/" + Uri.EscapeDataString(requestId),
            token, httpClient, cancellationToken);

    private static async Task<T?> SendAsync<T>(
        HttpMethod method, string url, string token, HttpClient? httpClient,
        CancellationToken cancellationToken, object? body = null)
        where T : class
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

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

            return await response.Content.ReadFromJsonAsync<T>(DeviceRoleJson.Read, cancellationToken).ConfigureAwait(false);
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
