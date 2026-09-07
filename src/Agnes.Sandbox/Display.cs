using System.Threading.Channels;

namespace Agnes.Sandbox;

/// <summary>
/// A graphical display asked for on a sandbox: the guest gets a virtual GPU, an X server and a
/// session at exactly this size. Null on <see cref="SandboxSpec.Display"/> means headless.
/// </summary>
/// <remarks>
/// The size is fixed for the life of the VM. It is applied once at guest start (the session unit's
/// <c>xrandr</c>), because a resize mid-session is a whole extra protocol — the guest has to
/// re-mode-set, every client has to re-lay-out, and an agent that has already reasoned about
/// coordinates would be looking at a stale map. One size, decided when the sandbox is created.
/// </remarks>
public sealed record GraphicalDisplay(int Width, int Height, int Dpi = 96);

/// <summary>The size a display session actually came up at, as reported by the capture backend.</summary>
public readonly record struct DisplayGeometry(int Width, int Height, int Dpi);

/// <summary>Byte layout of a <see cref="DisplayUpdate"/>'s pixels.</summary>
/// <remarks>
/// Names describe memory order, little-endian: <see cref="Bgrx8888"/> is B,G,R,pad — what QEMU's
/// <c>PIXMAN_x8r8g8b8</c> and every ARGB32 surface on x86 actually holds, and what SkiaSharp's
/// <c>Bgra8888</c> expects. Anything else the backend must convert before it reaches this seam.
/// </remarks>
public enum DisplayPixelFormat
{
    /// <summary>32bpp, bytes B,G,R,unused.</summary>
    Bgrx8888,

    /// <summary>32bpp, bytes B,G,R,A.</summary>
    Bgra8888,
}

/// <summary>
/// A rectangle of new pixels. A full scanout (the surface was created or replaced) arrives as an
/// update covering the whole surface, so a consumer that only understands rectangles is complete:
/// there is no separate "scanout" event to special-case.
/// </summary>
/// <param name="Stride">Bytes per row *within <paramref name="Pixels"/>*, not within the surface.</param>
/// <param name="Sequence">Monotonic per session, from 1 — lets a client tell "no updates" from "missed updates".</param>
public sealed record DisplayUpdate(
    int X,
    int Y,
    int Width,
    int Height,
    int Stride,
    DisplayPixelFormat Format,
    ReadOnlyMemory<byte> Pixels,
    long Sequence,
    DateTimeOffset At);

/// <summary>Which pointer button an input event is about.</summary>
public enum PointerButtonKind
{
    Left,
    Middle,
    Right,
    Back,
    Forward,
}

/// <summary>One input event to inject into a display session. A closed union — see the records below.</summary>
public abstract record DisplayInput;

/// <summary>Move the pointer to an absolute surface coordinate.</summary>
public sealed record PointerMove(int X, int Y) : DisplayInput;

/// <summary>Press or release a pointer button (at wherever the pointer currently is).</summary>
public sealed record PointerButton(PointerButtonKind Button, bool Down) : DisplayInput;

/// <summary>Scroll at a position. <paramref name="Dx"/>/<paramref name="Dy"/> are wheel detents, not pixels;
/// positive <paramref name="Dy"/> scrolls down (the direction a wheel is pushed away from you).</summary>
public sealed record PointerScroll(int X, int Y, int Dx, int Dy) : DisplayInput;

/// <summary>
/// Press or release one key, named by its X keysym — <c>Return</c>, <c>ctrl</c>, <c>a</c>, <c>KP_0</c>,
/// the same vocabulary <c>xdotool key</c> takes and the one Anthropic's computer-use tool speaks.
/// </summary>
/// <remarks>
/// A keysym name, not a scancode, on purpose: it is the only spelling that is stable across capture
/// backends and that a model can be expected to produce. Translating it to whatever the backend wants
/// (QEMU key numbers, for the Incus backend) is the implementation's job, not the caller's.
/// </remarks>
public sealed record KeyPress(string Key, bool Down) : DisplayInput;

/// <summary>
/// A live capture of a sandbox's framebuffer, with input injection. Disposing detaches the capture;
/// it does not touch the sandbox.
/// </summary>
public interface IDisplaySession : IAsyncDisposable
{
    /// <summary>The surface size this session is delivering.</summary>
    DisplayGeometry Geometry { get; }

    /// <summary>Damage rectangles as they happen. Completes when the session ends.</summary>
    ChannelReader<DisplayUpdate> Updates { get; }

    /// <summary>Injects one input event. Coordinates are clamped to the surface by the implementation.</summary>
    Task InjectAsync(DisplayInput input, CancellationToken cancellationToken = default);

    /// <summary>The whole current surface, top-left origin, <see cref="DisplayPixelFormat.Bgrx8888"/>,
    /// <c>Geometry.Width * 4</c> bytes per row. A copy — safe to hold.</summary>
    Task<ReadOnlyMemory<byte>> SnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A sandbox whose framebuffer can be read and driven. Optional capability on <see cref="ISandbox"/>,
/// present only when the sandbox was created with <see cref="SandboxSpec.Display"/> set.
/// </summary>
public interface IDisplaySource
{
    Task<IDisplaySession> OpenDisplayAsync(CancellationToken cancellationToken = default);
}
