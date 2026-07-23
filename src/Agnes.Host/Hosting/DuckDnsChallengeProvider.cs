using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Configuration for <see cref="DuckDnsChallengeProvider"/> (bound from <c>Agnes:Transport:Relay:Cert:DuckDns</c>).</summary>
public sealed record DuckDnsOptions
{
    /// <summary>The DuckDNS sub-domain(s) to set the TXT on — the label(s) left of <c>.duckdns.org</c>,
    /// comma-separated for multiple (DuckDNS ignores any <c>_acme-challenge.</c> prefix itself).</summary>
    public required string Domains { get; init; }

    /// <summary>The DuckDNS account token that authorizes updates.</summary>
    public required string Token { get; init; }

    /// <summary>The update endpoint base; overridable for tests. Defaults to the public DuckDNS API.</summary>
    public string BaseUrl { get; init; } = "https://www.duckdns.org/update";
}

/// <summary>Typed result of a DuckDNS update call (its API replies with a plain-text <c>OK</c>/<c>KO</c>).</summary>
public sealed record DuckDnsUpdateResult(bool Ok, string RawResponse);

/// <summary>
/// A DynDNS-style <see cref="IDnsChallengeProvider"/> for DuckDNS — the canonical NAT/home choice: a free
/// dynamic-DNS host whose HTTP API sets the <c>_acme-challenge</c> TXT with a single GET
/// (<c>update?domains=&lt;sub&gt;&amp;token=&lt;t&gt;&amp;txt=&lt;value&gt;</c>). Structured so other DynDNS
/// providers (No-IP, Dynu, …) are easy additions: each is just another <see cref="IDnsChallengeProvider"/>
/// hitting its own documented TXT-set endpoint. The <see cref="HttpClient"/> is injected so it is fully
/// testable offline against a stub handler.
/// </summary>
public sealed class DuckDnsChallengeProvider : IDnsChallengeProvider
{
    private readonly HttpClient _http;
    private readonly DuckDnsOptions _options;
    private readonly ILogger<DuckDnsChallengeProvider>? _logger;

    public DuckDnsChallengeProvider(HttpClient http, DuckDnsOptions options, ILogger<DuckDnsChallengeProvider>? logger = null)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task AddTxtRecordAsync(string recordName, string value, CancellationToken ct = default)
        => UpdateAsync(txt: value, clear: false, ct);

    /// <inheritdoc />
    public Task RemoveTxtRecordAsync(string recordName, string value, CancellationToken ct = default)
        // DuckDNS clears a TXT with clear=true; recordName/value aren't needed (the domain identifies it).
        => UpdateAsync(txt: "", clear: true, ct);

    private async Task UpdateAsync(string txt, bool clear, CancellationToken ct)
    {
        // DuckDNS is a fixed, documented query shape — build it explicitly so the exact wire call is asserted.
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{_options.BaseUrl}?domains={Uri.EscapeDataString(_options.Domains)}&token={Uri.EscapeDataString(_options.Token)}&txt={Uri.EscapeDataString(txt)}&clear={(clear ? "true" : "false")}");

        DuckDnsUpdateResult result;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            string body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            response.EnsureSuccessStatusCode();
            result = new DuckDnsUpdateResult(
                Ok: body.StartsWith("OK", StringComparison.OrdinalIgnoreCase), body);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"DuckDNS did not accept the ACME TXT update for '{_options.Domains}'. Verify " +
                "Agnes:Transport:Relay:Cert:DuckDns:Domains and :Token.", ex);
        }

        if (!result.Ok)
        {
            // DuckDNS answers 'KO' (HTTP 200) for a bad token/domain — surface it as a clear, actionable error.
            throw new InvalidOperationException(
                $"DuckDNS rejected the ACME TXT update for '{_options.Domains}' (response: '{result.RawResponse}'). " +
                "Verify Agnes:Transport:Relay:Cert:DuckDns:Domains and :Token.");
        }

        _logger?.LogInformation("Published DuckDNS ACME challenge TXT for '{Domains}' (clear={Clear}).", _options.Domains, clear);
    }
}
