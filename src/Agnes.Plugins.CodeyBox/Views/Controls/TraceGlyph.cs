using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// One item's audit loop, drawn: a bar per iteration, oldest on the left, tall where the audit blocked.
/// </summary>
/// <remarks>
/// The shape is the point. A descending staircase is convergence; a sawtooth is oscillation; a flat
/// high line is stuck; and a long trace that stopped growing is a wedge. None of that survives being
/// reduced to "iteration 14 of 25", which is the number the old dashboard showed, so the loop is drawn
/// instead of counted. Three marks carry the rest of it:
/// <list type="bullet">
/// <item>a rise on the <em>same gate</em> as the previous iteration is pink — that, not repetition, is
/// the waste;</item>
/// <item>the trailing bar is outlined while auditors are still reporting, so an in-progress iteration is
/// never read as a finished one;</item>
/// <item>a freshness dot on the right fades with time since the item last moved, which is what turns a
/// healthy-looking trace on a silent item into a visibly stale one.</item>
/// </list>
/// A zero-finding iteration still draws — as a baseline pip — because "it passed" is information and an
/// empty gap reads as missing data.
/// </remarks>
public sealed class TraceGlyph : ThemedDrawing
{
    public static readonly StyledProperty<ItemTrace?> TraceProperty =
        AvaloniaProperty.Register<TraceGlyph, ItemTrace?>(nameof(Trace));

    static TraceGlyph() => AffectsRender<TraceGlyph>(TraceProperty);

    public ItemTrace? Trace
    {
        get => GetValue(TraceProperty);
        set => SetValue(TraceProperty, value);
    }

    /// <summary>An hour of silence is where the dot has faded as far as it goes.</summary>
    private static readonly TimeSpan FullyStale = TimeSpan.FromMinutes(60);

    protected override Size MeasureOverride(Size availableSize) => new(120, 22);

    public override void Render(DrawingContext context)
    {
        if (Trace is not { } trace || Bounds.Width <= 12 || Bounds.Height <= 4)
        {
            return;
        }

        var height = Bounds.Height;
        var baseline = height - 1;
        // The rightmost sliver belongs to the freshness dot, so bars never collide with it.
        var barsWidth = Math.Max(0, Bounds.Width - 10);

        DrawBars(context, trace, barsWidth, baseline, height);
        DrawFreshness(context, trace, baseline, height);
    }

    private void DrawBars(DrawingContext context, ItemTrace trace, double barsWidth, double baseline, double height)
    {
        var points = trace.Points;
        if (points.Count == 0 || barsWidth <= 0)
        {
            return;
        }

        // x spans the ceiling when there is one, so two items with the same trace but different budgets
        // do not look equally close to the end of theirs.
        var slots = Math.Max(points.Count, trace.Ceiling > 0 ? trace.Ceiling : points.Count);
        var slot = barsWidth / slots;
        var barWidth = Math.Max(1.0, Math.Min(slot - 1, 7));
        var worst = points.Max(p => p.BlockingFindings);
        var full = height - 3;

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var x = i * slot;
            var rise = i > 0 && point.BlockingFindings > points[i - 1].BlockingFindings;
            var oscillating = rise && point.SameGateAsPrevious;
            var brush = Brush(oscillating ? Roles.Pink : Roles.Dim);

            if (point.BlockingFindings == 0)
            {
                // Passed. A pip on the baseline rather than nothing at all.
                context.DrawRectangle(Brush(Roles.Mint), null, new Rect(x, baseline - 2, barWidth, 2));
                continue;
            }

            var barHeight = worst <= 0 ? 2 : Math.Max(2, full * point.BlockingFindings / worst);
            var rect = new Rect(x, baseline - barHeight, barWidth, barHeight);

            if (point.Complete)
            {
                context.DrawRectangle(brush, null, rect);
            }
            else
            {
                // Still reporting: outlined, so a partial count is not read as a verdict.
                context.DrawRectangle(null, new Pen(brush, 1), rect.Deflate(0.5));
            }
        }

        // The cap is already legible as the empty space the bars have not reached, so the tick is drawn
        // only where it says something that space cannot: an item that has run PAST its budget, with
        // bars continuing to the right of the line.
        if (trace.Ceiling > 0 && trace.Ceiling < points.Count)
        {
            var x = trace.Ceiling * slot - 0.5;
            context.DrawLine(Pen(1, Roles.Amber), new Point(x, 0), new Point(x, baseline));
        }
    }

    private void DrawFreshness(DrawingContext context, ItemTrace trace, double baseline, double height)
    {
        var age = trace.SinceMoved < TimeSpan.Zero ? TimeSpan.Zero : trace.SinceMoved;
        var decayed = Math.Min(1.0, age.TotalMinutes / FullyStale.TotalMinutes);
        var opacity = 1.0 - (0.75 * decayed);
        var centre = new Point(Bounds.Width - 3.5, Math.Min(baseline, height / 2));

        using (context.PushOpacity(opacity))
        {
            context.DrawEllipse(Brush(MotionDot.MotionRole(trace.Motion)), null, centre, 2.5, 2.5);
        }
    }
}
