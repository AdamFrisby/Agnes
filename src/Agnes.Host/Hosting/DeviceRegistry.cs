using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>Public metadata about a paired device (never includes the token).</summary>
public sealed record DeviceInfo(string Id, string Name, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt);

/// <summary>The result of a successful pairing — the raw token is shown to the client exactly once.</summary>
public sealed record PairingResult(string DeviceId, string DeviceName, string Token);

/// <summary>
/// Per-device bearer-token auth with a short pairing code. A client pairs once (presenting the code
/// the host prints at startup) and receives a durable per-device token; only its SHA-256 hash is
/// persisted, so the store never holds a usable token at rest. Devices can be listed and revoked.
/// A configured bootstrap token (<c>Agnes:PairingToken</c>), if set, is always accepted — for
/// headless setups and back-compat with the earlier single-token scheme.
/// </summary>
public sealed class DeviceRegistry
{
    private const int MaxPairingFailures = 5;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new(); // by token hash
    private readonly string? _bootstrapToken;
    private readonly string _path;
    private readonly ILogger<DeviceRegistry>? _logger;
    private int _pairingFailures;

    public DeviceRegistry(string? bootstrapToken, string dataFilePath, ILogger<DeviceRegistry>? logger = null)
    {
        _bootstrapToken = string.IsNullOrWhiteSpace(bootstrapToken) ? null : bootstrapToken;
        _path = dataFilePath;
        _logger = logger;
        PairingCode = GeneratePairingCode();
        Load();
    }

    /// <summary>The one-time code a new device presents to pair. Rotates after too many bad attempts.</summary>
    public string PairingCode { get; private set; }

    /// <summary>Validates a bearer token (bootstrap or a paired device); records last-seen.</summary>
    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        if (_bootstrapToken is not null && FixedTimeEquals(token, _bootstrapToken))
        {
            return true;
        }

        if (_devices.TryGetValue(Hash(token), out var device))
        {
            device.LastSeenAt = DateTimeOffset.UtcNow;
            return true;
        }

        return false;
    }

    /// <summary>Pairs a new device given the current pairing code; returns its durable token.</summary>
    public PairingResult? TryPair(string? code, string? deviceName)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(code) || !FixedTimeEquals(code.Trim(), PairingCode))
            {
                if (++_pairingFailures >= MaxPairingFailures)
                {
                    PairingCode = GeneratePairingCode();
                    _pairingFailures = 0;
                    _logger?.LogWarning("Too many failed pairing attempts — pairing code rotated to: {Code}", PairingCode);
                }

                return null;
            }

            _pairingFailures = 0;
            var token = GenerateToken();
            var record = new DeviceRecord
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName.Trim(),
                TokenHash = Hash(token),
                PairedAt = DateTimeOffset.UtcNow,
            };
            _devices[record.TokenHash] = record;
            // A pairing code is single-use: rotate it so the same code can't pair a second device.
            PairingCode = GeneratePairingCode();
            Save();
            _logger?.LogInformation("Paired device {Name} ({Id}); new pairing code: {Code}", record.Name, record.Id, PairingCode);
            return new PairingResult(record.Id, record.Name, token);
        }
    }

    public IReadOnlyList<DeviceInfo> ListDevices()
        => _devices.Values
            .OrderByDescending(d => d.PairedAt)
            .Select(d => new DeviceInfo(d.Id, d.Name, d.PairedAt, d.LastSeenAt))
            .ToArray();

    public bool Revoke(string deviceId)
    {
        lock (_gate)
        {
            var entry = _devices.FirstOrDefault(kv => kv.Value.Id == deviceId);
            if (entry.Key is null || !_devices.TryRemove(entry.Key, out _))
            {
                return false;
            }

            Save();
            _logger?.LogInformation("Revoked device {Id}", deviceId);
            return true;
        }
    }

    private static string GeneratePairingCode()
    {
        // 8 chars from an unambiguous alphabet (no 0/O/1/I), grouped for readability: ABCD-EFGH.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[9];
        for (int i = 0, j = 0; i < 8; i++)
        {
            if (i == 4)
            {
                chars[j++] = '-';
            }

            chars[j++] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var records = JsonSerializer.Deserialize<List<DeviceRecord>>(File.ReadAllText(_path));
            foreach (var r in records ?? [])
            {
                if (!string.IsNullOrEmpty(r.TokenHash))
                {
                    _devices[r.TokenHash] = r;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load device registry from {Path}", _path);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_devices.Values.ToList()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not persist device registry to {Path}", _path);
        }
    }

    private sealed class DeviceRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public DateTimeOffset PairedAt { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
    }
}
