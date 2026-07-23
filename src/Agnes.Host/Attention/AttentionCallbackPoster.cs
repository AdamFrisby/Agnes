using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Attention;

/// <summary>The body POSTed to a caller's callback URL. <c>Kind</c> distinguishes a real answer
/// (<c>"answer"</c>, with <c>Answer</c> set) from an expiry notification (<c>"timeout"</c>, <c>Answer</c> null),
/// so a caller can tell "a human chose X" apart from "nobody answered in time". snake_case on the wire.</summary>
public sealed record AttentionCallbackPayload(
    string RequestId,
    string Source,
    string Question,
    string Kind,
    string? Answer);

/// <summary>
/// Delivers an attention-request outcome to the caller's callback URL over HTTP, retrying transient failures
/// with backoff up to a bounded attempt count, then giving up (the answer stays available via polling). The
/// clock/backoff wait is an injected async delay so tests run with no real delays; the <see cref="HttpClient"/>
/// is injected so a stub handler can be substituted offline. It never throws — a total failure is a
/// <c>false</c> return, logged.
/// </summary>
public sealed class AttentionCallbackPoster
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly HttpClient _http;
    private readonly int _maxAttempts;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<AttentionCallbackPoster>? _logger;

    /// <param name="http">The HTTP client used for the POST (inject a stub handler in tests).</param>
    /// <param name="maxAttempts">Total attempts before giving up (bounded; must be ≥ 1).</param>
    /// <param name="backoff">Wait before attempt N+1 given the just-failed attempt number (1-based).</param>
    /// <param name="delay">How to wait — defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>;
    /// tests pass a no-op to avoid real time.</param>
    public AttentionCallbackPoster(
        HttpClient http,
        int maxAttempts = 5,
        Func<int, TimeSpan>? backoff = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger<AttentionCallbackPoster>? logger = null)
    {
        _http = http;
        _maxAttempts = Math.Max(1, maxAttempts);
        _backoff = backoff ?? (attempt => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1))));
        _delay = delay ?? Task.Delay;
        _logger = logger;
    }

    /// <summary>Attempts to deliver <paramref name="payload"/> to <paramref name="url"/>, retrying with
    /// backoff up to the attempt cap. Returns true on the first success, false once the cap is exhausted.</summary>
    public async Task<bool> PostAsync(string url, AttentionCallbackPayload payload, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                using var response = await _http.PostAsync(url, JsonContent.Create(payload, options: Json), cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                _logger?.LogWarning("Attention callback to {Url} for {RequestId} returned {Status} (attempt {Attempt}/{Max}).",
                    url, payload.RequestId, (int)response.StatusCode, attempt, _maxAttempts);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "Attention callback to {Url} for {RequestId} failed (attempt {Attempt}/{Max}).",
                    url, payload.RequestId, attempt, _maxAttempts);
            }

            if (attempt < _maxAttempts)
            {
                await _delay(_backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        _logger?.LogWarning("Gave up delivering attention callback to {Url} for {RequestId} after {Max} attempts; answer remains pollable.",
            url, payload.RequestId, _maxAttempts);
        return false;
    }
}
