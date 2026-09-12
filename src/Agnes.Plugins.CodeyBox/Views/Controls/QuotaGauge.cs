using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// One quota window as a vertical gauge: a track the height of the card's chart, filled from the bottom
/// to the percentage still available.
/// </summary>
/// <remarks>
/// A provider publishes several overlapping windows and only the longest is worth a curve; the shorter
/// ones — a five-hour window inside a seven-day one — refill many times before the big one does, so
/// their history is a sawtooth that says little. What matters about them is where they stand now, and a
/// bar beside the big window's chart says that in a glance without pretending to be a chart. The hue
/// follows the card: sky where the router would dispatch to this agent, faint where it would not.
/// </remarks>
public sealed class QuotaGauge : ThemedDrawing
{
    public static readonly StyledProperty<double?> PctProperty =
        AvaloniaProperty.Register<QuotaGauge, double?>(nameof(Pct));

    public static readonly StyledProperty<bool> EligibleProperty =
        AvaloniaProperty.Register<QuotaGauge, bool>(nameof(Eligible));

    static QuotaGauge() => AffectsRender<QuotaGauge>(PctProperty, EligibleProperty);

    /// <summary>Percent available, 0–100; null draws an empty track.</summary>
    public double? Pct
    {
        get => GetValue(PctProperty);
        set => SetValue(PctProperty, value);
    }

    public bool Eligible
    {
        get => GetValue(EligibleProperty);
        set => SetValue(EligibleProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(10, 46);

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 2 || Bounds.Height <= 4)
        {
            return;
        }

        var track = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.DrawRectangle(null, Pen(1, Roles.Line), new RoundedRect(track.Deflate(0.5), 3));
        if (Pct is not { } pct)
        {
            return;
        }

        var filled = Math.Clamp(pct, 0, 100) / 100 * (Bounds.Height - 2);
        if (filled < 1)
        {
            // Empty is a fact worth a mark: a hairline on the floor rather than nothing.
            filled = 1;
        }
        var fill = new Rect(1, Bounds.Height - 1 - filled, Bounds.Width - 2, filled);
        context.DrawRectangle(Brush(Eligible ? Roles.Sky : Roles.Faint), null, new RoundedRect(fill, 2));
    }
}
