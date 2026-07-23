using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

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
    private readonly bool _pairingEnabled;
    private int _pairingFailures;

    public DeviceRegistry(string? bootstrapToken, string dataFilePath, ILogger<DeviceRegistry>? logger = null, bool pairingEnabled = true)
    {
        _bootstrapToken = string.IsNullOrWhiteSpace(bootstrapToken) ? null : bootstrapToken;
        _path = dataFilePath;
        _logger = logger;
        _pairingEnabled = pairingEnabled;
        // Don't mint (or expose) a pairing code at all when the method is disabled for an internet-facing host.
        PairingCode = pairingEnabled ? GeneratePairingCode() : string.Empty;
        Load();
    }

    /// <summary>The one-time code a new device presents to pair. Rotates after too many bad attempts.
    /// Empty when pairing is disabled.</summary>
    public string PairingCode { get; private set; }

    /// <summary>Whether the pairing-code bootstrap is enabled (may be off in favour of GitHub SSO / keypair).</summary>
    public bool PairingEnabled => _pairingEnabled;

    /// <summary>
    /// Mints a durable per-device token for a caller that authenticated by some other means (GitHub SSO,
    /// keypair, …). <paramref name="subject"/> records who/what it belongs to for the device list/audit
    /// (e.g. <c>github:alice</c>, <c>key:laptop</c>); <paramref name="kind"/> is the method.
    /// </summary>
    public PairingResult IssueDeviceToken(string? deviceName, string subject, string kind)
    {
        lock (_gate)
        {
            var result = IssueDeviceTokenLocked(deviceName, subject, kind);
            Save();
            _logger?.LogInformation("Issued device token for {Subject} via {Kind} ({Id})", subject, kind, result.DeviceId);
            return result;
        }
    }

    // Assumes _gate is held; callers persist + log.
    private PairingResult IssueDeviceTokenLocked(string? deviceName, string subject, string kind)
    {
        var token = GenerateToken();
        var record = new DeviceRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName.Trim(),
            TokenHash = Hash(token),
            Subject = subject,
            Kind = kind,
            PairedAt = DateTimeOffset.UtcNow,
        };
        _devices[record.TokenHash] = record;
        return new PairingResult(record.Id, record.Name, token);
    }

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

    /// <summary>
    /// Resolves a bearer token to a stable caller id for per-caller scoping (external attention requests),
    /// or null if the token is invalid. A paired/issued device maps to its device id; the configured
    /// bootstrap token maps to the fixed id <c>"bootstrap"</c> (it isn't a device record). Records last-seen,
    /// matching <see cref="IsValid"/>.
    /// </summary>
    public string? ResolveCallerId(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (_bootstrapToken is not null && FixedTimeEquals(token, _bootstrapToken))
        {
            return "bootstrap";
        }

        if (_devices.TryGetValue(Hash(token), out var device))
        {
            device.LastSeenAt = DateTimeOffset.UtcNow;
            return device.Id;
        }

        return null;
    }

    /// <summary>
    /// Resolves a bearer token to the GitHub login of the paired device, or null when the device wasn't paired
    /// via GitHub (its <c>Subject</c> isn't <c>github:&lt;login&gt;</c>) or the token is unknown. This is how the
    /// social/friends layer learns "who" a caller is on GitHub — the login the GitHub exchange recorded on the
    /// device record — without a fresh GitHub round-trip. Never records last-seen (a pure identity read).
    /// </summary>
    public string? ResolveGitHubLogin(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return _devices.TryGetValue(Hash(token), out var device) ? GitHubLoginFromSubject(device.Subject) : null;
    }

    /// <summary>The GitHub login recorded on a device subject (<c>github:&lt;login&gt;</c>), or null.</summary>
    internal static string? GitHubLoginFromSubject(string? subject)
    {
        const string prefix = "github:";
        return subject is not null && subject.StartsWith(prefix, StringComparison.Ordinal) && subject.Length > prefix.Length
            ? subject[prefix.Length..]
            : null;
    }

    /// <summary>Pairs a new device given the current pairing code; returns its durable token.</summary>
    public PairingResult? TryPair(string? code, string? deviceName)
    {
        lock (_gate)
        {
            if (!_pairingEnabled)
            {
                return null; // pairing-code bootstrap turned off (GitHub SSO / keypair only).
            }

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
            var result = IssueDeviceTokenLocked(deviceName, subject: "pairing", kind: "pairing");
            // A pairing code is single-use: rotate it so the same code can't pair a second device.
            PairingCode = GeneratePairingCode();
            Save();
            _logger?.LogInformation("Paired device {Name} ({Id}); new pairing code: {Code}", result.DeviceName, result.DeviceId, PairingCode);
            return result;
        }
    }

    public IReadOnlyList<DeviceInfo> ListDevices()
        => _devices.Values
            .OrderByDescending(d => d.PairedAt)
            .Select(d => new DeviceInfo(d.Id, d.Name, d.PairedAt, d.LastSeenAt, d.Subject))
            .ToArray();

    /// <summary>
    /// Whether a resolved caller id is the host owner/operator. The configured bootstrap token (mapped to the
    /// fixed id <c>"bootstrap"</c>) is the operator by definition; otherwise the earliest-paired device is
    /// treated as the owner (the first device paired to a fresh host is whoever set it up). Used to gate the
    /// sensitive owner-only host-log diagnostic attachment. Returns false for an unknown/anonymous caller.
    /// </summary>
    public bool IsOwner(string? callerId)
    {
        if (string.IsNullOrEmpty(callerId))
        {
            return false;
        }

        if (string.Equals(callerId, "bootstrap", StringComparison.Ordinal))
        {
            return true;
        }

        var owner = _devices.Values
            .OrderBy(d => d.PairedAt)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return owner is not null && string.Equals(owner.Id, callerId, StringComparison.Ordinal);
    }

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
        public string? Subject { get; set; }   // who/what the token belongs to (github:login, key:label, pairing)
        public string? Kind { get; set; }       // the bootstrap method that minted it
        public DateTimeOffset PairedAt { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
    }
}
