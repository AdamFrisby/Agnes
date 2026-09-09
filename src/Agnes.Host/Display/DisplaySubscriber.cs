using Agnes.Protocol;

namespace Agnes.Host.Display;

/// <summary>
/// One watching client's mailbox: a <b>single slot</b> holding the damage that has accumulated since the
/// last frame this subscriber actually sent, plus any control notices it still owes.
/// <para>
/// Single-slot is the whole design. A queue of frames for a slow socket is a memory leak with a latency
/// problem attached: by the time the tenth stale frame is written, it is describing a screen that stopped
/// existing seconds ago. Unioning damage instead means a subscriber that fell behind sends <em>one</em>
/// frame covering everything it missed, and a subscriber on a fast link sends every frame — with no branch
/// anywhere that has to know which it is. Frames are views, not facts; dropping them loses nothing.
/// </para>
/// <para>Control notices are queued, small and bounded, because they <em>are</em> facts and a client that
/// misses "the user took control" would draw the wrong affordances.</para>
/// </summary>
public sealed class DisplaySubscriber : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Queue<DisplayControlNotice> _notices = new();
    private readonly int _maxFpsCeiling;

    private DisplayRect _damage = DisplayRect.Empty;
    private DisplayQuality _quality;
    private long _droppedFrames;
    private bool _disposed;

    internal DisplaySubscriber(DisplayQuality quality, int maxFpsCeiling)
    {
        _maxFpsCeiling = maxFpsCeiling;
        _quality = Clamp(quality, maxFpsCeiling);
    }

    /// <summary>What this subscriber can use. A client may change it mid-stream (a phone rotating, a panel
    /// being resized) and the next frame honours it.</summary>
    public DisplayQuality Quality
    {
        get
        {
            lock (_gate)
            {
                return _quality;
            }
        }
    }

    /// <summary>Frames coalesced away because this subscriber was still writing the previous one. Surfaced
    /// for diagnostics and asserted on in tests — a slow client must show drops, not growth.</summary>
    public long DroppedFrames
    {
        get
        {
            lock (_gate)
            {
                return _droppedFrames;
            }
        }
    }

    public void SetQuality(DisplayQuality quality)
    {
        lock (_gate)
        {
            _quality = Clamp(quality, _maxFpsCeiling);
        }
    }

    /// <summary>The minimum gap between frames this subscriber asked for.</summary>
    public TimeSpan FrameInterval
    {
        get
        {
            var fps = Math.Clamp(Quality.MaxFps, 1, _maxFpsCeiling);
            return TimeSpan.FromMilliseconds(1000.0 / fps);
        }
    }

    /// <summary>Records damage. Called by the broker for every guest update, on the broker's pump — so it
    /// must never block and never allocate per frame.</summary>
    internal void Damage(DisplayRect rect)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (!_damage.IsEmpty)
            {
                _droppedFrames++; // the previous damage was never sent; it is being folded into this one.
            }

            _damage = _damage.Union(rect);
        }

        Wake();
    }

    internal void Notice(DisplayControlNotice notice)
    {
        lock (_gate)
        {
            _notices.Enqueue(notice);
        }

        Wake();
    }

    /// <summary>Waits until there is something to send, then takes it — clearing the slot atomically so
    /// damage that arrives while the frame is being encoded accumulates for the next one.</summary>
    public async Task<(DisplayRect Damage, IReadOnlyList<DisplayControlNotice> Notices)> TakeAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var damage = _damage;
            _damage = DisplayRect.Empty;
            var notices = _notices.Count == 0 ? Array.Empty<DisplayControlNotice>() : _notices.ToArray();
            _notices.Clear();
            return (damage, notices);
        }
    }

    /// <summary>Marks the whole surface dirty — used to seed a joining subscriber with a first full frame
    /// without waiting for the guest to repaint something.</summary>
    internal void DamageAll(int width, int height) => Damage(new DisplayRect(0, 0, width, height));

    private void Wake()
    {
        // Release only if nobody is already holding the signal: the slot is one-deep, so one wake is enough
        // however many updates landed while the sender was busy.
        try
        {
            if (!_disposed && _signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Raced with another Damage/Notice; the sender is already awake, which is all we wanted.
        }
        catch (ObjectDisposedException)
        {
            // The subscriber went away mid-update. Nothing to wake.
        }
    }

    private static DisplayQuality Clamp(DisplayQuality quality, int fpsCeiling) => new(
        Math.Clamp(quality.MaxWidth, 16, 8192),
        Math.Clamp(quality.MaxFps, 1, fpsCeiling),
        Math.Clamp(quality.JpegQuality, 1, 100));

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _signal.Dispose();
    }
}
