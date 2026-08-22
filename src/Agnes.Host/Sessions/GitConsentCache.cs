using System.Collections.Concurrent;

namespace Agnes.Host.Sessions;

/// <summary>How a git-consent card ended. The third case is the one that matters: an unanswered card is
/// treated as a refusal <b>for this request</b> (so a push can't hang the broker) but must never be
/// remembered as one.</summary>
public enum GitConsentOutcome
{
    /// <summary>The user allowed it.</summary>
    Allowed,

    /// <summary>The user refused it.</summary>
    Denied,

    /// <summary>Nobody answered before the card expired.</summary>
    Unanswered,
}

/// <summary>
/// Ask-once-per-repository consent for a sandboxed agent's GitHub access. Git asks for a credential the
/// same way for clone, fetch and push and for any repo, so we prompt the user the first time the agent
/// touches a given repository and remember that decision for the rest of the session — no nagging on every
/// fetch/push. "Trust" mode auto-allows every repo without prompting.
/// </summary>
/// <remarks>
/// Only a decision a person actually made is remembered. An unanswered card denies the request in front of
/// it and is then forgotten, so the next attempt asks again. Caching a timeout was a real fault: a user who
/// stepped away for two minutes locked the repository out for the rest of the session, was never prompted
/// again, and approving afterwards did nothing — every later request was refused instantly from cache.
/// </remarks>
public sealed class GitConsentCache
{
    private readonly ConcurrentDictionary<string, bool> _byRepo = new();

    private static string Key(string sessionId, string host, string? repo) => $"{sessionId}|{host}|{repo ?? "*"}";

    /// <summary>
    /// Decides whether the agent may use the linked account for this repo. In "Trust" mode always true.
    /// Otherwise returns the remembered decision for this (session, host, repo), or invokes
    /// <paramref name="ask"/> once and caches its result — unless nobody answered, which is not a decision.
    /// </summary>
    public async Task<bool> DecideAsync(
        string sessionId, string host, string? repo, string mode, Func<Task<GitConsentOutcome>> ask)
    {
        if (string.Equals(mode, "Trust", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var key = Key(sessionId, host, repo);
        if (_byRepo.TryGetValue(key, out var decided))
        {
            return decided;
        }

        var outcome = await ask().ConfigureAwait(false);
        if (outcome is GitConsentOutcome.Unanswered)
        {
            return false; // refuse this attempt, remember nothing — the next one asks again
        }

        var allowed = outcome is GitConsentOutcome.Allowed;
        _byRepo[key] = allowed;
        return allowed;
    }

    /// <summary>Forgets one remembered decision, so the next request asks again. Lets a user who refused
    /// (or was locked out by an old cached refusal) change their mind without restarting the session.</summary>
    public bool Reconsider(string sessionId, string host, string? repo)
        => _byRepo.TryRemove(Key(sessionId, host, repo), out _);

    /// <summary>Forget a session's consents (on close) so a re-opened session asks again.</summary>
    public void Forget(string sessionId)
    {
        foreach (var key in _byRepo.Keys.Where(k => k.StartsWith(sessionId + "|", StringComparison.Ordinal)).ToList())
        {
            _byRepo.TryRemove(key, out _);
        }
    }
}
