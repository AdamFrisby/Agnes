using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Social;

/// <summary>
/// The host's ledger of <see cref="AccessGrant"/> records, keyed by id and persisted to
/// <c>~/.agnes/grants.json</c>. Mirrors the other host stores (single lock, atomic tmp-move, load-tolerant).
/// Revocation is modelled as a permanent state transition — <see cref="Revoke"/> stamps
/// <see cref="AccessGrant.RevokedAt"/> and the record is kept for audit, but every authorization read filters
/// to active grants, so a revoked grant can never again authorize anything. State comes from an injected
/// <see cref="TimeProvider"/> so stamps are deterministic under test.
/// </summary>
public sealed class GrantStore
{
    /// <summary>The file name under the data directory.</summary>
    public const string FileName = "grants.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger<GrantStore>? _logger;
    private readonly Dictionary<string, AccessGrant> _byId = new(StringComparer.Ordinal);

    /// <param name="directory">
    /// Directory to persist the ledger under (production passes <c>~/.agnes</c>). When null or blank the store
    /// is in-memory only and never touches disk — used by tests.
    /// </param>
    public GrantStore(string? directory = null, TimeProvider? time = null, ILogger<GrantStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
        _time = time ?? TimeProvider.System;
        _logger = logger;
        Load();
    }

    /// <summary>Creates and persists a new active grant, stamped from the clock; returns the stored record.</summary>
    public AccessGrant Grant(string granteeLogin, string resource, GrantScope scope, string grantedByDevice)
    {
        var grant = new AccessGrant(
            Id: Guid.NewGuid().ToString("n"),
            GranteeLogin: granteeLogin,
            Resource: resource,
            Scope: scope,
            GrantedAt: _time.GetUtcNow(),
            GrantedByDevice: grantedByDevice,
            RevokedAt: null);

        lock (_gate)
        {
            _byId[grant.Id] = grant;
            Persist();
        }

        return grant;
    }

    /// <summary>The grant with this id, regardless of state, or null.</summary>
    public AccessGrant? Find(string id)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    /// <summary>Active (non-revoked) grants, newest first.</summary>
    public IReadOnlyList<AccessGrant> ListActive()
    {
        lock (_gate)
        {
            return _byId.Values.Where(g => g.IsActive).OrderByDescending(g => g.GrantedAt).ToArray();
        }
    }

    /// <summary>Every grant, revoked included, newest first — the audit view.</summary>
    public IReadOnlyList<AccessGrant> ListAll()
    {
        lock (_gate)
        {
            return _byId.Values.OrderByDescending(g => g.GrantedAt).ToArray();
        }
    }

    /// <summary>Active grants that name <paramref name="granteeLogin"/> (case-insensitive) on
    /// <paramref name="resource"/> (exact) — the covering set an authorization decision reads.</summary>
    public IReadOnlyList<AccessGrant> FindActiveFor(string granteeLogin, string resource)
    {
        if (string.IsNullOrWhiteSpace(granteeLogin) || string.IsNullOrWhiteSpace(resource))
        {
            return [];
        }

        lock (_gate)
        {
            return _byId.Values
                .Where(g => g.IsActive
                    && string.Equals(g.GranteeLogin, granteeLogin, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(g.Resource, resource, StringComparison.Ordinal))
                .ToArray();
        }
    }

    /// <summary>Revokes a grant by id — a permanent transition. Returns the revoked record, or null if the id is
    /// unknown or was already revoked (idempotent-safe). A revoked grant is retained for audit but can never
    /// again authorize anything.</summary>
    public AccessGrant? Revoke(string id)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var g) || !g.IsActive)
            {
                return null;
            }

            var revoked = g with { RevokedAt = _time.GetUtcNow() };
            _byId[id] = revoked;
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
            var grants = JsonSerializer.Deserialize<List<AccessGrant>>(File.ReadAllText(_path), Options);
            foreach (var g in grants ?? [])
            {
                if (!string.IsNullOrWhiteSpace(g.Id))
                {
                    _byId[g.Id] = g;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load grant ledger from {Path}; starting empty.", _path);
            _byId.Clear();
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byId.Values.ToArray(), Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist grant ledger to {Path}.", _path);
        }
    }
}
