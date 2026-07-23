namespace Agnes.Relay;

/// <summary>Abuse-control config for the relay (bound from <c>Relay:RateLimit</c>).</summary>
public sealed record RelayRateLimitOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>Max new connections accepted per source IP per minute.</summary>
    public int PerIpPerMinute { get; init; } = 60;

    /// <summary>Max concurrent in-flight connections per source IP.</summary>
    public int MaxConcurrentPerIp { get; init; } = 64;

    /// <summary>Max lookups of an unknown/unclaimed host-id per source IP per minute (probing defense).</summary>
    public int UnknownHostPerIpPerMinute { get; init; } = 10;

    /// <summary>How long a host-signalled ban lasts.</summary>
    public TimeSpan BanDuration { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Per-source-IP abuse controls for the relay. Mirrors the shape of the host-side
/// <c>Agnes.Host.Hosting.AuthRateLimit</c> — a fixed-window rate limit plus a concurrency cap — and
/// adds a host-signalled ban list and a separate, tighter window for unknown-host-id probing. All
/// decisions are metadata-only (source IP + counts); the limiter never sees payload. A
/// <see cref="TimeProvider"/> is injected so window/ban expiry is deterministic in tests.
/// </summary>
public sealed class RelayRateLimiter(RelayRateLimitOptions options, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Window> _unknownLookups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _concurrent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _bans = new(StringComparer.Ordinal);

    private sealed class Window
    {
        public DateTimeOffset Start;
        public int Count;
    }

    /// <summary>
    /// Decides whether to accept a freshly-accepted connection from <paramref name="sourceIp"/>. On
    /// success reserves a concurrency slot that must be returned via <see cref="ReleaseConnection"/>.
    /// </summary>
    public bool TryAcceptConnection(string sourceIp)
    {
        if (!options.Enabled)
        {
            return true;
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (IsBannedLocked(sourceIp, now))
            {
                return false;
            }

            if (Bump(_connections, sourceIp, now, options.PerIpPerMinute) is false)
            {
                return false;
            }

            int current = _concurrent.GetValueOrDefault(sourceIp);
            if (current >= options.MaxConcurrentPerIp)
            {
                // Undo the window bump we just took — this connection is refused for concurrency.
                _connections[sourceIp].Count--;
                return false;
            }

            _concurrent[sourceIp] = current + 1;
            return true;
        }
    }

    /// <summary>Returns a concurrency slot reserved by <see cref="TryAcceptConnection"/>.</summary>
    public void ReleaseConnection(string sourceIp)
    {
        if (!options.Enabled)
        {
            return;
        }

        lock (_gate)
        {
            int current = _concurrent.GetValueOrDefault(sourceIp);
            if (current <= 1)
            {
                _concurrent.Remove(sourceIp);
            }
            else
            {
                _concurrent[sourceIp] = current - 1;
            }
        }
    }

    /// <summary>Records (and rate-limits) a lookup of an unknown host-id — probing defense.</summary>
    public bool AllowUnknownHostLookup(string sourceIp)
    {
        if (!options.Enabled)
        {
            return true;
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            return Bump(_unknownLookups, sourceIp, now, options.UnknownHostPerIpPerMinute);
        }
    }

    /// <summary>Host-signalled ban: block this source IP's next connections for the configured duration.</summary>
    public void Ban(string sourceIp)
    {
        DateTimeOffset until = _time.GetUtcNow() + options.BanDuration;
        lock (_gate)
        {
            _bans[sourceIp] = until;
        }
    }

    public bool IsBanned(string sourceIp)
    {
        lock (_gate)
        {
            return IsBannedLocked(sourceIp, _time.GetUtcNow());
        }
    }

    private bool IsBannedLocked(string sourceIp, DateTimeOffset now)
    {
        if (!_bans.TryGetValue(sourceIp, out DateTimeOffset until))
        {
            return false;
        }

        if (until <= now)
        {
            _bans.Remove(sourceIp);
            return false;
        }

        return true;
    }

    /// <summary>Fixed-window bump; returns false when the window's permit limit is exceeded.</summary>
    private bool Bump(Dictionary<string, Window> windows, string key, DateTimeOffset now, int limit)
    {
        if (!windows.TryGetValue(key, out Window? w))
        {
            w = new Window { Start = now, Count = 0 };
            windows[key] = w;
        }

        if (now - w.Start >= TimeSpan.FromMinutes(1))
        {
            w.Start = now;
            w.Count = 0;
        }

        if (w.Count >= limit)
        {
            return false;
        }

        w.Count++;
        return true;
    }
}
