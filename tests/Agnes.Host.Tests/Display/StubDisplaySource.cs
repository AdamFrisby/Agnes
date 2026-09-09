using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Display;
using Agnes.Sandbox;

namespace Agnes.Host.Tests.Display;

/// <summary>
/// A VM-free display: a small synthetic surface, damage pushed on demand, and every injected input recorded.
/// <para>
/// Deliberately the <em>only</em> place these tests know how a display is produced. The sandbox side is
/// growing a richer <c>ScriptedDisplaySource</c> in <c>Agnes.TestKit</c>; when it lands, swapping to it is
/// one line — <see cref="DisplayFixture.NewSource"/> — and nothing else in this folder changes. The name is
/// different on purpose so the two can coexist during the handover.
/// </para>
/// </summary>
public sealed class StubDisplaySource : IDisplaySource
{
    public StubDisplaySource(int width = 64, int height = 48)
    {
        Display = new GraphicalDisplay(width, height);
        Session = new StubDisplaySession(width, height);
    }

    public GraphicalDisplay Display { get; }

    public StubDisplaySession Session { get; }

    /// <summary>How many times a broker has opened this source — the probe for "one capture connection".</summary>
    public int Opens { get; private set; }

    public Task<IDisplaySession> OpenDisplayAsync(CancellationToken cancellationToken = default)
    {
        Opens++;
        return Task.FromResult<IDisplaySession>(Session);
    }
}

/// <summary>The live half of <see cref="StubDisplaySource"/>: a surface a test paints into by hand.</summary>
public sealed class StubDisplaySession : IDisplaySession
{
    private readonly Channel<DisplayUpdate> _updates = Channel.CreateUnbounded<DisplayUpdate>();
    private long _sequence;

    public StubDisplaySession(int width, int height)
        => Geometry = new DisplayGeometry(width, height, DisplayPixelFormat.Bgrx32);

    public DisplayGeometry Geometry { get; }

    public ChannelReader<DisplayUpdate> Updates => _updates.Reader;

    /// <summary>Every input this "guest" was given, in order.</summary>
    public List<DisplayInput> Injected { get; } = [];

    /// <summary>Set to throw from <see cref="InjectAsync"/>, for the failure paths.</summary>
    public Exception? InjectFault { get; set; }

    public bool Disposed { get; private set; }

    public Task<ReadOnlyMemory<byte>> SnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ReadOnlyMemory<byte>>(Fill(Geometry.Width, Geometry.Height, 0x20));

    public Task InjectAsync(DisplayInput input, CancellationToken cancellationToken = default)
    {
        if (InjectFault is { } fault)
        {
            throw fault;
        }

        lock (Injected)
        {
            Injected.Add(input);
        }

        return Task.CompletedTask;
    }

    /// <summary>Paints a solid rectangle and publishes it as one damage update.</summary>
    public void Paint(int x, int y, int width, int height, byte value = 0xC0)
        => _updates.Writer.TryWrite(new DisplayUpdate(
            x, y, width, height, width * 4, DisplayPixelFormat.Bgrx32,
            Fill(width, height, value), Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow));

    /// <summary>A snapshot of what was injected, taken under the same lock the injector writes under.</summary>
    public IReadOnlyList<DisplayInput> Snapshot()
    {
        lock (Injected)
        {
            return Injected.ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _updates.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static byte[] Fill(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, value);
        return pixels;
    }
}

/// <summary>A session source the tests point at one stub display, with a turn flag they can flip.</summary>
public sealed class StubSessionSource : IDisplaySessionSource
{
    private readonly Dictionary<string, StubDisplaySource> _displays = new(StringComparer.Ordinal);

    public HashSet<string> ActiveTurns { get; } = new(StringComparer.Ordinal);

    public StubDisplaySource Add(string sessionId, int width = 64, int height = 48)
    {
        var source = new StubDisplaySource(width, height);
        _displays[sessionId] = source;
        return source;
    }

    public IDisplaySource? DisplaySourceFor(string sessionId)
        => _displays.TryGetValue(sessionId, out var source) ? source : null;

    public bool IsTurnActive(string sessionId) => ActiveTurns.Contains(sessionId);
}

/// <summary>Records the control handovers that would have gone into the session log.</summary>
public sealed class RecordingControlSink : IDisplayControlSink
{
    public List<DisplayControlChangedEvent> Appended { get; } = [];

    public Task AppendAsync(string sessionId, DisplayControlChangedEvent changed, CancellationToken cancellationToken = default)
    {
        lock (Appended)
        {
            Appended.Add(changed);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<DisplayControlChangedEvent> Snapshot()
    {
        lock (Appended)
        {
            return Appended.ToArray();
        }
    }
}

/// <summary>A clock the tests move by hand, so idle timeouts and budgets are deterministic.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Assembles the pieces the display tests share, so each test states only what it is about.</summary>
public static class DisplayFixture
{
    public const string Session = "sess-display";

    /// <summary>THE seam to the fake display. Swap this one line for <c>ScriptedDisplaySource</c> when the
    /// TestKit version lands.</summary>
    public static StubDisplaySource NewSource(StubSessionSource sessions, string sessionId = Session, int width = 64, int height = 48)
        => sessions.Add(sessionId, width, height);

    public static DisplayOptions Options() => new()
    {
        MaxFps = 60,          // the tests pace themselves; don't make them wait on a frame budget.
        JpegQuality = 60,
        ControlIdleSeconds = 60,
    };

    /// <summary>A real broker registry over the stub source — the tests exercise the production wiring, not a
    /// mock of it, because "does the tool reach the same surface the watcher sees" is the interesting part.</summary>
    public static DisplayBrokerRegistry Registry(StubSessionSource sessions, DisplayOptions? options = null, TimeProvider? time = null)
        => new(sessions, options ?? Options(), new EventBus(), new RecordingControlSink(), loggers: null, time: time);

    /// <summary>A display backend for a host that has no graphical sessions at all — what most tests want.</summary>
    public static Agnes.Host.Mcp.IAgnesDisplayBackend NoDisplays()
    {
        var options = Options();
        return new Agnes.Host.Mcp.BrokerDisplayBackend(Registry(new StubSessionSource(), options), options);
    }

    public static async Task<(DisplayBroker Broker, StubDisplaySource Source, RecordingControlSink Sink, ManualClock Clock)>
        BrokerAsync(DisplayOptions? options = null)
    {
        var sessions = new StubSessionSource();
        var source = NewSource(sessions);
        var sink = new RecordingControlSink();
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var session = await source.OpenDisplayAsync();
        var broker = new DisplayBroker(
            Session, session, source.Display, options ?? Options(), new EventBus(), sink, logger: null, time: clock);
        await broker.StartAsync();
        return (broker, source, sink, clock);
    }
}
