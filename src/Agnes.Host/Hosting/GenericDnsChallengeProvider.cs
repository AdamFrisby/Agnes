using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The bring-your-own-domain (BYO) seam: the tiny surface a user's own authoritative-DNS API plugs into so
/// Agnes can publish the ACME TXT on a domain the user controls directly (the advanced path — you own the
/// zone, versus the DynDNS shortcut of <see cref="DuckDnsChallengeProvider"/>). Implement this against your
/// provider's API (a token + zone), or point it at a local script/hook. One reference implementation ships:
/// <see cref="CloudflareDnsTxtRecordApi"/>.
/// </summary>
public interface IDnsTxtRecordApi
{
    /// <summary>Creates/updates the TXT record <paramref name="recordName"/> to <paramref name="value"/>.</summary>
    Task UpsertTxtAsync(string recordName, string value, CancellationToken ct = default);

    /// <summary>Deletes the TXT record with the given name/value (best-effort cleanup).</summary>
    Task DeleteTxtAsync(string recordName, string value, CancellationToken ct = default);
}

/// <summary>
/// BYO-domain <see cref="IDnsChallengeProvider"/>: adapts any <see cref="IDnsTxtRecordApi"/> to the ACME
/// DNS-01 seam. This keeps the ACME wiring provider-agnostic — swap the API implementation, keep the flow.
/// </summary>
public sealed class GenericDnsChallengeProvider : IDnsChallengeProvider
{
    private readonly IDnsTxtRecordApi _api;
    private readonly ILogger<GenericDnsChallengeProvider>? _logger;

    public GenericDnsChallengeProvider(IDnsTxtRecordApi api, ILogger<GenericDnsChallengeProvider>? logger = null)
    {
        _api = api;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AddTxtRecordAsync(string recordName, string value, CancellationToken ct = default)
    {
        await _api.UpsertTxtAsync(recordName, value, ct).ConfigureAwait(false);
        _logger?.LogInformation("Published ACME challenge TXT '{Record}' via a BYO DNS provider.", recordName);
    }

    /// <inheritdoc />
    public Task RemoveTxtRecordAsync(string recordName, string value, CancellationToken ct = default)
        => _api.DeleteTxtAsync(recordName, value, ct);
}

/// <summary>Configuration for <see cref="CloudflareDnsTxtRecordApi"/> (bound from <c>Agnes:Transport:Relay:Cert:Cloudflare</c>).</summary>
public sealed record CloudflareOptions
{
    /// <summary>A scoped API token with <c>Zone.DNS:Edit</c> on the target zone.</summary>
    public required string ApiToken { get; init; }

    /// <summary>The Cloudflare zone id the record lives in.</summary>
    public required string ZoneId { get; init; }

    /// <summary>API base; overridable for tests. Defaults to the Cloudflare v4 API.</summary>
    public string BaseUrl { get; init; } = "https://api.cloudflare.com/client/v4";
}

/// <summary>
/// Reference <see cref="IDnsTxtRecordApi"/> against the Cloudflare v4 DNS API (token + zone id) — the
/// "advanced, you own the domain" path. Typed request/response DTOs, injected <see cref="HttpClient"/>. Other
/// authoritative-DNS APIs (Route53, Azure DNS, …) follow this exact shape as separate implementations.
/// </summary>
public sealed class CloudflareDnsTxtRecordApi : IDnsTxtRecordApi
{
    private readonly HttpClient _http;
    private readonly CloudflareOptions _options;

    public CloudflareDnsTxtRecordApi(HttpClient http, CloudflareOptions options)
    {
        _http = http;
        _options = options;
    }

    /// <inheritdoc />
    public async Task UpsertTxtAsync(string recordName, string value, CancellationToken ct = default)
    {
        string? existingId = await FindRecordIdAsync(recordName, value, ct).ConfigureAwait(false);
        var body = new CloudflareDnsRecord("TXT", recordName, value, 60);

        HttpResponseMessage response = existingId is null
            ? await _http.PostAsJsonAsync(RecordsUrl(), body, ct).ConfigureAwait(false)
            : await _http.PutAsJsonAsync($"{RecordsUrl()}/{existingId}", body, ct).ConfigureAwait(false);
        using (response)
        {
            await EnsureCloudflareOkAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DeleteTxtAsync(string recordName, string value, CancellationToken ct = default)
    {
        string? id = await FindRecordIdAsync(recordName, value, ct).ConfigureAwait(false);
        if (id is null)
        {
            return; // already gone — nothing to clean up.
        }

        using HttpResponseMessage response = await _http.DeleteAsync($"{RecordsUrl()}/{id}", ct).ConfigureAwait(false);
        await EnsureCloudflareOkAsync(response, ct).ConfigureAwait(false);
    }

    private string RecordsUrl() => $"{_options.BaseUrl}/zones/{_options.ZoneId}/dns_records";

    private async Task<string?> FindRecordIdAsync(string recordName, string value, CancellationToken ct)
    {
        string url = $"{RecordsUrl()}?type=TXT&name={Uri.EscapeDataString(recordName)}&content={Uri.EscapeDataString(value)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        CloudflareListResponse? listed = await ReadCloudflareListAsync(response, ct).ConfigureAwait(false);
        return listed?.Result?.FirstOrDefault()?.Id;
    }

    private async Task<CloudflareListResponse?> ReadCloudflareListAsync(HttpResponseMessage response, CancellationToken ct)
    {
        CloudflareListResponse? parsed = await response.Content.ReadFromJsonAsync<CloudflareListResponse>(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || parsed is { Success: false })
        {
            throw new InvalidOperationException(
                $"Cloudflare DNS API call failed ({(int)response.StatusCode}). Verify " +
                "Agnes:Transport:Relay:Cert:Cloudflare:ApiToken and :ZoneId.");
        }

        return parsed;
    }

    private static async Task EnsureCloudflareOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        CloudflareResponse? parsed = await response.Content.ReadFromJsonAsync<CloudflareResponse>(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || parsed is { Success: false })
        {
            throw new InvalidOperationException(
                $"Cloudflare DNS API call failed ({(int)response.StatusCode}). Verify " +
                "Agnes:Transport:Relay:Cert:Cloudflare:ApiToken and :ZoneId.");
        }
    }

    // ---- Cloudflare v4 DTOs (external schema — typed immediately at the boundary) ----

    private sealed record CloudflareResponse([property: JsonPropertyName("success")] bool Success);

    private sealed record CloudflareListResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("result")] IReadOnlyList<CloudflareRecordId>? Result);

    private sealed record CloudflareRecordId([property: JsonPropertyName("id")] string Id);

    private sealed record CloudflareDnsRecord(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("ttl")] int Ttl);
}
