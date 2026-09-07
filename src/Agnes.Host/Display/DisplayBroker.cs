using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Protocol;
using Agnes.Sandbox;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Display;

/// <summary>Appends a display-control change to the session log — the same persisted + broadcast + spine path
/// an agent's own events take. A one-method seam so the broker stays testable without a SessionManager.</summary>
public interface IDisplayControlSink
{
    Task AppendAsync(string sessionId, DisplayControlChangedEvent changed, CancellationToken cancellationToken = default);
}

/// <summary>
/// One graphical session's display, brokered: the single <see cref="IDisplaySession"/>, the host's copy of
/// the screen, every watching client, and the arbiter that decides whose input reaches the guest.
/// <para>
/// One broker per session, and it owns the <em>only</em> capture connection. Every consumer — a person on a
/// laptop, the same person's phone, the agent's <c>computer_screenshot</c> — reads from the one surface, so
/// they cannot disagree about what is on screen. It is created on the first consumer and collapses when the
/// last one leaves with no turn running, because a capture connection to an idle VM is pure cost: nobody is
/// looking, and the guest is still painting.
/// </para>
/// </summary>
public sealed class DisplayBroker : IAsyncDisposable
{
    private readonly string _sessionId;
    private readonly IDisplaySession _session;
    private readonly DisplayOptions _options;
    private readonly IEventBus _bus;
    private readonly IDisplayControlSink _sink;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<DisplaySubscriber> _subscribers = [];
    private readonly object _subscribersGate = new();
    private readonly SemaphoreSlim _injectGate = new(1, 1);

    private Task _pump = Task.CompletedTask;
    private int _pointerX;
    private int _pointerY;

    // Stamped on every outgoing frame. Interlocked because each subscriber's send loop is its own task, and
    // two clients on one screen must not be handed the same number.
    private int _frameSequence;

    public DisplayBroker(
        string sessionId,
        IDisplaySession session,
        GraphicalDisplay display,
        DisplayOptions options,
        IEventBus bus,
        IDisplayControlSink sink,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        _sessionId = sessionId;
        _session = session;
        _options = options;
        _bus = bus;
        _sink = sink;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Display = display;
        Surface = new DisplaySurface(session.Geometry);
        Arbiter = new InputArbiter(options, _time);
        LastActivityUtc = _time.GetUtcNow();
    }

    public string SessionId => _sessionId;

    public GraphicalDisplay Display { get; }

    public DisplayGeometry Geometry => Surface.Geometry;

    public DisplaySurface Surface { get; }

    public InputArbiter Arbiter { get; }

    /// <summary>When a consumer last used this broker — a subscriber joining or leaving, an input, a
    /// screenshot. The idle-collapse sweep reads it so a burst of tool calls between turns doesn't get the
    /// capture connection torn down underneath it.</summary>
    public DateTimeOffset LastActivityUtc { get; private set; }

    public int SubscriberCount
    {
        get
        {
            lock (_subscribersGate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>Takes the first full frame and starts the capture pump. Separate from the constructor because
    /// it awaits the guest, and a broker that failed to start must never be handed to a subscriber.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _session.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        Surface.ApplySnapshot(snapshot, Geometry.Format);
        _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in _session.Updates.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var damaged = Surface.Apply(update);
                if (damaged.IsEmpty)
                {
                    continue; // Damage gating: no update from the guest means no frame on any wire.
                }

                lock (_subscribersGate)
                {
                    foreach (var subscriber in _subscribers)
                    {
                        subscriber.Damage(damaged);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Display capture for session {SessionId} stopped.", _sessionId);
        }
    }

    // ---- subscribers ----

    /// <summary>Registers a watching client and seeds it with one full frame, so a client that joins a static
    /// screen sees it immediately rather than waiting for the guest to repaint something.</summary>
    public DisplaySubscriber AddSubscriber(DisplayQuality quality)
    {
        var subscriber = new DisplaySubscriber(quality, _options.MaxFps);
        lock (_subscribersGate)
        {
            _subscribers.Add(subscriber);
        }

        Touch();
        subscriber.DamageAll(Surface.Width, Surface.Height);
        return subscriber;
    }

    public void RemoveSubscriber(DisplaySubscriber subscriber)
    {
        lock (_subscribersGate)
        {
            _subscribers.Remove(subscriber);
        }

        subscriber.Dispose();
        Touch();
    }

    /// <summary>
    /// The frame this subscriber should send for the damage it has accumulated: one Tile when the damage is a
    /// small part of the screen, one Full frame when it is most of it. The threshold
    /// (<see cref="DisplayOptions.FullFrameThresholdPercent"/>, 40% by default) is where a tile stops paying
    /// for itself — and a Full frame doubles as a resync for a client that has been dropping.
    /// <para>The header always states the <em>guest</em> geometry, whatever the subscriber's max width scaled
    /// the pixels to, so a client can always map a click back to a guest coordinate.</para>
    /// </summary>
    public (DisplayFrameHeader Header, byte[] Payload)? BuildFrame(DisplaySubscriber subscriber, DisplayRect damage)
    {
        var clamped = damage.ClampTo(Surface.Width, Surface.Height);
        if (clamped.IsEmpty)
        {
            return null;
        }

        var surfaceArea = (long)Surface.Width * Surface.Height;
        var full = surfaceArea == 0 || clamped.Area * 100 >= surfaceArea * _options.FullFrameThresholdPercent;
        var region = full ? new DisplayRect(0, 0, Surface.Width, Surface.Height) : clamped;

        var quality = subscriber.Quality;
        // A tile is scaled by the SAME factor as the whole screen would be, not fitted to the subscriber's max
        // width itself — otherwise a 100px-wide tile would arrive magnified and land in the wrong place.
        var scale = DisplayJpeg.Fit(Surface.Width, Surface.Height, quality.MaxWidth).Width / (double)Surface.Width;
        int? regionMaxWidth = scale >= 1.0 ? null : Math.Max(1, (int)Math.Round(region.Width * scale));

        var pixels = Surface.CopyRegion(region);
        var jpeg = DisplayJpeg.Encode(pixels, region.Width, region.Height, regionMaxWidth, quality.JpegQuality);
        if (jpeg.Length == 0 || jpeg.Length > DisplayWire.MaxPayloadBytes)
        {
            _logger?.LogWarning(
                "Dropped a {Width}×{Height} display frame for session {SessionId}: {Bytes} bytes exceeds the wire cap.",
                region.Width, region.Height, _sessionId, jpeg.Length);
            return null;
        }

        var header = new DisplayFrameHeader(
            full ? DisplayFrameKind.Full : DisplayFrameKind.Tile,
            unchecked((uint)Interlocked.Increment(ref _frameSequence)),
            (ushort)region.X,
            (ushort)region.Y,
            (ushort)region.Width,
            (ushort)region.Height,
            (ushort)Surface.Width,
            (ushort)Surface.Height,
            (uint)jpeg.Length);
        return (header, jpeg);
    }

    // ---- what the tools see ----

    /// <summary>The whole screen as one JPEG. Never takes control: looking is not driving.</summary>
    public Task<byte[]> ScreenshotJpegAsync(int? maxWidth, int quality, CancellationToken cancellationToken = default)
    {
        Touch();
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = Surface.CopyAll();
        return Task.FromResult(DisplayJpeg.Encode(pixels, Surface.Width, Surface.Height, maxWidth, quality));
    }

    /// <summary>
    /// <paramref name="frames"/> screenshots spread evenly over <paramref name="span"/>. The point is motion:
    /// a single frame cannot tell a model whether a spinner is spinning, a page finished loading, or an
    /// animation settled — and "take a screenshot, wait, take another" costs a round trip per frame.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> BurstJpegAsync(
        int frames, TimeSpan span, int? maxWidth, int quality, CancellationToken cancellationToken = default)
    {
        Touch();
        var count = Math.Clamp(frames, 1, 12);
        var gap = count <= 1 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(0, span.Ticks) / (count - 1));
        var shots = new List<byte[]>(count);

        for (var i = 0; i < count; i++)
        {
            if (i > 0 && gap > TimeSpan.Zero)
            {
                await Task.Delay(gap, _time, cancellationToken).ConfigureAwait(false);
            }

            shots.Add(await ScreenshotJpegAsync(maxWidth, quality, cancellationToken).ConfigureAwait(false));
        }

        return shots;
    }

    /// <summary>Where the pointer was last put. Tracked here rather than asked of the guest: the capture seam
    /// injects absolute moves and has no read-back, so the last coordinate we sent IS the answer.</summary>
    public (int X, int Y) PointerPosition => (_pointerX, _pointerY);

    // ---- input ----

    /// <summary>
    /// The agent injects a sequence — a chord, a drag, a typed string — as one indivisible call: budget and
    /// control are checked once, up front, so a refused call injects <em>nothing</em> rather than half a
    /// keystroke that leaves a modifier stuck down in the guest.
    /// </summary>
    /// <exception cref="InvalidOperationException">A person holds the display, the per-call or per-minute
    /// budget is exhausted, or an interceptor vetoed the input. Every message is written for the model.</exception>
    public async Task InjectAgentAsync(IReadOnlyList<DisplayInput> inputs, CancellationToken cancellationToken = default)
    {
        Arbiter.CheckAgentCall(inputs.Count);

        await _injectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var claimed = Arbiter.ClaimForAgent();
            Arbiter.SpendBudget(inputs.Count);
            if (claimed is not null)
            {
                await PublishControlAsync(claimed, cancellationToken).ConfigureAwait(false);
            }

            foreach (var input in inputs)
            {
                var gate = await GateAsync(input, DisplayInputActor.Agent, cancellationToken).ConfigureAwait(false);
                if (gate is { Length: > 0 } reason)
                {
                    throw new InvalidOperationException($"That input was blocked: {reason}.");
                }

                await SendAsync(input, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _injectGate.Release();
        }

        Touch();
    }

    /// <summary>
    /// Presses, holds for <paramref name="hold"/>, and releases — with the injection gate held across the whole
    /// thing. A hold is the one action that has a duration, and it must be atomic: if another input slipped in
    /// between the press and the release, or the release were refused because control changed hands mid-hold,
    /// the guest would be left with a key stuck down and no way to notice.
    /// </summary>
    public async Task InjectAgentHeldAsync(
        IReadOnlyList<DisplayInput> down, TimeSpan hold, IReadOnlyList<DisplayInput> up, CancellationToken cancellationToken = default)
    {
        Arbiter.CheckAgentCall(down.Count + up.Count);

        await _injectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var claimed = Arbiter.ClaimForAgent();
            Arbiter.SpendBudget(down.Count + up.Count);
            if (claimed is not null)
            {
                await PublishControlAsync(claimed, cancellationToken).ConfigureAwait(false);
            }

            foreach (var input in down)
            {
                if (await GateAsync(input, DisplayInputActor.Agent, cancellationToken).ConfigureAwait(false) is { Length: > 0 } reason)
                {
                    throw new InvalidOperationException($"That input was blocked: {reason}.");
                }

                await SendAsync(input, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(hold, _time, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // The release is unconditional — not gated, not budgeted twice. Whatever went wrong during
                // the hold, the key comes back up.
                foreach (var input in up)
                {
                    await SendAsync(input, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _injectGate.Release();
        }

        Touch();
    }

    /// <summary>
    /// A person's input. Silently ignored (returning false) rather than thrown, because there is nowhere to
    /// show an exception on a hot input channel and a dropped mouse move is not worth a dialog — the client
    /// learns the state from the Control frames it already receives.
    /// </summary>
    /// <returns>False when this device does not hold the display, the budget is spent, or an interceptor
    /// vetoed the input.</returns>
    public async Task<bool> InjectUserAsync(DisplayInput input, string deviceId, CancellationToken cancellationToken = default)
    {
        await _injectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Arbiter.Holder != DisplayControlHolder.User
                || !string.Equals(Arbiter.HolderDeviceId, deviceId, StringComparison.Ordinal))
            {
                return false;
            }

            if (Arbiter.WouldExceedBudget(1))
            {
                return false;
            }

            if (await GateAsync(input, DisplayInputActor.User, cancellationToken).ConfigureAwait(false) is not null)
            {
                return false;
            }

            Arbiter.SpendBudget(1);
            Arbiter.NoteUserActivity();
            await SendAsync(input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _injectGate.Release();
        }

        Touch();
        return true;
    }

    /// <summary>A person takes or hands back the display; the change is logged and pushed to every watcher.</summary>
    public async Task<DisplayControlNotice> RequestControlAsync(string deviceId, bool take, CancellationToken cancellationToken = default)
    {
        var change = Arbiter.RequestControl(deviceId, take);
        if (change is not null)
        {
            await PublishControlAsync(change, cancellationToken).ConfigureAwait(false);
        }

        Touch();
        return Arbiter.Current;
    }

    /// <summary>Releases a hold whose device has gone away (its channel closed).</summary>
    public async Task ReleaseControlForAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (Arbiter.ReleaseFor(deviceId) is { } change)
        {
            await PublishControlAsync(change, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Expires a hold whose owner has stopped touching it. Called by the sweep.</summary>
    public async Task ReleaseIdleControlAsync(CancellationToken cancellationToken = default)
    {
        if (Arbiter.ReleaseIfIdle() is { } change)
        {
            await PublishControlAsync(change, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One control change, told twice on purpose: appended to the session log (persisted, replayed to a client
    /// that joins later, observable on the spine) AND pushed as a Control frame to everyone currently
    /// watching, who are on a channel that carries no log at all.
    /// </summary>
    private async Task PublishControlAsync(DisplayControlNotice notice, CancellationToken cancellationToken)
    {
        try
        {
            await _sink.AppendAsync(_sessionId, new DisplayControlChangedEvent(notice.Holder, notice.DeviceId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Losing the log entry must not lose the live state, or watchers would draw the wrong affordances.
            _logger?.LogWarning(ex, "Could not log a display-control change on session {SessionId}.", _sessionId);
        }

        lock (_subscribersGate)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Notice(notice);
            }
        }
    }

    /// <summary>The spine veto point. Returns the reason to refuse, or null to proceed.</summary>
    private async Task<string?> GateAsync(DisplayInput input, DisplayInputActor actor, CancellationToken cancellationToken)
    {
        var (kind, x, y, key) = Describe(input);
        var gate = await _bus.DispatchAsync(
            new BeforeDisplayInputEvent(_sessionId, actor, kind, x, y, key), cancellationToken).ConfigureAwait(false);
        return gate.IsCanceled ? gate.CancelReason ?? "no reason given" : null;
    }

    private async Task SendAsync(DisplayInput input, CancellationToken cancellationToken)
    {
        if (input is PointerMove move)
        {
            _pointerX = move.X;
            _pointerY = move.Y;
        }

        await _session.InjectAsync(input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Flattens a typed input into the shape <see cref="BeforeDisplayInputEvent"/> carries. The event is
    /// deliberately coarse — an interceptor needs "a key, here, named this", not the union. Per its contract
    /// <c>Key</c> is the X keysym for a key event and null for everything else, so a button event carries the
    /// pointer's position and no key; a pointer event's position is where it is going, a key event's is where
    /// the pointer already was.
    /// </summary>
    private (string Kind, int X, int Y, string? Key) Describe(DisplayInput input) => input switch
    {
        PointerMove m => ("move", m.X, m.Y, null),
        PointerButton => ("button", _pointerX, _pointerY, null),
        PointerScroll s => ("scroll", s.X, s.Y, null),
        KeyPress k => ("key", _pointerX, _pointerY, k.Key),
        _ => ("unknown", _pointerX, _pointerY, null),
    };

    private void Touch() => LastActivityUtc = _time.GetUtcNow();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Display pump for session {SessionId} ended with an error.", _sessionId);
        }

        DisplaySubscriber[] leftover;
        lock (_subscribersGate)
        {
            leftover = _subscribers.ToArray();
            _subscribers.Clear();
        }

        foreach (var subscriber in leftover)
        {
            subscriber.Dispose();
        }

        await _session.DisposeAsync().ConfigureAwait(false);
        _injectGate.Dispose();
        _cts.Dispose();
    }
}
