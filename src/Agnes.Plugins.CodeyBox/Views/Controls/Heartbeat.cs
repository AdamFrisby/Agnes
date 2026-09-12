using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// Events per minute over the last hour: one bar per minute, oldest on the left, the minute in progress
/// on the right.
/// </summary>
/// <remarks>
/// <para>A real timeline, not a visualiser. The bars are counts of what the orchestrator actually
/// emitted, so a quiet fleet draws a quiet chart — which is the only way a busy one means anything. The
/// window rolls with the clock rather than filling up, so the chart moves even when nothing happens, and
/// that movement is honest: a minute of silence is a minute of silence, drawn.</para>
///
/// <para>Bars rather than a line, because the rightmost minute is always partial. A dipping line at the
/// right edge reads as a fleet falling over; a short bar reads as a minute that has not finished.</para>
///
/// <para>The scale is the window's own peak, stated by the caller beside the chart. An absolute scale
/// would make every ordinary hour a flat line at the bottom, which is the failure this shape exists to
/// avoid.</para>
/// </remarks>
public sealed class Heartbeat : ThemedDrawing
{
    public static readonly StyledProperty<IReadOnlyList<int>?> MinutesProperty =
        AvaloniaProperty.Register<Heartbeat, IReadOnlyList<int>?>(nameof(Minutes));

    static Heartbeat() => AffectsRender<Heartbeat>(MinutesProperty);

    /// <summary>Counts per minute, oldest first. Anything shorter than the control simply draws
    /// narrower bars; there is no padding with invented zeroes.</summary>
    public IReadOnlyList<int>? Minutes
    {
        get => GetValue(MinutesProperty);
        set => SetValue(MinutesProperty, value);
    }

    private const double AxisRoom = 12;
    private const double LabelSize = 9;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 360 : Math.Max(120, availableSize.Width);
        return new Size(width, 96);
    }

    public override void Render(DrawingContext context)
    {
        var minutes = Minutes;
        if (minutes is not { Count: > 0 } || Bounds.Width <= 24 || Bounds.Height <= AxisRoom + 6)
        {
            return;
        }

        var bottom = Bounds.Height - AxisRoom;
        var top = 2.0;
        var peak = Math.Max(1, minutes.Max());
        var step = Bounds.Width / minutes.Count;
        var barWidth = Math.Max(1, step - 1.5);

        // The floor: a hairline the bars stand on, so an hour of nothing is still a chart.
        context.DrawLine(Pen(1, Roles.Line), new Point(0, bottom + 0.5), new Point(Bounds.Width, bottom + 0.5));

        for (var i = 0; i < minutes.Count; i++)
        {
            var count = Math.Max(0, minutes[i]);
            var x = i * step;
            if (count == 0)
            {
                // A silent minute is a fact, and is marked as one: a pip on the floor rather than a gap
                // that could equally mean "no data".
                using (context.PushOpacity(0.5))
                {
                    context.DrawRectangle(Brush(Roles.Faint), null, new Rect(x, bottom - 1, barWidth, 1));
                }

                continue;
            }

            var height = Math.Max(2, count / (double)peak * (bottom - top));
            var rect = new Rect(x, bottom - height, barWidth, height);

            // The trailing bar is the minute in progress: outlined rather than filled, so a partial
            // count is never read as a finished one.
            if (i == minutes.Count - 1)
            {
                context.DrawRectangle(null, Pen(1, Roles.Sky), new RoundedRect(rect.Deflate(0.5), 1));
                continue;
            }

            context.DrawRectangle(Brush(Roles.Sky), null, new RoundedRect(rect, 1));
        }

        var left = Label("60m ago", LabelSize, Roles.Faint);
        context.DrawText(left, new Point(0, bottom + 1));
        var right = Label("now", LabelSize, Roles.Faint);
        context.DrawText(right, new Point(Bounds.Width - right.Width, bottom + 1));
    }
}
