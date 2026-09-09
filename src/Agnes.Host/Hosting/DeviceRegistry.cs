using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>The result of a successful pairing — the raw token is shown to the client exactly once.</summary>
public sealed record PairingResult(string DeviceId, string DeviceName, string Token, DeviceRole Role = DeviceRole.Member);

/// <summary>The outcome of an attempted role change: <see cref="Error"/> is a message fit to show a human.</summary>
public sealed record DeviceRoleUpdate(bool Ok, DeviceInfo? Device = null, string? Error = null, bool NotFound = false);

/// <summary>
/// Per-device bearer-token auth with a short pairing code. A client pairs once (presenting the code
/// the host prints at startup) and receives a durable per-device token; only its SHA-256 hash is
/// persisted, so the store never holds a usable token at rest. Devices can be listed and revoked.
/// A configured bootstrap token (<c>Agnes:PairingToken</c>), if set, is always accepted — for
/// headless setups and back-compat with the earlier single-token scheme.
/// <para>
/// Each record carries a <see cref="DeviceRole"/> decided by HOW the device was admitted, never by when it
/// arrived. The previous rule — "the earliest-paired record is the owner" — was a booby trap: anything that
/// wrote a record into the store before the operator's own device did (a stray test run, a demo, a device
/// paired and later revoked) silently became the host's owner, and under the default shared isolation that
/// left every real device unable to see any session at all.
/// </para>
/// </summary>
public sealed class DeviceRegistry
{
    private const int MaxPairingFailures = 5;

    /// <summary>How often a last-seen touch may hit the disk. Last-seen is a housekeeping fact used to
    /// answer "is this device still in use", so it must survive a restart — but it changes on every single
    /// request, and a fsync per request would be absurd. Debounced, plus a flush at shutdown.</summary>
    private static readonly TimeSpan LastSeenSaveInterval = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions StoreJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new(); // by token hash
    private readonly string? _bootstrapToken;
    private readonly string _path;
    private readonly ILogger<DeviceRegistry>? _logger;
    private readonly bool _pairingEnabled;
    private readonly bool _allowCodeAfterFirstDevice;
    private readonly TimeProvider _time;
    private int _pairingFailures;
    private DateTimeOffset _lastSeenSavedAt = DateTimeOffset.MinValue;
    private bool _lastSeenDirty;

    public DeviceRegistry(
        string? bootstrapToken,
        string dataFilePath,
        ILogger<DeviceRegistry>? logger = null,
        bool pairingEnabled = true,
        bool allowCodeAfterFirstDevice = false,
        TimeProvider? timeProvider = null)
    {
        _bootstrapToken = string.IsNullOrWhiteSpace(bootstrapToken) ? null : bootstrapToken;
        _path = dataFilePath;
        _logger = logger;
        _pairingEnabled = pairingEnabled;
        _allowCodeAfterFirstDevice = allowCodeAfterFirstDevice;
        _time = timeProvider ?? TimeProvider.System;
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
    /// Mints (or re-issues) a durable per-device token for a caller that authenticated by some other means
    /// (GitHub SSO, keypair, an approval, …). <paramref name="subject"/> records who/what it belongs to for the
    /// device list/audit (e.g. <c>github:alice</c>, <c>key:laptop</c>); <paramref name="kind"/> is the method;
    /// <paramref name="role"/> is what that method entitles the device to on this host.
    /// <para>
    /// <paramref name="identity"/> is the stable thing behind the credential — a key fingerprint, or a login
    /// plus device name. When one is supplied and a live record already carries it, this is a <b>rotation</b>:
    /// the record keeps its id, pairing date and role, gets a fresh token (the old one stops working), and the
    /// device list keeps one row per device instead of growing one per sign-in. A pairing code has no such
    /// thing behind it — it is single-use — so those always mint fresh.
    /// </para>
    /// </summary>
    public PairingResult IssueDeviceToken(
        string? deviceName, string subject, string kind, DeviceRole role = DeviceRole.Member, string? identity = null)
    {
        lock (_gate)
        {
            var result = IssueDeviceTokenLocked(deviceName, subject, kind, role, identity);
            Save();
            _logger?.LogInformation(
                "Issued device token for {Subject} via {Kind} as {Role} ({Id})", subject, kind, result.Role, result.DeviceId);
            return result;
        }
    }

    // Assumes _gate is held; callers persist + log.
    private PairingResult IssueDeviceTokenLocked(
        string? deviceName, string subject, string kind, DeviceRole role, string? identity)
    {
        var token = GenerateToken();
        var now = _time.GetUtcNow();
        var name = string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName.Trim();

        if (identity is { Length: > 0 }
            && _devices.FirstOrDefault(kv => string.Equals(kv.Value.Identity, identity, StringComparison.Ordinal)) is { Key: not null } existing)
        {
            // Same credential, same device: rotate the token in place rather than minting a second identity.
            _devices.TryRemove(existing.Key, out _);
            var rotated = existing.Value;
            rotated.TokenHash = Hash(token);
            rotated.Name = name;
            rotated.Subject = subject;
            rotated.Kind = kind;
            rotated.LastSeenAt = now;
            _devices[rotated.TokenHash] = rotated;
            return new PairingResult(rotated.Id, rotated.Name, token, rotated.EffectiveRole);
        }

        var record = new DeviceRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            TokenHash = Hash(token),
            Subject = subject,
            Kind = kind,
            Identity = identity,
            Role = role,
            PairedAt = now,
        };
        _devices[record.TokenHash] = record;
        return new PairingResult(record.Id, record.Name, token, record.EffectiveRole);
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
            TouchLastSeen(device);
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
            TouchLastSeen(device);
            return device.Id;
        }

        return null;
    }

    /// <summary>
    /// Resolves a bearer token to the GitHub login of the paired device, or null when the device wasn't paired
    /// via GitHub (its <c>Subject</c> isn't <c>github:&lt;login&gt;</c>) or the token is unknown. This is how the
    /// social/collaborators layer learns "who" a caller is on GitHub — the login the GitHub exchange recorded on the
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

    /// <summary>
    /// Whether any device has paired yet. The typed bootstrap code is only accepted while this is false —
    /// see <see cref="TryPair"/>.
    /// </summary>
    public bool HasPairedDevice => !_devices.IsEmpty;

    /// <summary>
    /// Pairs a new device given the current typed bootstrap code; returns its durable token.
    ///
    /// **The code only works until the first device pairs.** It is short because a human reads it off a
    /// screen and types it, which caps it at around forty bits — fine as a one-shot bootstrap for a host
    /// nobody is connected to yet, and not fine as a standing way in. Once there is a device that could
    /// vouch instead, the stronger paths take over: a 256-bit QR grant minted by that device, or an
    /// explicit approval of a request. An operator who genuinely needs the code back can re-enable it
    /// with <c>Agnes:Auth:Pairing:AllowCodeAfterFirstDevice</c>.
    ///
    /// The code IS the operator's own secret, read off the host's console, so a device that presents it is
    /// admitted as an <see cref="DeviceRole.Owner"/>.
    /// </summary>
    public PairingResult? TryPair(string? code, string? deviceName)
    {
        lock (_gate)
        {
            if (!_pairingEnabled)
            {
                return null; // pairing-code bootstrap turned off (GitHub SSO / keypair only).
            }

            if (HasPairedDevice && !_allowCodeAfterFirstDevice)
            {
                _logger?.LogWarning(
                    "Refused a pairing-code attempt: this host already has a paired device, so the typed code "
                    + "is closed. Use a QR grant or an approval from the paired device.");
                return null;
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
            var result = IssueDeviceTokenLocked(deviceName, subject: "pairing", kind: "pairing", DeviceRole.Owner, identity: null);
            // A pairing code is single-use: rotate it so the same code can't pair a second device.
            PairingCode = GeneratePairingCode();
            Save();
            _logger?.LogInformation("Paired device {Name} ({Id}); new pairing code: {Code}", result.DeviceName, result.DeviceId, PairingCode);
            return result;
        }
    }

    /// <summary>
    /// The paired devices, newest first. When <paramref name="callerToken"/> is supplied, the entry that token
    /// belongs to is marked <see cref="DeviceInfo.IsCurrentDevice"/> so a client can tell the caller which row
    /// is the one they are connected on — revoking it locks them out, and that is worth knowing first. A pure
    /// read: unlike <see cref="IsValid"/>/<see cref="ResolveCallerId"/> it does not touch last-seen.
    /// </summary>
    public IReadOnlyList<DeviceInfo> ListDevices(string? callerToken = null)
    {
        var currentId = callerToken is { Length: > 0 } && _devices.TryGetValue(Hash(callerToken), out var self)
            ? self.Id
            : null;

        return _devices.Values
            .OrderByDescending(d => d.PairedAt)
            .Select(d => Describe(d, currentId))
            .ToArray();
    }

    /// <summary>
    /// The caller's OWN device row, or null when the token isn't a device (unknown, or the bootstrap token,
    /// which has no record). Backs <c>GET /devices/me</c>: a Member can be told what it is on this host
    /// without being handed the whole device list, which is the answer to "why can't I see any sessions?".
    /// </summary>
    public DeviceInfo? DescribeSelf(string? token)
        => token is { Length: > 0 } && _devices.TryGetValue(Hash(token), out var device)
            ? Describe(device, device.Id)
            : null;

    private static DeviceInfo Describe(DeviceRecord d, string? currentId)
        => new(
            d.Id, d.Name, d.PairedAt, d.LastSeenAt, d.Subject,
            IsCurrentDevice: currentId is not null && string.Equals(d.Id, currentId, StringComparison.Ordinal),
            Role: d.EffectiveRole,
            Kind: d.Kind);

    /// <summary>
    /// Whether a resolved caller id is a host owner/operator. The configured bootstrap token (mapped to the
    /// fixed id <c>"bootstrap"</c>) is an operator by definition; otherwise the device's recorded
    /// <see cref="DeviceRole"/> decides. Returns false for an unknown/anonymous caller.
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

        return _devices.Values.Any(d =>
            d.EffectiveRole == DeviceRole.Owner && string.Equals(d.Id, callerId, StringComparison.Ordinal));
    }

    /// <summary>How many devices currently hold <see cref="DeviceRole.Owner"/>.</summary>
    public int OwnerCount => _devices.Values.Count(d => d.EffectiveRole == DeviceRole.Owner);

    /// <summary>
    /// Promotes or demotes a device. Refuses to remove the host's last Owner — a host with no owner cannot
    /// promote anybody back, so the only recovery would be editing the store by hand.
    /// </summary>
    public DeviceRoleUpdate SetRole(string deviceId, DeviceRole role, string? callerId)
    {
        lock (_gate)
        {
            var device = _devices.Values.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal));
            if (device is null)
            {
                return new DeviceRoleUpdate(false, NotFound: true, Error: "No such device.");
            }

            if (device.EffectiveRole == role)
            {
                return new DeviceRoleUpdate(true, Describe(device, callerId));
            }

            if (role != DeviceRole.Owner && device.EffectiveRole == DeviceRole.Owner && OwnerCount <= 1)
            {
                return new DeviceRoleUpdate(
                    false,
                    Error: string.Equals(device.Id, callerId, StringComparison.Ordinal)
                        ? "You are this host's last Owner; promote another device before demoting yourself."
                        : "This is the host's last Owner; promote another device first.");
            }

            device.Role = role;
            Save();
            _logger?.LogInformation("Device {Id} is now {Role} (changed by {Caller})", device.Id, role, callerId ?? "unknown");
            return new DeviceRoleUpdate(true, Describe(device, callerId));
        }
    }

    /// <summary>
    /// Removes devices that have not been seen for <paramref name="unusedForDays"/> days — measured from
    /// last-seen, or from pairing when a device has never connected at all. Never the caller's own device
    /// (locking yourself out while tidying up is not a tidy-up), and never the last Owner.
    /// </summary>
    /// <returns>The devices that were removed.</returns>
    public IReadOnlyList<DeviceInfo> Prune(int unusedForDays, string? callerId)
    {
        lock (_gate)
        {
            var cutoff = _time.GetUtcNow() - TimeSpan.FromDays(Math.Max(0, unusedForDays));
            var candidates = _devices
                .Where(kv => (kv.Value.LastSeenAt ?? kv.Value.PairedAt) < cutoff)
                .Where(kv => !string.Equals(kv.Value.Id, callerId, StringComparison.Ordinal))
                .ToList();

            // Keep at least one Owner standing. If every Owner is stale, the freshest of them survives.
            var survivingOwners = OwnerCount - candidates.Count(kv => kv.Value.EffectiveRole == DeviceRole.Owner);
            if (survivingOwners <= 0)
            {
                var keep = candidates
                    .Where(kv => kv.Value.EffectiveRole == DeviceRole.Owner)
                    .OrderByDescending(kv => kv.Value.LastSeenAt ?? kv.Value.PairedAt)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
                if (keep is not null)
                {
                    candidates.RemoveAll(kv => string.Equals(kv.Key, keep, StringComparison.Ordinal));
                }
            }

            var removed = new List<DeviceInfo>(candidates.Count);
            foreach (var kv in candidates)
            {
                if (_devices.TryRemove(kv.Key, out var gone))
                {
                    removed.Add(Describe(gone, callerId));
                }
            }

            if (removed.Count > 0)
            {
                Save();
                _logger?.LogInformation("Pruned {Count} device(s) unused for {Days} day(s)", removed.Count, unusedForDays);
            }

            return removed;
        }
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

    /// <summary>Persists any last-seen timestamps the debounce is still holding. Called at shutdown.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_lastSeenDirty)
            {
                return;
            }

            _lastSeenDirty = false;
            _lastSeenSavedAt = _time.GetUtcNow();
            Save();
        }
    }

    // Last-seen is written through to disk, but at most once a minute: it changes on every request, and it is
    // only useful across restarts (it is what "unused for 30 days" is measured from), so neither "never save"
    // nor "save every time" is right.
    private void TouchLastSeen(DeviceRecord device)
    {
        var now = _time.GetUtcNow();
        device.LastSeenAt = now;
        _lastSeenDirty = true;

        if (now - _lastSeenSavedAt < LastSeenSaveInterval)
        {
            return;
        }

        lock (_gate)
        {
            if (now - _lastSeenSavedAt < LastSeenSaveInterval)
            {
                return;
            }

            _lastSeenSavedAt = now;
            _lastSeenDirty = false;
            Save();
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

            var records = JsonSerializer.Deserialize<List<DeviceRecord>>(File.ReadAllText(_path), StoreJson);
            foreach (var r in records ?? [])
            {
                if (!string.IsNullOrEmpty(r.TokenHash))
                {
                    _devices[r.TokenHash] = r;
                }
            }

            MigrateRoles();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load device registry from {Path}", _path);
        }
    }

    /// <summary>
    /// A store written before roles existed has none, and every record reads back as a Member — which would
    /// leave a live host with no owner at all. So: if nothing claims Owner, the earliest-paired device becomes
    /// one. That is exactly the rule the old <c>IsOwner</c> applied on every call, frozen once and written
    /// down, so upgrading a host changes nothing about who its owner is.
    /// </summary>
    private void MigrateRoles()
    {
        // A record written before roles existed has none at all. That is the only thing that gets migrated —
        // an explicit Member is somebody's decision, not a gap.
        var unroled = _devices.Values.Where(d => d.Role is null).ToList();
        if (unroled.Count == 0)
        {
            return;
        }

        foreach (var device in unroled)
        {
            device.Role = DeviceRole.Member;
        }

        if (_devices.Values.All(d => d.EffectiveRole != DeviceRole.Owner))
        {
            var earliest = _devices.Values
                .OrderBy(d => d.PairedAt)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .First();
            earliest.Role = DeviceRole.Owner;
            _logger?.LogInformation(
                "Device roles migrated: nothing claimed Owner, so the earliest-paired device {Name} ({Id}) keeps "
                + "the ownership it had under the old first-device-wins rule. Promote or demote from the "
                + "Devices page.",
                earliest.Name, earliest.Id);
        }
        else
        {
            _logger?.LogInformation("Device roles migrated: {Count} device(s) recorded as Member.", unroled.Count);
        }

        Save();
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(_devices.Values.ToList(), StoreJson));
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
        public string? Identity { get; set; }   // the stable credential behind it — a key fingerprint, a login+name

        /// <summary>Null in a store written before roles existed — the signal <see cref="MigrateRoles"/>
        /// looks for. An explicit Member is a decision somebody made and is never migrated over.</summary>
        public DeviceRole? Role { get; set; }

        public DeviceRole EffectiveRole => Role ?? DeviceRole.Member;
        public DateTimeOffset PairedAt { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
    }
}
