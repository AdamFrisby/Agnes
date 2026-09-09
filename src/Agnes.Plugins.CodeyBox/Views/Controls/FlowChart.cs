using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// The cumulative flow chart: landed at the bottom, what is still in flight above it, cancelled on top.
/// </summary>
/// <remarks>
/// It answers the question no single number can: is the fleet <em>finishing</em> things. The slope of
/// the landed band is throughput and the gap above it is work in progress, so a flat bottom band under a
/// widening gap is the picture of a fleet that is busy and producing nothing — which reads instantly here
/// and not at all from "42 in flight". Axis marks are deliberately three: the first day, the last day,
/// and the height of the stack. Anything more is a chart to study rather than to glance at.
/// </remarks>
public sealed class FlowChart : ThemedDrawing
{
    public static readonly StyledProperty<FlowSeries?> SeriesProperty =
        AvaloniaProperty.Register<FlowChart, FlowSeries?>(nameof(Series));

    static FlowChart() => AffectsRender<FlowChart>(SeriesProperty);

    public FlowSeries? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    private const double LabelSize = 9;
    private const double AxisRoom = 13;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 320 : Math.Max(160, availableSize.Width);
        return new Size(width, 120);
    }

    public override void Render(DrawingContext context)
    {
        var days = Series?.Days;
        if (days is not { Count: >= 2 } || Bounds.Width <= 24 || Bounds.Height <= AxisRoom + 8)
        {
            return;
        }

        var top = 12.0;
        var bottom = Bounds.Height - AxisRoom;
        var width = Bounds.Width;
        var peak = Math.Max(1, days.Max(d => d.Landed + d.InFlight + d.Cancelled));
        var step = width / (days.Count - 1);

        double Y(double stacked) => bottom - (stacked / peak * (bottom - top));
        double X(int i) => i * step;

        // Bottom-up, each band on the running total below it: landed is the floor because that is the
        // line the eye follows.
        DrawBand(context, days, X, Y, _ => 0, d => d.Landed, Roles.Mint, 0.85);
        DrawBand(context, days, X, Y, d => d.Landed, d => d.Landed + d.InFlight, Roles.Sky, 0.7);
        DrawBand(context, days, X, Y, d => d.Landed + d.InFlight, d => d.Landed + d.InFlight + d.Cancelled, Roles.Faint, 0.35);

        context.DrawLine(Pen(1, Roles.Line), new Point(0, bottom), new Point(width, bottom));

        var first = Label(days[0].Day.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture), LabelSize, Roles.Faint);
        var last = Label(days[^1].Day.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture), LabelSize, Roles.Faint);
        var height = Label(peak.ToString(System.Globalization.CultureInfo.CurrentCulture) + " items", LabelSize, Roles.Faint);

        context.DrawText(first, new Point(0, bottom + 2));
        context.DrawText(last, new Point(Math.Max(0, width - last.Width), bottom + 2));
        context.DrawText(height, new Point(0, 0));
    }

    private void DrawBand(
        DrawingContext context,
        IReadOnlyList<FlowPoint> days,
        Func<int, double> x,
        Func<double, double> y,
        Func<FlowPoint, double> lower,
        Func<FlowPoint, double> upper,
        string[] role,
        double opacity)
    {
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Point(x(0), y(upper(days[0]))), true);
            for (var i = 1; i < days.Count; i++)
            {
                sink.LineTo(new Point(x(i), y(upper(days[i]))));
            }

            for (var i = days.Count - 1; i >= 0; i--)
            {
                sink.LineTo(new Point(x(i), y(lower(days[i]))));
            }

            sink.EndFigure(true);
        }

        using (context.PushOpacity(opacity))
        {
            context.DrawGeometry(Brush(role), null, geometry);
        }
    }
}
