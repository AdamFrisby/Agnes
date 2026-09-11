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
    private const double TopRoom = 4;
    private const double CountRoom = 36;
    private const int TickEveryDays = 7;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 320 : Math.Max(160, availableSize.Width);
        return new Size(width, 120);
    }

    public override void Render(DrawingContext context)
    {
        var series = Series;
        var days = series?.Days;
        if (series is null || days is not { Count: >= 2 } || Bounds.Width <= 24 + CountRoom || Bounds.Height <= AxisRoom + 8)
        {
            return;
        }

        var top = TopRoom;
        var bottom = Bounds.Height - AxisRoom;
        var plotWidth = Bounds.Width - CountRoom;
        // The axis starts at what had already landed when the window opened, not at zero: a month of
        // work drawn on top of a year of it is a sliver, and the sliver is the part being asked about.
        double floor = series.Floor;
        // Cancelled is cumulative too, and most of it is old; only what was cancelled inside the window
        // belongs on top of the stack.
        double cancelledBefore = days[0].Cancelled;
        double Top(FlowPoint d) => d.Landed + d.InFlight + (d.Cancelled - cancelledBefore);
        var peak = Math.Max(floor + 1, days.Max(Top));
        var step = plotWidth / (days.Count - 1);
        double Y(double stacked) => bottom - (Math.Max(0, stacked - floor) / (peak - floor) * (bottom - top));
        double X(int i) => i * step;

        // Week lines behind the bands, counted back from today so the last one is always today.
        for (var i = days.Count - 1; i >= 0; i -= TickEveryDays)
        {
            using (context.PushOpacity(0.6))
            {
                context.DrawLine(Pen(1, Roles.Line), new Point(X(i), top), new Point(X(i), bottom));
            }
        }

        // Bottom-up, each band on the running total below it: landed is the floor because that is the
        // line the eye follows.
        DrawBand(context, days, X, Y, _ => floor, d => d.Landed, Roles.Mint, 0.85);
        DrawBand(context, days, X, Y, d => d.Landed, d => d.Landed + d.InFlight, Roles.Sky, 0.7);
        DrawBand(context, days, X, Y, d => d.Landed + d.InFlight, Top, Roles.Faint, 0.35);
        context.DrawLine(Pen(1, Roles.Line), new Point(0, bottom), new Point(plotWidth, bottom));

        // Dates under every week line, dropped where they would collide.
        var lastRight = double.NegativeInfinity;
        for (var i = 0; i < days.Count; i++)
        {
            if ((days.Count - 1 - i) % TickEveryDays != 0)
            {
                continue;
            }
            var label = Label(days[i].Day.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture), LabelSize, Roles.Faint);
            var x = Math.Clamp(X(i) - (label.Width / 2), 0, Math.Max(0, plotWidth - label.Width));
            if (x < lastRight + 6)
            {
                continue;
            }
            context.DrawText(label, new Point(x, bottom + 2));
            lastRight = x + label.Width;
        }

        // The scale, in items, at the right edge: the top of the stack and the floor it is drawn from.
        var peakLabel = Label(((int)peak).ToString(System.Globalization.CultureInfo.InvariantCulture), LabelSize, Roles.Faint);
        context.DrawText(peakLabel, new Point(plotWidth + 4, top));
        if (floor > 0)
        {
            var floorLabel = Label(((int)floor).ToString(System.Globalization.CultureInfo.InvariantCulture), LabelSize, Roles.Faint);
            context.DrawText(floorLabel, new Point(plotWidth + 4, bottom - floorLabel.Height));
        }
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
