using Avalonia;
using Avalonia.Media;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// The 3px rule down the left of a tile or a headline: tone on the edge, never as a fill.
/// </summary>
/// <remarks>
/// Five filled panels in a row compete with each other and with the content below them, so the whole
/// overview carries tone on an edge. Drawn rather than assembled from four <c>IsVisible</c> borders,
/// because a <see cref="TileTone"/> is one value and rendering it as four mutually-exclusive elements is
/// how that invariant gets broken later.
/// </remarks>
public sealed class ToneEdge : ThemedDrawing
{
    public static readonly StyledProperty<TileTone> ToneProperty =
        AvaloniaProperty.Register<ToneEdge, TileTone>(nameof(Tone));

    static ToneEdge() => AffectsRender<ToneEdge>(ToneProperty);

    public TileTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(3, 0);

    public override void Render(DrawingContext context)
    {
        // Neutral is the common case and it draws nothing: an ordinary number should not wear a hue.
        if (Tone == TileTone.Neutral)
        {
            return;
        }

        var brush = Brush(Tone switch
        {
            TileTone.Active => Roles.Sky,
            TileTone.Attention => Roles.Amber,
            TileTone.Bad => Roles.Pink,
            _ => Roles.Faint,
        });

        var width = Math.Min(Bounds.Width, 3);
        context.DrawRectangle(brush, null, new RoundedRect(new Rect(0, 0, width, Bounds.Height), 2));
    }
}
