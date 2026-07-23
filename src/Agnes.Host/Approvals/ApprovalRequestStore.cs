using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Approvals;

/// <summary>
/// Thread-safe store of durable approval requests, keyed by id. Persisted to a JSON file with the same atomic
/// tmp-move + single-lock + load-tolerant pattern as the other host stores (mirrors
/// <see cref="Attention.AttentionRequestStore"/>), so an open request survives a restart. State transitions
/// are pure functions producing a new <see cref="ApprovalRequest"/> record; the store only ever swaps the
/// stored value under the lock. Time comes from an injected <see cref="TimeProvider"/> so creation stamps are
/// deterministic under test.
/// </summary>
public sealed class ApprovalRequestStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, ApprovalRequest> _requests = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger<ApprovalRequestStore>? _logger;

    public ApprovalRequestStore(string? path, TimeProvider? time = null, ILogger<ApprovalRequestStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        Load();
    }

    /// <summary>Creates an <see cref="ApprovalStatus.Open"/> request stamped from the clock.</summary>
    public ApprovalRequest Create(string actionId, ApprovalSurface surface, string argsSummary, string? preview)
    {
        var request = new ApprovalRequest(
            Id: Guid.NewGuid().ToString("n"),
            ActionId: actionId,
            Surface: surface,
            ArgsSummary: argsSummary,
            Preview: preview,
            Status: ApprovalStatus.Open,
            CreatedAt: _time.GetUtcNow());

        lock (_gate)
        {
            _requests[request.Id] = request;
            Persist();
        }

        return request;
    }

    /// <summary>The request by id, regardless of status, or null if unknown.</summary>
    public ApprovalRequest? Get(string id)
    {
        lock (_gate)
        {
            return _requests.GetValueOrDefault(id);
        }
    }

    /// <summary>Every still-Open request, oldest first — the slice unioned into the approvals inbox.</summary>
    public IReadOnlyList<ApprovalRequest> ListOpen()
    {
        lock (_gate)
        {
            return _requests.Values
                .Where(r => r.Status == ApprovalStatus.Open)
                .OrderBy(r => r.CreatedAt)
                .ToArray();
        }
    }

    /// <summary>Atomically moves a request from <paramref name="from"/> to <paramref name="to"/>, returning the
    /// updated record — or null if the id is unknown or not currently in <paramref name="from"/> (so a
    /// double-resolve or an out-of-order transition is a no-op rather than a corruption).</summary>
    public ApprovalRequest? TryTransition(string id, ApprovalStatus from, ApprovalStatus to)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var r) || r.Status != from)
            {
                return null;
            }

            var updated = r with { Status = to };
            _requests[id] = updated;
            Persist();
            return updated;
        }
    }

    private void Load()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var records = JsonSerializer.Deserialize<List<ApprovalRequest>>(File.ReadAllText(_path), Options);
            foreach (var r in records ?? [])
            {
                if (!string.IsNullOrEmpty(r.Id))
                {
                    _requests[r.Id] = r;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load approval-request store from {Path}; starting empty.", _path);
        }
    }

    // Assumes _gate is held.
    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_requests.Values.ToList(), Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not persist approval-request store to {Path}.", _path);
        }
    }
}
