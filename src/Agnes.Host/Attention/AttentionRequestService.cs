using Microsoft.Extensions.Logging;

namespace Agnes.Host.Attention;

/// <summary>The outcome of recording an answer: the updated request plus the (already-running) callback
/// delivery task. The delivery task completes true on success, false once the retry cap is exhausted; it is
/// <see cref="Task.CompletedTask"/>-equivalent (always true) when the request had no callback URL. Callers
/// that must not block on delivery (the hub) ignore <see cref="CallbackDelivery"/>; tests await it.</summary>
public sealed record AttentionAnswerOutcome(AttentionRequest Request, Task<bool> CallbackDelivery);

/// <summary>
/// Coordinates the attention-request store and the outbound callback poster: creation, owner-scoped reads,
/// the answer path (record → fire the callback), and the timeout sweep (expire → distinct timeout callback).
/// This is the single branch point the design calls for — recording an answer is identical to any inbox
/// resolution; only the after-effect (a webhook POST) differs, and it lives here rather than in the inbox.
/// </summary>
public sealed class AttentionRequestService
{
    private readonly AttentionRequestStore _store;
    private readonly AttentionCallbackPoster _poster;
    private readonly TimeProvider _time;
    private readonly ILogger<AttentionRequestService>? _logger;

    public AttentionRequestService(
        AttentionRequestStore store,
        AttentionCallbackPoster poster,
        TimeProvider? time = null,
        ILogger<AttentionRequestService>? logger = null)
    {
        _store = store;
        _poster = poster;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Creates a new Pending request owned by the authenticated caller.</summary>
    public AttentionRequest Create(string ownerCallerId, string source, string question, IReadOnlyList<string> options, string? callbackUrl, int? timeoutSeconds)
        => _store.Create(ownerCallerId, source, question, options, callbackUrl, timeoutSeconds);

    /// <summary>Owner-scoped read for the polling endpoint (null if unknown or not the owner's).</summary>
    public AttentionRequest? GetForOwner(string id, string ownerCallerId) => _store.GetForOwner(id, ownerCallerId);

    /// <summary>
    /// Records a human's answer against a Pending request and — if a callback URL was supplied — starts
    /// delivering it. Returns null if the id is unknown or the request is already resolved (answered or
    /// expired), so a late answer after a timeout is refused. The callback delivery runs on its own task so
    /// the answering client isn't held for the retry/backoff window; it never faults (the poster swallows
    /// failures), and the answer is recorded regardless of whether delivery ultimately succeeds.
    /// </summary>
    public AttentionAnswerOutcome? Answer(string id, string answer, CancellationToken cancellationToken = default)
    {
        var updated = _store.TryAnswer(id, answer);
        if (updated is null)
        {
            return null;
        }

        var delivery = updated.CallbackUrl is { } url
            ? _poster.PostAsync(url, new AttentionCallbackPayload(updated.Id, updated.Source, updated.Question, "answer", answer), cancellationToken)
            : Task.FromResult(true);

        return new AttentionAnswerOutcome(updated, delivery);
    }

    /// <summary>
    /// Expires every Pending request past its timeout and, for those with a callback URL, POSTs a distinct
    /// timeout notification (kind <c>"timeout"</c>, no answer). Idempotent per request: a request already
    /// flipped by a concurrent answer is skipped. Returns the delivery tasks so a caller/test can await them.
    /// </summary>
    public IReadOnlyList<Task<bool>> SweepExpired(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var deliveries = new List<Task<bool>>();
        foreach (var candidate in _store.FindTimedOut(now))
        {
            var expired = _store.TryExpire(candidate.Id);
            if (expired is null)
            {
                continue; // raced with an answer; that outcome wins.
            }

            _logger?.LogInformation("Attention request {RequestId} ({Source}) expired after {Timeout}s.",
                expired.Id, expired.Source, expired.TimeoutSeconds);

            if (expired.CallbackUrl is { } url)
            {
                deliveries.Add(_poster.PostAsync(url, new AttentionCallbackPayload(expired.Id, expired.Source, expired.Question, "timeout", null), cancellationToken));
            }
        }

        return deliveries;
    }
}
