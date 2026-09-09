using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// The mark at the head of a runway row: what this chain is doing, and whether it wants anything.
/// </summary>
/// <remarks>
/// A sibling of <see cref="MotionDot"/> rather than a reuse of it, because the two answer different
/// questions from different vocabularies — <c>Motion</c> has three states and no notion of "landed",
/// while a runway row has to distinguish done from ready from parked. Same shape and same
/// <see cref="StepMarks"/> mapping as a strip pip, deliberately: the head IS one of the steps, so a row
/// whose mark disagreed with its own strip would be teaching the operator that the colours mean nothing.
/// </remarks>
public sealed class StepDot : ThemedDrawing
{
    public static readonly StyledProperty<StepState> StateProperty =
        AvaloniaProperty.Register<StepDot, StepState>(nameof(State));

    static StepDot() => AffectsRender<StepDot>(StateProperty);

    public StepState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(9, 9);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height) - 1;
        if (size <= 0)
        {
            return;
        }

        var rect = new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);
        var (role, fill) = StepMarks.Of(State);
        StepMarks.Draw(context, rect, Brush(role), fill);
    }
}
