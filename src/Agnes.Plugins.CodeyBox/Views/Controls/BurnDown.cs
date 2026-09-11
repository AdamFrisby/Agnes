using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// One agent's quota window as a burn-down to its reset, with the projection drawn past the last sample.
/// </summary>
/// <remarks>
/// Under a subscription the marginal token is free, so the waste is quota left <em>unspent</em> when the
/// window rolls over — which a percentage-remaining number cannot show and a slope can. The dashed tail
/// from the last reading to the reset marker is the whole point of the picture: where it lands above the
/// floor is quota about to expire unused. An agent the router would not dispatch to right now draws
/// faint rather than sky, because its burn is not a live constraint.
/// </remarks>
public sealed class BurnDown : ThemedDrawing
{
    public static readonly StyledProperty<QuotaBurn?> BurnProperty =
        AvaloniaProperty.Register<BurnDown, QuotaBurn?>(nameof(Burn));

    static BurnDown() => AffectsRender<BurnDown>(BurnProperty);

    public QuotaBurn? Burn
    {
        get => GetValue(BurnProperty);
        set => SetValue(BurnProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(160, 40);

    public override void Render(DrawingContext context)
    {
        if (Burn is not { } burn || burn.Samples.Count == 0 || Bounds.Width <= 16 || Bounds.Height <= 8)
        {
            return;
        }

        var top = 3.0;
        var bottom = Bounds.Height - 3;
        // The reset marker owns the right edge; the series stops short of it.
        var right = Bounds.Width - 2;

        var start = burn.Samples[0].At;
        var end = burn.ResetAt is { } reset && reset > start ? reset : burn.Samples[^1].At;
        var span = (end - start).TotalSeconds;

        double X(DateTimeOffset at) => span <= 0 ? right : Math.Clamp((at - start).TotalSeconds / span * right, 0, right);
        double Y(double pct) => bottom - (Math.Clamp(pct, 0, 100) / 100 * (bottom - top));

        // The scale: 0% is the baseline, 100% the faint line at the top, half way a fainter one. Three
        // lines are what turn a wiggle into a reading.
        context.DrawLine(Pen(1, Roles.Line), new Point(0, bottom), new Point(right, bottom));
        using (context.PushOpacity(0.45))
        {
            context.DrawLine(Pen(1, Roles.Line), new Point(0, top), new Point(right, top));
        }
        using (context.PushOpacity(0.25))
        {
            context.DrawLine(Pen(1, Roles.Line), new Point(0, Y(50)), new Point(right, Y(50)));
        }
        var hue = burn.Eligible ? Roles.Sky : Roles.Faint;

        if (burn.Samples.Count >= 2)
        {
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                sink.BeginFigure(new Point(X(burn.Samples[0].At), Y(burn.Samples[0].Pct)), false);
                for (var i = 1; i < burn.Samples.Count; i++)
                {
                    sink.LineTo(new Point(X(burn.Samples[i].At), Y(burn.Samples[i].Pct)));
                }

                sink.EndFigure(false);
            }

            context.DrawGeometry(null, Pen(1.5, hue), geometry);
        }

        var lastPoint = new Point(X(burn.Samples[^1].At), Y(burn.Samples[^1].Pct));
        context.DrawEllipse(Brush(hue), null, lastPoint, 2, 2);
        // "Now" is where the solid line stops; a tick on the baseline says so where the dot is faint.
        context.DrawLine(Pen(1, Roles.Faint), new Point(lastPoint.X, bottom), new Point(lastPoint.X, bottom - 4));
        if (burn.ResetAt is not null)
        {
            using (context.PushOpacity(0.8))
            {
                context.DrawLine(Pen(1, Roles.Faint), new Point(right, top), new Point(right, bottom));
            }
        }

        // Only a line that is going down projects. A flat window with a dashed tail reads as broken.
        if (burn.IsBurning && burn.ProjectedUnspentPct is { } unspent)
        {
            var dashed = new Pen(Brush(hue), 1.2, new DashStyle([3, 3], 0));
            context.DrawLine(dashed, lastPoint, new Point(right, Y(unspent)));
        }
    }
}
