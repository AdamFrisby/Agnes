using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// Approval pairing from the <em>new</em> device's side: ask to be let in, show the digits, wait.
///
/// This is the path for a device that can't scan — no camera, or two screens that can't see each other.
/// The device presents the public key it will be known by, then displays digits it derives itself from
/// that key and the request id. The human compares those digits with the ones shown on an
/// already-trusted device before approving, which is what stops an attacker's simultaneous request
/// being approved in place of yours.
/// </summary>
public static class PairingApproval
{
    /// <summary>
    /// Asks a host to let this device in, returning the request to poll and the digits to display.
    ///
    /// The returned <see cref="PairApprovalPending.VerificationCode"/> is re-derived locally rather than
    /// trusted from the response — if the host returned different digits from the ones this key and
    /// request id imply, comparing screens would prove nothing.
    /// </summary>
    public static async Task<PairApprovalPending> RequestAsync(
        string hostUrl, string publicKey, string deviceName,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            using var response = await client
                .PostAsJsonAsync(hostUrl.TrimEnd('/') + "/pair/request",
                    new PairApprovalRequest(publicKey, deviceName), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new PairingRefusedException(response.StatusCode, response.StatusCode switch
                {
                    System.Net.HttpStatusCode.TooManyRequests =>
                        "Too many devices are already waiting to be approved. Try again shortly.",
                    System.Net.HttpStatusCode.NotFound =>
                        "This host is too old to approve devices — pair with a QR or a code instead.",
                    _ => $"The host refused the request ({(int)response.StatusCode}).",
                });
            }

            var pending = await response.Content.ReadFromJsonAsync<PairApprovalPending>(cancellationToken)
                              .ConfigureAwait(false)
                          ?? throw new PairingRefusedException(response.StatusCode, "The host returned no request.");

            return pending with { VerificationCode = PairVerification.Derive(publicKey, pending.RequestId) };
        }
        finally
        {
            if (httpClient is null)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Checks whether a human has decided yet. The token arrives exactly once, on the first
    /// poll after approval.</summary>
    public static async Task<PairApprovalStatus> PollAsync(
        string hostUrl, string requestId,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            return await client
                       .GetFromJsonAsync<PairApprovalStatus>(
                           hostUrl.TrimEnd('/') + "/pair/request/" + Uri.EscapeDataString(requestId),
                           cancellationToken)
                       .ConfigureAwait(false)
                   ?? new PairApprovalStatus(PairApprovalState.Unknown);
        }
        catch (HttpRequestException)
        {
            // A dropped poll is not a decision — keep waiting rather than reporting a denial.
            return new PairApprovalStatus(PairApprovalState.Pending);
        }
        finally
        {
            if (httpClient is null)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// The SPKI public key this device identifies itself with, as base64. Reuses the same client keypair
    /// as offline keypair sign-in, so a device has one identity rather than one per mechanism.
    /// </summary>
    public static string LocalPublicKey(string? keyPath = null)
    {
        using var key = KeypairEnrollment.LoadOrCreateKey(keyPath);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
}
