using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// The role chains the board's drawn controls ask for, in the app's own vocabulary.
/// </summary>
/// <remarks>
/// <see cref="ThemedDrawing"/> keeps the same list <c>protected</c> so that only a control can reach it,
/// which is right for a control but leaves the shared state-to-hue mapping — used by two controls and by
/// nothing else — with nowhere to live. Naming the chains once here is the smaller duplication: the
/// alternative is the same <c>switch</c> written twice, which is how a strip and a row mark end up
/// disagreeing about what mint means.
/// </remarks>
internal static class ThemedRoles
{
    /// <summary>In motion.</summary>
    internal static readonly string[] Sky = ["StatusWorking", "Accent"];

    /// <summary>Blocked on you.</summary>
    internal static readonly string[] Amber = ["StatusAttention", "Accent"];

    /// <summary>Done / healthy.</summary>
    internal static readonly string[] Mint = ["StatusDone", "Accent"];

    /// <summary>Failed or destructive.</summary>
    internal static readonly string[] Pink = ["StatusError", "Danger"];

    /// <summary>Present, but not asking for anything.</summary>
    internal static readonly string[] Faint = ["StatusIdle", "FgFaint"];

    /// <summary>The text hue, used for the ring around the step the pane is showing.</summary>
    internal static readonly string[] Fg = ["Fg"];
}

/// <summary>How a pip is drawn: the shape carries as much meaning as the hue does.</summary>
internal enum PipFill
{
    /// <summary>Something happened, or is happening. The loudest of the three.</summary>
    Solid,

    /// <summary>Ready to happen, or held back. Present but not asserting.</summary>
    Outline,

    /// <summary>Deliberately not going to happen.</summary>
    Dashed,
}

/// <summary>
/// The one place where a <see cref="StepState"/> becomes a hue and a shape.
/// </summary>
/// <remarks>
/// <para>One mapping, shared by the strip, by the mark at the head of a row and by a relation chip,
/// because the app's rule is one meaning per hue: a state drawn pink in a strip and amber in the row
/// directly above it teaches the operator that the colours are decorative. So mint landed, sky in
/// motion, amber waiting on a person, pink failed, faint present but not asking for anything.</para>
///
/// <para>Shape carries the rest, which is what lets eight states share five hues: solid for something
/// that happened or is happening, hollow for something merely queued or held, dashed for something that
/// deliberately will not happen. Ready and Blocked are both hollow and differ only in hue; Ready and
/// Parked are both quiet and differ only in fill.</para>
/// </remarks>
internal static class StepMarks
{
    /// <summary>A step's state, as a pip: hue for what it is, shape for how much it is asserting.</summary>
    internal static (string[] Role, PipFill Fill) Of(StepState state) => state switch
    {
        StepState.Done => (ThemedRoles.Mint, PipFill.Solid),
        StepState.Running => (ThemedRoles.Sky, PipFill.Solid),
        StepState.Ready => (ThemedRoles.Sky, PipFill.Outline),
        StepState.Blocked => (ThemedRoles.Faint, PipFill.Outline),
        StepState.Parked => (ThemedRoles.Faint, PipFill.Solid),
        StepState.Failed => (ThemedRoles.Pink, PipFill.Solid),
        StepState.Cancelled => (ThemedRoles.Faint, PipFill.Dashed),
        StepState.NeedsPerson => (ThemedRoles.Amber, PipFill.Solid),
        _ => (ThemedRoles.Faint, PipFill.Outline),
    };

    /// <summary>What a step reads as in a tooltip or a chip: the enum, in the operator's words.</summary>
    internal static string Word(StepState state) => state switch
    {
        StepState.NeedsPerson => "needs a person",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>Draws one pip, shape and hue together.</summary>
    internal static void Draw(DrawingContext context, Rect rect, IBrush brush, PipFill fill)
    {
        var rounded = new RoundedRect(rect, 2.5);
        switch (fill)
        {
            case PipFill.Solid:
                context.DrawRectangle(brush, null, rounded);
                break;
            case PipFill.Outline:
                context.DrawRectangle(null, new Pen(brush, 1.2), new RoundedRect(rect.Deflate(0.6), 2.2));
                break;
            default:
                context.DrawRectangle(null, new Pen(brush, 1.2) { DashStyle = new DashStyle([1.6, 1.6], 0) },
                                      new RoundedRect(rect.Deflate(0.6), 2.2));
                break;
        }
    }
}
