using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Agnes.App.Mobile.Controls;

/// <summary>
/// Strokes one of the 24×24 Lucide-style geometries from <c>Themes/Icons.axaml</c> at a given size
/// and colour, at the brand's 1.75px stroke with round caps and joins. Call sites read as
/// <c>&lt;c:Icon Data="{StaticResource IconSend}" /&gt;</c> rather than repeating stretch/stroke/cap
/// plumbing on every glyph.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 22d);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Icon, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.75d);

    /// <summary>When set, the geometry is filled with this brush as well as stroked — used for the
    /// "selected" state of the bottom-nav icons.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Icon, IBrush?>(nameof(Fill));

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeProperty, StrokeThicknessProperty, FillProperty);
        AffectsMeasure<Icon>(SizeProperty, DataProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry)
        {
            return;
        }

        // The geometries are authored on a 24×24 grid; scale uniformly to the requested size and keep the
        // stroke visually constant by scaling it inversely.
        var scale = Size / 24d;
        var pen = Stroke is null ? null : new Pen(Stroke, StrokeThickness / scale)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale));
        context.DrawGeometry(Fill, pen, geometry);
    }
}
