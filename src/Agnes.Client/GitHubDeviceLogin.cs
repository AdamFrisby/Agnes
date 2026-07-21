using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>A started GitHub device-flow login: show <see cref="UserCode"/> and open <see cref="VerificationUri"/>.</summary>
public sealed record GitHubDeviceCode(
    string DeviceCode, string UserCode, string VerificationUri, int Interval, int ExpiresIn);

/// <summary>
/// Signs a client in via GitHub's OAuth <b>Device Authorization Grant</b> — no callback URL, so it works
/// on every client head. <see cref="StartAsync"/> returns the code to show the user; <see cref="CompleteAsync"/>
/// polls GitHub until the user authorizes, then exchanges the GitHub token with the host for an Agnes device
/// token. Mirrors <see cref="DevicePairing"/>: the returned token is what the caller stores and connects with.
/// </summary>
public static class GitHubDeviceLogin
{
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string Scope = "read:user read:org";

    /// <summary>Begins the flow with the host's public client id; returns the user code + verification URL.</summary>
    public static async Task<GitHubDeviceCode> StartAsync(
        string clientId, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? NewJsonClient();
        try
        {
            using var response = await client.PostAsync(DeviceCodeUrl,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = Scope }),
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var dto = await response.Content.ReadFromJsonAsync<DeviceCodeDto>(cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("GitHub returned no device code.");
            return new GitHubDeviceCode(dto.DeviceCode ?? "", dto.UserCode ?? "", dto.VerificationUri ?? "",
                dto.Interval <= 0 ? 5 : dto.Interval, dto.ExpiresIn <= 0 ? 900 : dto.ExpiresIn);
        }
        finally
        {
            if (httpClient is null) { client.Dispose(); }
        }
    }

    /// <summary>Polls GitHub until the user authorizes (or it expires), then exchanges for an Agnes token.</summary>
    public static async Task<PairResponse> CompleteAsync(
        string hostUrl, string clientId, GitHubDeviceCode code, string deviceName,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? NewJsonClient();
        try
        {
            var accessToken = await PollForTokenAsync(client, clientId, code, cancellationToken).ConfigureAwait(false);

            using var exchange = await client.PostAsJsonAsync(
                hostUrl.TrimEnd('/') + "/auth/github/exchange",
                new GitHubExchangeRequest(accessToken, deviceName), cancellationToken).ConfigureAwait(false);
            if (!exchange.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The host rejected this GitHub account ({(int)exchange.StatusCode}). It may not be on the allowlist.");
            }

            return await exchange.Content.ReadFromJsonAsync<PairResponse>(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("The host returned no token.");
        }
        finally
        {
            if (httpClient is null) { client.Dispose(); }
        }
    }

    private static async Task<string> PollForTokenAsync(
        HttpClient client, string clientId, GitHubDeviceCode code, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.Interval));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            using var response = await client.PostAsync(TokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = code.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }),
                cancellationToken).ConfigureAwait(false);

            var dto = await response.Content.ReadFromJsonAsync<TokenDto>(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(dto?.AccessToken))
            {
                return dto!.AccessToken!;
            }

            switch (dto?.Error)
            {
                case "authorization_pending":
                    break; // keep waiting
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case "access_denied":
                    throw new InvalidOperationException("GitHub authorization was denied.");
                case "expired_token":
                    throw new InvalidOperationException("The GitHub sign-in code expired — try again.");
                default:
                    if (dto?.Error is { } err)
                    {
                        throw new InvalidOperationException($"GitHub sign-in failed: {err}.");
                    }

                    break;
            }
        }

        throw new InvalidOperationException("Timed out waiting for GitHub authorization.");
    }

    private static HttpClient NewJsonClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json"); // else GitHub replies form-encoded
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Agnes");
        return client;
    }

    private sealed record DeviceCodeDto(
        [property: JsonPropertyName("device_code")] string? DeviceCode,
        [property: JsonPropertyName("user_code")] string? UserCode,
        [property: JsonPropertyName("verification_uri")] string? VerificationUri,
        [property: JsonPropertyName("interval")] int Interval,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record TokenDto(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("error")] string? Error);
}
