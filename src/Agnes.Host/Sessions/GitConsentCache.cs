using System.Collections.Concurrent;

namespace Agnes.Host.Sessions;

/// <summary>
/// Ask-once-per-repository consent for a sandboxed agent's GitHub access. Git asks for a credential the
/// same way for clone, fetch and push and for any repo, so we prompt the user the first time the agent
/// touches a given repository and remember that decision (allow or deny) for the rest of the session —
/// no nagging on every fetch/push. "Trust" mode auto-allows every repo without prompting.
/// </summary>
public sealed class GitConsentCache
{
    private readonly ConcurrentDictionary<string, bool> _byRepo = new();

    private static string Key(string sessionId, string host, string? repo) => $"{sessionId}|{host}|{repo ?? "*"}";

    /// <summary>
    /// Decides whether the agent may use the linked account for this repo. In "Trust" mode always true.
    /// Otherwise returns the remembered decision for this (session, host, repo), or invokes
    /// <paramref name="ask"/> once and caches its result.
    /// </summary>
    public async Task<bool> DecideAsync(string sessionId, string host, string? repo, string mode, Func<Task<bool>> ask)
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

        var allowed = await ask().ConfigureAwait(false);
        _byRepo[key] = allowed;
        return allowed;
    }

    /// <summary>Forget a session's consents (on close) so a re-opened session asks again.</summary>
    public void Forget(string sessionId)
    {
        foreach (var key in _byRepo.Keys.Where(k => k.StartsWith(sessionId + "|", StringComparison.Ordinal)).ToList())
        {
            _byRepo.TryRemove(key, out _);
        }
    }
}
