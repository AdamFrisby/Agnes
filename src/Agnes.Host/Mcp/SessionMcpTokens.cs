using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Agnes.Host.Mcp;

/// <summary>
/// Per-session bearer tokens for Agnes's own MCP endpoint, as offered to a sandboxed agent.
/// </summary>
/// <remarks>
/// The token <b>is</b> the session's identity to the tool layer: an agent presenting one is that session and
/// cannot claim to be another, so tools like <c>arm_goal</c> need no session argument and cannot be pointed
/// at somebody else's work. That is the whole reason not to hand a sandboxed agent a device token — a device
/// token carries the authority of a paired human across every session on the host.
///
/// A session's token is stable for as long as the session is provisioned (re-issuing on every re-stamp would
/// invalidate a config the running agent already read) and is revoked when the session goes away. In-memory
/// only, like the MCP forward's grants: a token that outlived a host restart would authorize a caller whose
/// session no longer exists.
/// </remarks>
public sealed class SessionMcpTokens
{
    private readonly ConcurrentDictionary<string, string> _tokenBySession = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionByToken = new(StringComparer.Ordinal);

    /// <summary>The session's token, minting one on first use. Stable across re-provisioning.</summary>
    public string Issue(string sessionId)
    {
        if (_tokenBySession.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        // Racing callers must agree on one token, or the loser writes a config nobody will honour.
        var winner = _tokenBySession.GetOrAdd(sessionId, token);
        _sessionByToken[winner] = sessionId;
        return winner;
    }

    /// <summary>The session a token belongs to, or null when it isn't one of ours.</summary>
    public string? SessionFor(string? token)
        => token is { Length: > 0 } t && _sessionByToken.TryGetValue(t, out var sessionId) ? sessionId : null;

    /// <summary>Revokes a session's token (on close), so a leaked config can't outlive the session.</summary>
    public void Revoke(string sessionId)
    {
        if (_tokenBySession.TryRemove(sessionId, out var token))
        {
            _sessionByToken.TryRemove(token, out _);
        }
    }
}
