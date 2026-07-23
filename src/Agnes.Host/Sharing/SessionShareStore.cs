using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sharing;

/// <summary>
/// A single active direct-share record: recipient <paramref name="RecipientId"/> may access session
/// <paramref name="SessionId"/> at <paramref name="Level"/>, optionally with the orthogonal right to answer that
/// session's tool-permission prompts (<paramref name="AllowPermissionApprovals"/>). Revocation is a permanent
/// state transition — <paramref name="RevokedAt"/> is stamped and the record kept for audit, but every
/// authorization read filters to active records, so a revoked share can never again authorize anything (mirrors
/// collaboration/01's <see cref="AccessGrant"/>). Carries no secret.
/// </summary>
public sealed record SessionShareRecord(
    string SessionId,
    string RecipientId,
    SessionAccessLevel Level,
    bool AllowPermissionApprovals,
    DateTimeOffset SharedAt,
    string SharedByDevice,
    DateTimeOffset? RevokedAt = null)
{
    /// <summary>True while the share has not been revoked. Only active shares can authorize access.</summary>
    public bool IsActive => RevokedAt is null;

    /// <summary>The secret-free wire/domain projection.</summary>
    public SessionShare ToShare() => new(SessionId, RecipientId, Level, AllowPermissionApprovals);
}

/// <summary>
/// The host's ledger of direct session shares, persisted to <c>~/.agnes/session-shares.json</c>. Mirrors the
/// other host stores (single lock, atomic tmp-move, load-tolerant) and the collaboration/01 <c>GrantStore</c>:
/// a share IS a session-scoped, revocable grant with three levels plus the permission-approval toggle. At most
/// one active share exists per (session, recipient) — re-sharing supersedes the prior one. State comes from an
/// injected <see cref="TimeProvider"/> so stamps are deterministic under test.
/// </summary>
public sealed class SessionShareStore
{
    /// <summary>The file name under the data directory.</summary>
    public const string FileName = "session-shares.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionShareStore>? _logger;

    // Keyed by (sessionId, recipientId) so at most one record exists per pair; the map holds the latest state
    // (active or revoked) for that pair.
    private readonly Dictionary<(string Session, string Recipient), SessionShareRecord> _byPair = new();

    /// <param name="directory">Directory to persist under (production passes <c>~/.agnes</c>). Null/blank keeps
    /// the store in-memory only — used by tests.</param>
    public SessionShareStore(string? directory = null, TimeProvider? time = null, ILogger<SessionShareStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
        _time = time ?? TimeProvider.System;
        _logger = logger;
        Load();
    }

    /// <summary>Creates (or supersedes) the active share for a (session, recipient) pair and returns it.</summary>
    public SessionShareRecord Share(string sessionId, string recipientId, SessionAccessLevel level, bool allowPermissionApprovals, string sharedByDevice)
    {
        var record = new SessionShareRecord(
            SessionId: sessionId,
            RecipientId: recipientId,
            Level: level,
            AllowPermissionApprovals: allowPermissionApprovals,
            SharedAt: _time.GetUtcNow(),
            SharedByDevice: sharedByDevice,
            RevokedAt: null);

        lock (_gate)
        {
            _byPair[(sessionId, recipientId)] = record;
            Persist();
        }

        return record;
    }

    /// <summary>The active (non-revoked) share for this (session, recipient), or null.</summary>
    public SessionShareRecord? FindActive(string sessionId, string recipientId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(recipientId))
        {
            return null;
        }

        lock (_gate)
        {
            return _byPair.TryGetValue((sessionId, recipientId), out var r) && r.IsActive ? r : null;
        }
    }

    /// <summary>
    /// The active share covering <paramref name="candidateRecipientIds"/> on <paramref name="sessionId"/> — the
    /// most-privileged one when a caller matches by more than one identity (device id and GitHub login). Null
    /// when none of the identities has an active share. This is the covering-set an authorization read consults.
    /// </summary>
    public SessionShareRecord? FindActiveForAny(string sessionId, IEnumerable<string> candidateRecipientIds)
    {
        lock (_gate)
        {
            SessionShareRecord? best = null;
            foreach (var id in candidateRecipientIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (_byPair.TryGetValue((sessionId, id), out var r) && r.IsActive && (best is null || r.Level > best.Level))
                {
                    best = r;
                }
            }

            return best;
        }
    }

    /// <summary>Active shares on a session, newest first — the collaborator list for a manage/UI view.</summary>
    public IReadOnlyList<SessionShareRecord> ListActiveForSession(string sessionId)
    {
        lock (_gate)
        {
            return _byPair.Values
                .Where(r => r.IsActive && string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                .OrderByDescending(r => r.SharedAt)
                .ToArray();
        }
    }

    /// <summary>Revokes the active share for a (session, recipient) — immediate and permanent. Returns the
    /// revoked record, or null if there was no active share (idempotent-safe).</summary>
    public SessionShareRecord? Revoke(string sessionId, string recipientId)
    {
        lock (_gate)
        {
            if (!_byPair.TryGetValue((sessionId, recipientId), out var r) || !r.IsActive)
            {
                return null;
            }

            var revoked = r with { RevokedAt = _time.GetUtcNow() };
            _byPair[(sessionId, recipientId)] = revoked;
            Persist();
            return revoked;
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<SessionShareRecord>>(File.ReadAllText(_path), Options);
            foreach (var r in records ?? [])
            {
                if (!string.IsNullOrWhiteSpace(r.SessionId) && !string.IsNullOrWhiteSpace(r.RecipientId))
                {
                    _byPair[(r.SessionId, r.RecipientId)] = r;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load session-share ledger from {Path}; starting empty.", _path);
            _byPair.Clear();
        }
    }

    // Caller holds _gate.
    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byPair.Values.ToArray(), Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist session-share ledger to {Path}.", _path);
        }
    }
}
