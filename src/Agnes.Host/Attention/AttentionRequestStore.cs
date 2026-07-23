using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Attention;

/// <summary>
/// Thread-safe store of external attention requests, keyed by id. Persisted to a JSON file with the same
/// atomic tmp-move + single-lock + load-tolerant pattern as the other host stores, so answers survive a
/// restart. State transitions are pure functions producing a new <see cref="AttentionRequest"/> record;
/// the store only ever swaps the stored value under the lock. Time comes from an injected
/// <see cref="TimeProvider"/> so creation stamps and timeout sweeps are deterministic under test.
/// </summary>
public sealed class AttentionRequestStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, AttentionRequest> _requests = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger<AttentionRequestStore>? _logger;

    public AttentionRequestStore(string? path, TimeProvider? time = null, ILogger<AttentionRequestStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        Load();
    }

    /// <summary>Creates a Pending request owned by <paramref name="ownerCallerId"/>, stamped from the clock.</summary>
    public AttentionRequest Create(string ownerCallerId, string source, string question, IReadOnlyList<string> options, string? callbackUrl, int? timeoutSeconds)
    {
        var request = new AttentionRequest(
            Id: Guid.NewGuid().ToString("n"),
            Source: source,
            Question: question,
            Options: options.ToArray(),
            CallbackUrl: callbackUrl,
            TimeoutSeconds: timeoutSeconds,
            CreatedAt: _time.GetUtcNow(),
            Status: AttentionStatus.Pending,
            Answer: null,
            OwnerCallerId: ownerCallerId);

        lock (_gate)
        {
            _requests[request.Id] = request;
            Persist();
        }

        return request;
    }

    /// <summary>The request by id, regardless of owner (host-internal read for the inbox/answer paths).</summary>
    public AttentionRequest? Get(string id)
    {
        lock (_gate)
        {
            return _requests.GetValueOrDefault(id);
        }
    }

    /// <summary>The request by id ONLY if <paramref name="ownerCallerId"/> created it — the scoping read for
    /// the external polling endpoint. A cross-caller (or unknown) id yields null so existence never leaks.</summary>
    public AttentionRequest? GetForOwner(string id, string ownerCallerId)
    {
        lock (_gate)
        {
            return _requests.TryGetValue(id, out var r) && string.Equals(r.OwnerCallerId, ownerCallerId, StringComparison.Ordinal)
                ? r
                : null;
        }
    }

    /// <summary>Every still-Pending request, oldest first — the slice unioned into the approvals inbox.</summary>
    public IReadOnlyList<AttentionRequest> ListPending()
    {
        lock (_gate)
        {
            return _requests.Values
                .Where(r => r.Status == AttentionStatus.Pending)
                .OrderBy(r => r.CreatedAt)
                .ToArray();
        }
    }

    /// <summary>Records an answer and flips to Answered — but only from Pending. Returns the updated record,
    /// or null if the id is unknown or already resolved (answered/expired), so a late answer after a timeout
    /// is rejected.</summary>
    public AttentionRequest? TryAnswer(string id, string answer)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var r) || r.Status != AttentionStatus.Pending)
            {
                return null;
            }

            var updated = r with { Status = AttentionStatus.Answered, Answer = answer };
            _requests[id] = updated;
            Persist();
            return updated;
        }
    }

    /// <summary>Flips a Pending request to Expired (idempotent-safe: null if not Pending). Used by the sweeper.</summary>
    public AttentionRequest? TryExpire(string id)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var r) || r.Status != AttentionStatus.Pending)
            {
                return null;
            }

            var updated = r with { Status = AttentionStatus.Expired };
            _requests[id] = updated;
            Persist();
            return updated;
        }
    }

    /// <summary>Pending requests whose <c>CreatedAt + TimeoutSeconds</c> is at or before <paramref name="now"/>.</summary>
    public IReadOnlyList<AttentionRequest> FindTimedOut(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _requests.Values
                .Where(r => r.Status == AttentionStatus.Pending
                    && r.TimeoutSeconds is { } t
                    && now >= r.CreatedAt.AddSeconds(t))
                .ToArray();
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

            var records = JsonSerializer.Deserialize<List<AttentionRequest>>(File.ReadAllText(_path), Options);
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
            _logger?.LogWarning(ex, "Could not load attention-request store from {Path}; starting empty.", _path);
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
            _logger?.LogWarning(ex, "Could not persist attention-request store to {Path}.", _path);
        }
    }
}
