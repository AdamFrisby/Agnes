using System.Collections.Concurrent;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Notifications;

/// <summary>
/// Per-device, per-trigger push toggles plus a master on/off. Each of the three triggers is independently
/// controllable because a user waiting on a long build might want turn-ready pings but find permission pings
/// noisy (or vice versa on a security-sensitive repo). Immutable — a change produces a new record.
/// </summary>
public sealed record PushTriggerPrefs(
    bool TurnReady = true,
    bool PermissionRequest = true,
    bool UserActionRequest = true)
{
    /// <summary>Whether this device wants pushes for <paramref name="trigger"/>.</summary>
    public bool IsEnabled(NotificationTrigger trigger) => trigger switch
    {
        NotificationTrigger.TurnReady => TurnReady,
        NotificationTrigger.PermissionRequest => PermissionRequest,
        NotificationTrigger.UserActionRequest => UserActionRequest,
        _ => false,
    };
}

/// <summary>
/// One device's push registration: which channel it uses, the channel-specific token, and its toggles. Keyed
/// by device id so it lives and dies with the device's pairing (see <see cref="PushRegistrationStore"/>).
/// </summary>
public sealed record PushRegistration(
    string DeviceId,
    string ChannelId,
    string ChannelToken,
    bool Enabled,
    PushTriggerPrefs Triggers);

/// <summary>
/// The host-side store of which paired device wants pushes, keyed by device id — a small extension of the
/// pairing/device identity rather than a second identity concept. It observes <see cref="DeviceRevokedEvent"/>
/// on the spine so that revoking a device's pairing (existing device management) also drops its push
/// registration: no further pushes reach a device whose token was revoked. Persisted to a JSON file with the
/// same atomic temp-then-move write the device registry uses.
/// </summary>
public sealed class PushRegistrationStore : IEventObserver<DeviceRevokedEvent>, IDisposable
{
    private readonly ConcurrentDictionary<string, PushRegistration> _byDevice = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<PushRegistrationStore>? _logger;
    private readonly IDisposable? _subscription;

    public PushRegistrationStore(string dataFilePath, IEventBus? bus = null, ILogger<PushRegistrationStore>? logger = null)
    {
        _path = dataFilePath;
        _logger = logger;
        Load();
        // Ride the spine: a revoked device loses its push registration without this store taking a hard
        // dependency on the security-critical DeviceRegistry (which stays free of async plugin observers).
        _subscription = bus?.Observe(this);
    }

    /// <summary>Registers (or replaces) a device's push token + channel, leaving toggles at their default
    /// (all on) unless they were previously set. Returns the stored registration.</summary>
    public PushRegistration Register(string deviceId, string channelId, string channelToken)
    {
        lock (_gate)
        {
            var existing = _byDevice.GetValueOrDefault(deviceId);
            var registration = new PushRegistration(
                deviceId,
                channelId,
                channelToken,
                Enabled: existing?.Enabled ?? true,
                Triggers: existing?.Triggers ?? new PushTriggerPrefs());
            _byDevice[deviceId] = registration;
            Save();
            return registration;
        }
    }

    /// <summary>Sets the master on/off + per-trigger toggles for an already-registered device. No-op (returns
    /// null) if the device has no registration yet — a device sets its token before its preferences.</summary>
    public PushRegistration? SetPreferences(string deviceId, bool enabled, PushTriggerPrefs triggers)
    {
        lock (_gate)
        {
            if (!_byDevice.TryGetValue(deviceId, out var existing))
            {
                return null;
            }

            var updated = existing with { Enabled = enabled, Triggers = triggers };
            _byDevice[deviceId] = updated;
            Save();
            return updated;
        }
    }

    /// <summary>Removes a device's registration (revocation or explicit opt-out). True if one was present.</summary>
    public bool Remove(string deviceId)
    {
        lock (_gate)
        {
            if (!_byDevice.TryRemove(deviceId, out _))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>The registration for a device, or null if it hasn't registered.</summary>
    public PushRegistration? Get(string deviceId) => _byDevice.GetValueOrDefault(deviceId);

    /// <summary>Every registered device, in no guaranteed order.</summary>
    public IReadOnlyList<PushRegistration> All => _byDevice.Values.ToArray();

    /// <summary>Spine observer: a revoked device's push registration is invalidated along with its pairing.</summary>
    public ValueTask ObserveAsync(DeviceRevokedEvent evt, CancellationToken cancellationToken = default)
    {
        if (Remove(evt.DeviceId))
        {
            _logger?.LogInformation("Dropped push registration for revoked device {Device}", evt.DeviceId);
        }

        return ValueTask.CompletedTask;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var records = JsonSerializer.Deserialize<List<PushRegistration>>(File.ReadAllText(_path));
            foreach (var r in records ?? [])
            {
                if (!string.IsNullOrEmpty(r.DeviceId))
                {
                    _byDevice[r.DeviceId] = r;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load push registrations from {Path}", _path);
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byDevice.Values.ToList()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not persist push registrations to {Path}", _path);
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
