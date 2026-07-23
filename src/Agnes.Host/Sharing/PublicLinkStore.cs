using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sharing;

/// <summary>
/// The stored form of a public link. The raw token is NEVER persisted — only <paramref name="TokenHash"/> (a
/// SHA-256 hex digest) — so if this store is ever read (backup, compromise, stray log) it leaks nothing usable;
/// a lost link is reissued, not recovered. A public link is view-only by construction: this record has no level
/// or approval field to abuse. <paramref name="RevokedAt"/> is a permanent transition — a revoked link can never
/// validate again.
/// </summary>
public sealed record PublicLinkRecord(
    string SessionId,
    string TokenHash,
    PublicLinkOptions Options,
    int UseCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt = null)
{
    /// <summary>True while the link has not been revoked. Only active links can validate.</summary>
    public bool IsActive => RevokedAt is null;
}

/// <summary>The outcome of validating a presented public-link token against the store.</summary>
public enum PublicLinkValidation
{
    /// <summary>Valid: the token matched an active, unexpired, under-limit link (use count was incremented).</summary>
    Valid,

    /// <summary>No active link exists for the session, or the presented token did not match its hash.</summary>
    NotFound,

    /// <summary>The link exists but its expiry has elapsed.</summary>
    Expired,

    /// <summary>The link exists but its maximum use count has already been reached.</summary>
    UsesExhausted,
}

/// <summary>
/// The host's ledger of public view links, persisted to <c>~/.agnes/public-links.json</c>. At most one active
/// link exists per session — reissuing supersedes (and thereby invalidates) the prior link. Tokens are minted
/// with <see cref="RandomNumberGenerator"/> and stored only as a SHA-256 hash; validation is a fixed-time hash
/// comparison. Expiry and max-uses are honoured on every validation, driven by an injected
/// <see cref="TimeProvider"/> so tests can advance the clock deterministically. No bespoke crypto — BCL only.
/// </summary>
public sealed class PublicLinkStore
{
    /// <summary>The file name under the data directory.</summary>
    public const string FileName = "public-links.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger<PublicLinkStore>? _logger;
    private readonly Dictionary<string, PublicLinkRecord> _bySession = new(StringComparer.Ordinal);

    /// <param name="directory">Directory to persist under (production passes <c>~/.agnes</c>). Null/blank keeps
    /// the store in-memory only — used by tests.</param>
    public PublicLinkStore(string? directory = null, TimeProvider? time = null, ILogger<PublicLinkStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
        _time = time ?? TimeProvider.System;
        _logger = logger;
        Load();
    }

    /// <summary>The result of minting a link: the stored record plus the raw token, which is returned to the
    /// caller exactly once and never persisted.</summary>
    public sealed record MintResult(PublicLinkRecord Record, string RawToken);

    /// <summary>Mints a fresh token, stores only its hash (superseding any prior link for the session), and
    /// returns the record plus the one-time raw token.</summary>
    public MintResult Create(string sessionId, PublicLinkOptions options)
    {
        var rawToken = GenerateToken();
        var record = new PublicLinkRecord(
            SessionId: sessionId,
            TokenHash: Hash(rawToken),
            Options: options,
            UseCount: 0,
            CreatedAt: _time.GetUtcNow(),
            RevokedAt: null);

        lock (_gate)
        {
            _bySession[sessionId] = record;
            Persist();
        }

        return new MintResult(record, rawToken);
    }

    /// <summary>The active link for a session, or null. Never exposes a raw token (there is none to expose).</summary>
    public PublicLinkRecord? FindActive(string sessionId)
    {
        lock (_gate)
        {
            return _bySession.TryGetValue(sessionId, out var r) && r.IsActive ? r : null;
        }
    }

    /// <summary>
    /// Validates a presented raw token against the session's active link. On <see cref="PublicLinkValidation.Valid"/>
    /// the use count is incremented and persisted atomically under the lock, so a link with <c>MaxUses = n</c>
    /// admits exactly n opens. Expiry is checked against the injected clock. A missing/revoked link, or a token
    /// whose hash doesn't match, is <see cref="PublicLinkValidation.NotFound"/> — the same answer either way, so a
    /// probe can't distinguish "wrong token" from "no link".
    /// </summary>
    public PublicLinkValidation Validate(string sessionId, string? rawToken)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return PublicLinkValidation.NotFound;
        }

        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var r) || !r.IsActive)
            {
                return PublicLinkValidation.NotFound;
            }

            if (!HashEquals(r.TokenHash, Hash(rawToken)))
            {
                return PublicLinkValidation.NotFound;
            }

            if (r.Options.Expiry is { } expiry && _time.GetUtcNow() - r.CreatedAt >= expiry)
            {
                return PublicLinkValidation.Expired;
            }

            if (r.Options.MaxUses is { } maxUses && r.UseCount >= maxUses)
            {
                return PublicLinkValidation.UsesExhausted;
            }

            _bySession[sessionId] = r with { UseCount = r.UseCount + 1 };
            Persist();
            return PublicLinkValidation.Valid;
        }
    }

    /// <summary>Revokes a session's link immediately — a permanent transition. Returns true if an active link was
    /// revoked (idempotent-safe: revoking an already-revoked/absent link returns false).</summary>
    public bool Revoke(string sessionId)
    {
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var r) || !r.IsActive)
            {
                return false;
            }

            _bySession[sessionId] = r with { RevokedAt = _time.GetUtcNow() };
            Persist();
            return true;
        }
    }

    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool HashEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<PublicLinkRecord>>(File.ReadAllText(_path), Options);
            foreach (var r in records ?? [])
            {
                if (!string.IsNullOrWhiteSpace(r.SessionId))
                {
                    _bySession[r.SessionId] = r;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load public-link ledger from {Path}; starting empty.", _path);
            _bySession.Clear();
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(_bySession.Values.ToArray(), Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist public-link ledger to {Path}.", _path);
        }
    }
}
