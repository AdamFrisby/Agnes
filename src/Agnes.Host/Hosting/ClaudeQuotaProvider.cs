using System.Net;
using System.Net.Http.Headers;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The first REAL <see cref="IQuotaReportingProvider"/>: probes Anthropic's OAuth usage endpoint
/// (<c>https://api.anthropic.com/api/oauth/usage</c>) with the host's Claude OAuth access token and maps the
/// response onto a <see cref="QuotaSnapshot"/>. Also an <see cref="IConnectedServiceProvider"/> (id "claude")
/// so it registers in the same plugin point the <see cref="QuotaService"/> routes through — a
/// <see cref="ConnectedServiceProfile"/> whose <see cref="ConnectedServiceProfile.ProviderId"/> is "claude"
/// resolves to it.
/// </summary>
/// <remarks>
/// <para>Resilience is adapted from CodeyBox's <c>ClaudeQuotaProbe</c>: a transient probe failure (network
/// error, timeout, 5xx, 408, 429) is retried with exponential backoff; on continued transient failure the
/// last known-good snapshot is RETAINED (stale — its original <see cref="QuotaSnapshot.FetchedAt"/> is the
/// honest staleness indicator) until either <see cref="ClaudeQuotaResilienceOptions.MaxConsecutiveFailures"/>
/// probes have failed or the retained reading exceeds
/// <see cref="ClaudeQuotaResilienceOptions.MaxStaleness"/> — only then does it fall to null so the caller
/// renders "unavailable". A permanent failure (a non-retryable 4xx / unparseable body) or a missing token
/// discards any retained reading and returns null. It never throws for a probe failure.</para>
/// <para>The host's <see cref="QuotaService"/> caches the result per profile behind its own staleness window,
/// so this provider does not re-cache; the retain-last-good behaviour here is only about a probe failing
/// mid-refresh, not the UI-paint cadence.</para>
/// </remarks>
public sealed class ClaudeQuotaProvider : IConnectedServiceProvider, IQuotaReportingProvider
{
    /// <summary>The provider id a Claude connected-service profile must carry to route here.</summary>
    public const string ProviderId = "claude";

    internal const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const int MaxResponseBytes = 256 * 1024;

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string?>> _tokenSource;
    private readonly TimeProvider _time;
    private readonly ClaudeQuotaResilienceOptions _options;
    private readonly ILogger<ClaudeQuotaProvider>? _logger;

    private readonly object _gate = new();
    private (QuotaSnapshot Snapshot, DateTimeOffset CapturedAt)? _lastGood;
    private int _consecutiveTransientFailures;

    /// <param name="httpClient">The client used for the usage GET (injected so tests stub the handler).</param>
    /// <param name="tokenSource">
    /// Reads the current Claude OAuth access token (see <see cref="ClaudeOAuthTokenSource"/>), or null when
    /// there is none. Production wires this to the real credentials file; tests inject a fixed token.
    /// </param>
    /// <param name="time">Clock for backoff delays and stamping <see cref="QuotaSnapshot.FetchedAt"/>.</param>
    /// <param name="options">Retry/staleness tuning; defaults are sane for a slow-changing usage endpoint.</param>
    public ClaudeQuotaProvider(
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>> tokenSource,
        TimeProvider? time = null,
        ClaudeQuotaResilienceOptions? options = null,
        ILogger<ClaudeQuotaProvider>? logger = null)
    {
        _http = httpClient;
        _tokenSource = tokenSource;
        _time = time ?? TimeProvider.System;
        _options = options ?? new ClaudeQuotaResilienceOptions();
        _logger = logger;
    }

    public string Id => ProviderId;

    public string DisplayName => "Claude";

    /// <summary>
    /// Materialises the Claude OAuth access token as a short-lived connected-service credential. Only the
    /// access token is ever handed out — never the refresh token. Throws when no token is available so a
    /// session start fails loudly rather than launching unauthenticated.
    /// </summary>
    public async Task<ResolvedServiceCredential> ResolveAsync(ConnectedServiceProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var token = await _tokenSource(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(
                $"Connected-service profile '{profile.Id}' has no Claude OAuth credential available on this host.");
        }

        return new ResolvedServiceCredential(
            Value: token,
            ExpiresAt: null,
            Env: new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token });
    }

    /// <summary>
    /// Reads the current Claude usage quota, or null when it genuinely cannot be reported (no token, or a
    /// persistent/permanent probe failure with no retainable last-good reading). Never throws for a probe
    /// failure. A transient failure after a prior success returns the retained stale snapshot instead of null.
    /// </summary>
    public async Task<QuotaSnapshot?> GetQuotaAsync(string profileId, CancellationToken ct = default)
    {
        string? token;
        try
        {
            token = await _tokenSource(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            token = null;
        }

        if (string.IsNullOrEmpty(token))
        {
            // No credential: nothing to read and nothing worth retaining — discard any stale reading.
            lock (_gate)
            {
                _lastGood = null;
                _consecutiveTransientFailures = 0;
            }

            return null;
        }

        ProbeResult result;
        try
        {
            result = await FetchWithResilienceAsync(token, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Belt-and-braces: the contract is "never throw for a probe failure".
            _logger?.LogDebug(ex, "Claude quota probe threw unexpectedly; treating as transient.");
            result = ProbeResult.Transient;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            switch (result.Kind)
            {
                case ProbeKind.Success:
                    _lastGood = (result.Snapshot!, now);
                    _consecutiveTransientFailures = 0;
                    return result.Snapshot;

                case ProbeKind.PermanentFailure:
                    // The reading can no longer be trusted (auth/contract failure) — drop any retained one.
                    _lastGood = null;
                    _consecutiveTransientFailures = 0;
                    return null;

                default: // Transient
                    _consecutiveTransientFailures++;
                    if (_lastGood is { } lg
                        && _consecutiveTransientFailures <= _options.MaxConsecutiveFailures
                        && now - lg.CapturedAt <= _options.MaxStaleness)
                    {
                        // Retain the stale-but-honest reading; its original FetchedAt flags the staleness.
                        return lg.Snapshot;
                    }

                    _lastGood = null;
                    return null;
            }
        }
    }

    private async Task<ProbeResult> FetchWithResilienceAsync(string token, CancellationToken ct)
    {
        var totalAttempts = Math.Max(1, _options.MaxRetries + 1);
        var last = ProbeResult.Transient;

        for (var attempt = 0; attempt < totalAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var backoff = TimeSpan.FromTicks(
                    _options.RetryInitialDelay.Ticks * (long)Math.Pow(2, attempt - 1));
                await Task.Delay(backoff, _time, ct).ConfigureAwait(false);
            }

            last = await ProbeOnceAsync(token, ct).ConfigureAwait(false);
            if (last.Kind is ProbeKind.Success or ProbeKind.PermanentFailure)
            {
                return last;
            }
        }

        return last; // exhausted all attempts transiently
    }

    private async Task<ProbeResult> ProbeOnceAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            // The Authorization header carries the OAuth token — never log it.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _logger?.LogDebug("Claude quota endpoint returned {StatusCode}.", status);
                return IsTransientStatus(response.StatusCode) ? ProbeResult.Transient : ProbeResult.Permanent;
            }

            var body = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            // Do NOT log the body — it may carry account identifiers.
            var snapshot = ClaudeUsageParsing.Parse(body, _time.GetUtcNow());
            return snapshot is null ? ProbeResult.Permanent : ProbeResult.FromSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Claude quota probe failed; treating as transient.");
            return ProbeResult.Transient;
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        var body = await content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Length > MaxResponseBytes ? null : body;
    }

    private static bool IsTransientStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return (code >= 500 && code <= 599)
            || status == HttpStatusCode.RequestTimeout
            || status == HttpStatusCode.TooManyRequests;
    }

    private enum ProbeKind
    {
        Success,
        TransientFailure,
        PermanentFailure,
    }

    private readonly record struct ProbeResult(ProbeKind Kind, QuotaSnapshot? Snapshot)
    {
        public static ProbeResult FromSnapshot(QuotaSnapshot snapshot) => new(ProbeKind.Success, snapshot);
        public static readonly ProbeResult Transient = new(ProbeKind.TransientFailure, null);
        public static readonly ProbeResult Permanent = new(ProbeKind.PermanentFailure, null);
    }
}

/// <summary>
/// Retry/staleness tuning for <see cref="ClaudeQuotaProvider"/>, adapted from CodeyBox's probe resilience
/// options. Defaults suit a slow-changing usage endpoint fronted by the host's own quota cache.
/// </summary>
public sealed record ClaudeQuotaResilienceOptions
{
    /// <summary>Extra attempts after the first before a probe is recorded as a transient failure.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Base backoff before the first retry; doubles each subsequent retry.</summary>
    public TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How many consecutive transient failures may pass before a retained reading is dropped to null.</summary>
    public int MaxConsecutiveFailures { get; init; } = 3;

    /// <summary>How old a retained last-good reading may get before it is dropped to null.</summary>
    public TimeSpan MaxStaleness { get; init; } = TimeSpan.FromMinutes(30);
}
