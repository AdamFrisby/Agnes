namespace Agnes.Host.Sessions;

/// <summary>
/// How an agent's one-line status is bounded (<c>Agnes:Status:*</c>).
/// <para>
/// Two limits, for two different failure modes. <see cref="MaxChars"/> exists because the status line is
/// rendered where there is room for a line and no more — a session row, a tab, a phone's list — so a model
/// that answers with a paragraph must be cut somewhere, and cutting it here (once, visibly, with the agent
/// told what happened) beats every client inventing its own truncation. <see cref="MinIntervalSeconds"/>
/// exists because the log is durable and replayed: an agent that reported on every tool call would bury its
/// own transcript in status lines. The window does not <i>drop</i> reports — the newest one is held and
/// written when the window closes — so the latest line always lands and a chatty agent is merely coalesced.
/// </para>
/// </summary>
public sealed record StatusOptions
{
    /// <summary>The default clip: 240 characters, about two sentences.</summary>
    public const int DefaultMaxChars = 240;

    /// <summary>
    /// <see cref="DefaultMaxChars"/> as text, because the tool description and the standing nudge are
    /// compile-time constant strings and cannot interpolate an <c>int</c>. A test asserts the two agree, so
    /// the number a model is told is always the number the normaliser applies.
    /// </summary>
    public const string DefaultMaxCharsText = "240";

    /// <summary>The default coalescing window: 20 seconds.</summary>
    public const int DefaultMinIntervalSeconds = 20;

    /// <summary>Longest status line kept, in characters. A non-positive value falls back to the default.</summary>
    public int MaxChars { get; init; } = DefaultMaxChars;

    /// <summary>Shortest gap between two written status lines, in seconds. Zero disables coalescing.</summary>
    public int MinIntervalSeconds { get; init; } = DefaultMinIntervalSeconds;

    /// <summary>The effective clip, so a misconfigured zero can't collapse every status to an ellipsis.</summary>
    public int EffectiveMaxChars => MaxChars > 0 ? MaxChars : DefaultMaxChars;

    /// <summary>The effective window. A negative value reads as "off", not as a window in the past.</summary>
    public TimeSpan MinInterval => MinIntervalSeconds > 0 ? TimeSpan.FromSeconds(MinIntervalSeconds) : TimeSpan.Zero;
}
