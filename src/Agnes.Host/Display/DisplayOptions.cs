namespace Agnes.Host.Display;

/// <summary>
/// Host-level knobs for the graphical-sandbox display channel, bound from <c>Agnes:Display:*</c>. Every
/// default is chosen to be safe on a shared host: the channel costs nothing until somebody opens it, a
/// person's claim on the pointer expires on its own, and an agent cannot spend the session's input budget
/// faster than a human could plausibly watch it.
/// </summary>
public sealed record DisplayOptions
{
    /// <summary>How long a person may hold the display without touching it before control falls back to
    /// <see cref="Agnes.Abstractions.DisplayControlHolder.None"/>. Without this, closing a laptop lid would
    /// leave the agent locked out of its own screen indefinitely.</summary>
    public int ControlIdleSeconds { get; init; } = 60;

    /// <summary>Ceiling on a subscriber's requested frame rate. A client asking for more is clamped.</summary>
    public int MaxFps { get; init; } = 15;

    /// <summary>Default JPEG quality (1–100) when a subscriber or tool doesn't state one.</summary>
    public int JpegQuality { get; init; } = 75;

    /// <summary>
    /// A damaged region covering more than this share of the surface is sent as one Full frame instead of a
    /// Tile: past roughly this point the tile buys nothing, and a Full frame also re-syncs a client that has
    /// been dropping frames.
    /// </summary>
    public int FullFrameThresholdPercent { get; init; } = 40;

    /// <summary>Injected input events allowed per session per rolling minute, across the agent and every
    /// person. The ceiling on "the agent got stuck in a loop mashing the keyboard".</summary>
    public int InputEventsPerMinute { get; init; } = 240;

    /// <summary>Input events one agent tool call may expand to (a chord, a drag, a typed string).</summary>
    public int InputEventsPerToolCall { get; init; } = 32;

    /// <summary>UTF-8 bytes <c>computer_type</c> accepts in one call.</summary>
    public int MaxTypeBytes { get; init; } = 4096;

    /// <summary>Longest a <c>computer_wait</c> may sleep, in milliseconds.</summary>
    public int MaxWaitMs { get; init; } = 10_000;

    /// <summary>
    /// Key chords the <b>agent</b> may not press, matched against the normalized form
    /// <see cref="KeyChord.Normalize"/> produces; a trailing <c>*</c> matches any key after the modifiers.
    /// Applied to the agent only: a person driving the display is already authorized to do anything the guest
    /// allows, and blocking their window manager would simply be broken. The list is what stops an agent
    /// leaving the app under test — dropping to a TTY, opening a run dialog, or bringing up devtools.
    /// </summary>
    public IReadOnlyList<string> BlockedChords { get; init; } = DefaultBlockedChords;

    public static readonly IReadOnlyList<string> DefaultBlockedChords =
    [
        "super",
        "super+*",
        "meta",
        "meta+*",
        "ctrl+alt+*",
        "alt+f2",
        "ctrl+shift+i",
    ];
}
