using System.Collections.Concurrent;

namespace Agnes.Host.Notifications;

/// <summary>
/// Tracks which device is actively viewing which session <b>on that device</b>, so a push-eligible event for a
/// session someone is already looking at on their laptop is suppressed for that laptop only — a phone the same
/// person is carrying still gets paged, since "I'm looking at it here" says nothing about the other device.
/// <para>
/// This is intentionally device-scoped, not connection-scoped: the client tells the host "device D is now
/// foregrounding session S" (and clears it on background/close). Thread-safe; a session set per device.
/// </para>
/// </summary>
public sealed class ActiveSessionViewTracker
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _viewing = new(StringComparer.Ordinal);

    /// <summary>Records that <paramref name="deviceId"/> is now actively viewing <paramref name="sessionId"/>.</summary>
    public void MarkViewing(string deviceId, string sessionId)
    {
        var set = _viewing.GetOrAdd(deviceId, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set)
        {
            set.Add(sessionId);
        }
    }

    /// <summary>Records that <paramref name="deviceId"/> stopped viewing <paramref name="sessionId"/>.</summary>
    public void ClearViewing(string deviceId, string sessionId)
    {
        if (_viewing.TryGetValue(deviceId, out var set))
        {
            lock (set)
            {
                set.Remove(sessionId);
            }
        }
    }

    /// <summary>Clears all sessions a device was viewing (e.g. on disconnect).</summary>
    public void ClearDevice(string deviceId) => _viewing.TryRemove(deviceId, out _);

    /// <summary>Whether this exact device is actively viewing this exact session.</summary>
    public bool IsViewing(string deviceId, string sessionId)
    {
        if (!_viewing.TryGetValue(deviceId, out var set))
        {
            return false;
        }

        lock (set)
        {
            return set.Contains(sessionId);
        }
    }
}
