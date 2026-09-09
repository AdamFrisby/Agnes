using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// The first thing a row says: is this item doing anything.
/// </summary>
/// <remarks>
/// Filled for a state the fleet arrived at on its own (moving, blocked, wedged); an outline for parked,
/// because parked is a decision with a known resume and reads as quieter than the others by being
/// hollow rather than by being a paler shade of the same hue.
/// </remarks>
public sealed class MotionDot : ThemedDrawing
{
    public static readonly StyledProperty<Motion> MotionProperty =
        AvaloniaProperty.Register<MotionDot, Motion>(nameof(Motion));

    static MotionDot() => AffectsRender<MotionDot>(MotionProperty);

    public Motion Motion
    {
        get => GetValue(MotionProperty);
        set => SetValue(MotionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(9, 9);

    public override void Render(DrawingContext context)
    {
        var brush = Brush(MotionRole(Motion));
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 1;
        if (radius <= 0)
        {
            return;
        }

        if (Motion == Motion.Parked)
        {
            context.DrawEllipse(null, new Pen(brush, 1.4), centre, radius, radius);
            return;
        }

        context.DrawEllipse(brush, null, centre, radius, radius);
    }

    /// <summary>One meaning per hue, applied to motion: sky moving, amber blocked on you, pink wedged.</summary>
    internal static string[] MotionRole(Motion motion) => motion switch
    {
        Motion.Moving => Roles.Sky,
        Motion.Blocked => Roles.Amber,
        Motion.Wedged => Roles.Pink,
        _ => Roles.Faint,
    };
}
