using System.Threading.Channels;

namespace Agnes.Sandbox;

// ---------------------------------------------------------------------------------------------------
// THE DISPLAY SEAM
//
// A graphical sandbox has one logical display, fixed at launch. Capture and input happen at the VM
// boundary — QEMU's D-Bus display on Incus — so the guest runs no Agnes software for this and the
// bytes come from host-trusted code; the guest controls only what is drawn. Everything above this
// file (the host broker, the MCP tools, the clients) is guest-agnostic: a Windows guest is a new
// implementation of IDisplaySource, not a change anywhere else.
//
// Coordinates everywhere are GUEST pixels. Scaling for a model or a phone is the consumer's job, and
// the consumer states the transform it used.
// ---------------------------------------------------------------------------------------------------

/// <summary>The display a graphical sandbox is launched with. Null on a <see cref="SandboxSpec"/> means headless.</summary>
/// <param name="Width">Guest pixels. 1280×800 is the default: inside every current model's screenshot
/// guidance, so an agent's screenshot needs no downscaling.</param>
public sealed record GraphicalDisplay(int Width = 1280, int Height = 800, int Dpi = 96)
{
    public static readonly GraphicalDisplay Default = new();
}

/// <summary>Pixel layout of a <see cref="DisplayUpdate"/>. QEMU hands out BGRx (little-endian xRGB) in practice.</summary>
public enum DisplayPixelFormat
{
    /// <summary>4 bytes per pixel, blue first, top byte unused.</summary>
    Bgrx32,
    /// <summary>4 bytes per pixel, red first, top byte unused.</summary>
    Rgbx32,
}

/// <summary>The display's fixed geometry.</summary>
public sealed record DisplayGeometry(int Width, int Height, DisplayPixelFormat Format);

/// <summary>
/// One damaged region of the display, or the whole surface (a scanout) when it covers it. Pixels are
/// rows of <see cref="Stride"/> bytes; the region is <see cref="Width"/>×<see cref="Height"/> at
/// (<see cref="X"/>, <see cref="Y"/>) in guest pixels.
/// </summary>
public sealed record DisplayUpdate(
    int X,
    int Y,
    int Width,
    int Height,
    int Stride,
    DisplayPixelFormat Format,
    ReadOnlyMemory<byte> Pixels,
    long Sequence,
    DateTimeOffset At)
{
    public bool IsFullFrame(DisplayGeometry g) => X == 0 && Y == 0 && Width == g.Width && Height == g.Height;
}

/// <summary>Which pointer button.</summary>
public enum PointerButtonKind
{
    Left,
    Middle,
    Right,
}

/// <summary>Input injected into the guest as emulated hardware. The guest cannot tell it from a real device.</summary>
public abstract record DisplayInput;

/// <summary>Absolute pointer move, guest pixels.</summary>
public sealed record PointerMove(int X, int Y) : DisplayInput;

public sealed record PointerButton(PointerButtonKind Button, bool Down) : DisplayInput;

/// <summary>Wheel movement at a position; positive <paramref name="Dy"/> scrolls down.</summary>
public sealed record PointerScroll(int X, int Y, int Dx, int Dy) : DisplayInput;

/// <summary>
/// A key, named the way X and xdotool name it (<c>Return</c>, <c>ctrl</c>, <c>a</c>, <c>KP_0</c>,
/// <c>F5</c>). That vocabulary is what every model with computer-use training has seen, so it passes
/// through untranslated from tool to display; the implementation owns the table to QEMU keycodes.
/// </summary>
public sealed record KeyPress(string Key, bool Down) : DisplayInput;

/// <summary>
/// A live connection to a sandbox's display: updates out, input in. One per sandbox; every consumer
/// (the human stream, the agent's screenshot) shares it, so two connections can never drift into two
/// displays by accident.
/// </summary>
public interface IDisplaySession : IAsyncDisposable
{
    DisplayGeometry Geometry { get; }

    /// <summary>Damaged regions as they happen. The first item after opening is always a full frame.</summary>
    ChannelReader<DisplayUpdate> Updates { get; }

    /// <summary>The current whole surface, <see cref="DisplayGeometry.Format"/>, tightly packed (stride = width × 4).</summary>
    Task<ReadOnlyMemory<byte>> SnapshotAsync(CancellationToken cancellationToken = default);

    Task InjectAsync(DisplayInput input, CancellationToken cancellationToken = default);
}

/// <summary>A sandbox that has a display. Optional capability, alongside <see cref="IPausableSandbox"/> and friends.</summary>
public interface IDisplaySource
{
    GraphicalDisplay Display { get; }

    Task<IDisplaySession> OpenDisplayAsync(CancellationToken cancellationToken = default);
}
