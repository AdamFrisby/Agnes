using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// A vital's recent history against its own norm: the control band behind, the series through it, and
/// where it ended up.
/// </summary>
/// <remarks>
/// The band is what makes the number readable. "3 landed today" is a collapse for a fleet that normally
/// lands twelve and an ordinary Tuesday for one that normally lands four, and only the trailing band
/// says which — so the band is drawn first, behind everything, and the tone is spent only on the last
/// point and the trend chevron. The chevron is drawn, never typed: a text arrow is a glyph the brand
/// fonts do not carry, and it could not take a status hue if it were.
/// </remarks>
public sealed class Sparkline : ThemedDrawing
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<double?> BandLowProperty =
        AvaloniaProperty.Register<Sparkline, double?>(nameof(BandLow));

    public static readonly StyledProperty<double?> BandHighProperty =
        AvaloniaProperty.Register<Sparkline, double?>(nameof(BandHigh));

    public static readonly StyledProperty<double?> MedianProperty =
        AvaloniaProperty.Register<Sparkline, double?>(nameof(Median));

    public static readonly StyledProperty<Trend> TrendProperty =
        AvaloniaProperty.Register<Sparkline, Trend>(nameof(Trend));

    public static readonly StyledProperty<TileTone> ToneProperty =
        AvaloniaProperty.Register<Sparkline, TileTone>(nameof(Tone));

    static Sparkline() => AffectsRender<Sparkline>(
        ValuesProperty, BandLowProperty, BandHighProperty, MedianProperty, TrendProperty, ToneProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double? BandLow
    {
        get => GetValue(BandLowProperty);
        set => SetValue(BandLowProperty, value);
    }

    public double? BandHigh
    {
        get => GetValue(BandHighProperty);
        set => SetValue(BandHighProperty, value);
    }

    public double? Median
    {
        get => GetValue(MedianProperty);
        set => SetValue(MedianProperty, value);
    }

    public Trend Trend
    {
        get => GetValue(TrendProperty);
        set => SetValue(TrendProperty, value);
    }

    public TileTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(120, 26);

    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (Bounds.Width <= 16 || Bounds.Height <= 6)
        {
            return;
        }

        // The chevron lives in its own column so the series is never drawn under it.
        var chartWidth = Math.Max(0, Bounds.Width - 12);
        var top = 2.0;
        var bottom = Bounds.Height - 2;

        var (low, high) = Extent(values);
        double Y(double value) => high - low <= 0 ? (top + bottom) / 2 : bottom - ((value - low) / (high - low) * (bottom - top));

        DrawBand(context, Y, chartWidth);

        if (values is { Count: >= 2 } && chartWidth > 0)
        {
            var step = chartWidth / (values.Count - 1);
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                sink.BeginFigure(new Point(0, Y(values[0])), false);
                for (var i = 1; i < values.Count; i++)
                {
                    sink.LineTo(new Point(i * step, Y(values[i])));
                }

                sink.EndFigure(false);
            }

            context.DrawGeometry(null, Pen(1.5, Roles.Dim), geometry);
            context.DrawEllipse(Brush(ToneRole(Tone)), null, new Point(chartWidth, Y(values[^1])), 2.5, 2.5);
        }

        DrawChevron(context, chartWidth);
    }

    private void DrawBand(DrawingContext context, Func<double, double> y, double width)
    {
        if (BandLow is not { } low || BandHigh is not { } high || width <= 0)
        {
            return;
        }

        var topY = y(Math.Max(low, high));
        var bottomY = y(Math.Min(low, high));
        // A wash of the faint role rather than a panel fill: it has to read on a tile that is already
        // Panel-coloured, and on a light theme where PanelAlt is barely a shade off the tile itself.
        using (context.PushOpacity(0.22))
        {
            context.DrawRectangle(Brush(Roles.Faint), null,
                new Rect(0, topY, width, Math.Max(1, bottomY - topY)));
        }

        if (Median is { } median)
        {
            var line = y(median);
            using (context.PushOpacity(0.7))
            {
                context.DrawLine(Pen(1, Roles.Faint), new Point(0, line), new Point(width, line));
            }
        }
    }

    private void DrawChevron(DrawingContext context, double chartWidth)
    {
        // Flat and Unknown draw nothing: an arrow that means "no idea" is worse than no arrow.
        if (Trend is not (Trend.Up or Trend.Down))
        {
            return;
        }

        var pen = Pen(1.6, ToneRole(Tone));
        var midY = Bounds.Height / 2;
        var left = chartWidth + 3;
        var right = Bounds.Width - 1;
        var apexX = (left + right) / 2;
        var reach = 3.0;

        var apexY = Trend == Trend.Up ? midY - reach : midY + reach;
        var footY = Trend == Trend.Up ? midY + reach : midY - reach;

        context.DrawLine(pen, new Point(left, footY), new Point(apexX, apexY));
        context.DrawLine(pen, new Point(apexX, apexY), new Point(right, footY));
    }

    private (double Low, double High) Extent(IReadOnlyList<double>? values)
    {
        var low = double.MaxValue;
        var high = double.MinValue;

        void Consider(double v)
        {
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        if (values is not null)
        {
            foreach (var v in values)
            {
                Consider(v);
            }
        }

        if (BandLow is { } bl)
        {
            Consider(bl);
        }

        if (BandHigh is { } bh)
        {
            Consider(bh);
        }

        if (Median is { } m)
        {
            Consider(m);
        }

        if (low > high)
        {
            return (0, 1);
        }

        // Padding is a fraction of the series' own spread, never an absolute: a vital measured in
        // percentages (0.04 → 0.12) shares this control with one measured in items (21 → 34), and a
        // constant pad flattens the first into a straight line.
        var spread = high - low;
        var pad = spread > 0 ? spread * 0.15 : Math.Max(0.5, Math.Abs(high) * 0.1);
        return (low - pad, high + pad);
    }

    internal static string[] ToneRole(TileTone tone) => tone switch
    {
        TileTone.Active => Roles.Sky,
        TileTone.Attention => Roles.Amber,
        TileTone.Bad => Roles.Pink,
        _ => Roles.Dim,
    };
}
