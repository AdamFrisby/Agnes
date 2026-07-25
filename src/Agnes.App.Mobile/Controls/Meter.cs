using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Agnes.App.Mobile.Controls;

/// <summary>
/// A horizontal fill bar for a 0..1 fraction — the context-window meter.
///
/// A custom control rather than a Border-inside-a-Border because the fill's width is a fraction of the
/// *measured* track, which XAML can't express without a converter that has to know the parent's size.
/// Renders nothing when <see cref="Fraction"/> is null, so a session whose agent never reports usage
/// shows no meter at all instead of a misleading empty one.
/// </summary>
public sealed class Meter : Control
{
    public static readonly StyledProperty<double?> FractionProperty =
        AvaloniaProperty.Register<Meter, double?>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(Fill));

    static Meter() => AffectsRender<Meter>(FractionProperty, TrackProperty, FillProperty);

    public Meter() => Height = 7;

    /// <summary>How full, 0..1. Null means "not reported" and draws nothing.</summary>
    public double? Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Fraction is not { } fraction || Bounds.Width <= 0)
        {
            return;
        }

        var radius = Bounds.Height / 2;

        if (Track is { } track)
        {
            context.DrawRectangle(track, null, new RoundedRect(new Rect(Bounds.Size), radius));
        }

        var width = Math.Clamp(fraction, 0, 1) * Bounds.Width;
        if (Fill is { } fill && width > 0)
        {
            // Never narrower than a full cap, so a 1% reading is still a visible sliver rather than a
            // squashed ellipse.
            width = Math.Max(width, Bounds.Height);
            context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, 0, width, Bounds.Height), radius));
        }
    }
}
