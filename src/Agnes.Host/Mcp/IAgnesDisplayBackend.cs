using Agnes.Sandbox;

namespace Agnes.Host.Mcp;

/// <summary>
/// What the <c>computer_*</c> tools need from the host, and nothing else: look at the screen, drive the
/// screen, ask where the pointer is. Separate from <see cref="IAgnesMcpBackend"/> because it is a separate
/// capability — most sessions have no display at all, and a tool that is present but always throws is worse
/// for a model than a tool that isn't offered.
/// </summary>
public interface IAgnesDisplayBackend
{
    /// <summary>Whether this session has a display the tools can act on.</summary>
    bool HasDisplay(string sessionId);

    /// <summary>The guest's fixed geometry, or null when the session has no display.</summary>
    Task<GraphicalDisplay?> GeometryAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>The whole screen as one JPEG, optionally scaled to <paramref name="maxWidth"/>.</summary>
    Task<DisplayShot> ScreenshotAsync(string sessionId, int? maxWidth, CancellationToken cancellationToken = default);

    /// <summary><paramref name="count"/> frames over <paramref name="span"/>, tiled into one contact sheet.</summary>
    Task<DisplayContactSheet> ContactSheetAsync(
        string sessionId, int count, TimeSpan span, int? maxWidth, CancellationToken cancellationToken = default);

    /// <summary>
    /// Injects a sequence as ONE agent action: the control and budget checks happen once, up front, so a
    /// refused call injects nothing rather than leaving a modifier stuck down in the guest.
    /// </summary>
    /// <exception cref="InvalidOperationException">A person holds the display, a budget is exhausted, a chord
    /// is blocked, or a plugin vetoed the input. The message is the tool's error text.</exception>
    Task InjectAsync(string sessionId, IReadOnlyList<DisplayInput> inputs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Types a string. Separate from <see cref="InjectAsync"/> because typing is budgeted per <em>keystroke</em>
    /// and bounded by <see cref="Display.DisplayOptions.MaxTypeBytes"/>, not by the per-call event ceiling: a
    /// character expands to two key events (four when shifted), so charging it as a chord would cap
    /// <c>computer_type</c> at sixteen characters and make its own byte ceiling unreachable.
    /// </summary>
    Task TypeAsync(string sessionId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Presses a key chord. Takes the <see cref="Display.KeyChord"/> rather than the events it expands to, so
    /// the operator's blocked-chord policy is applied where it is decided (the arbiter) instead of being
    /// re-derived from a flat list of key events in the tool layer.
    /// </summary>
    Task PressChordAsync(string sessionId, Display.KeyChord chord, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Presses one key, holds it, releases it — as one atomic action. A hold is the only input with a
    /// duration, so it cannot be expressed as a list of events; and the release has to be guaranteed even if
    /// something goes wrong mid-hold, or the guest is left with a key stuck down.
    /// </summary>
    Task HoldKeyAsync(string sessionId, Display.KeyChord key, TimeSpan hold, CancellationToken cancellationToken = default);

    /// <summary>Where the pointer was last put, in guest pixels.</summary>
    Task<(int X, int Y)> PointerPositionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>The limits the tools quote back to the model when they refuse something.</summary>
    Display.DisplayOptions Options { get; }
}

/// <summary>One screenshot: the JPEG, the size it actually is, and the guest size it was taken from.</summary>
public sealed record DisplayShot(byte[] Jpeg, int Width, int Height, int DisplayWidth, int DisplayHeight);

/// <summary>A burst tiled into one image, plus the grid and timing needed to read it.</summary>
public sealed record DisplayContactSheet(
    byte[] Jpeg, int Columns, int Rows, int Frames, int CellWidth, int CellHeight, int SpanMs, int DisplayWidth, int DisplayHeight);
