using Avalonia;
using Avalonia.Controls;

namespace Agnes.Plugins.CodeyBox.Views.Controls;

/// <summary>
/// Lays equal-width cards out in rows, as many per row as fit, and reports a height that is exactly the
/// rows it will arrange.
/// </summary>
/// <remarks>
/// A <c>WrapPanel</c> did this job until the live overview showed nine quota cards in three rows with the
/// page's scroll extent stopping part-way down the third: measured at one width and arranged at another,
/// its desired height described two rows and the ScrollViewer believed it, so the last card could never be
/// scrolled to. This panel measures and arranges from the same arithmetic — the card width is the widest
/// child's, the column count is what the given width holds — so the height it promises is the height it
/// uses. Children keep their own <c>Margin</c>; the panel adds nothing of its own.
/// </remarks>
public sealed class CardFlow : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children.Where(c => c.IsVisible).ToList();
        if (children.Count == 0)
        {
            return new Size(0, 0);
        }

        foreach (var child in children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        var cardWidth = children.Max(c => c.DesiredSize.Width);
        var rowHeight = children.Max(c => c.DesiredSize.Height);
        var columns = Columns(availableSize.Width, cardWidth, children.Count);
        var rows = (children.Count + columns - 1) / columns;
        var width = double.IsInfinity(availableSize.Width) ? columns * cardWidth : Math.Min(availableSize.Width, columns * cardWidth);
        return new Size(width, rows * rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children.Where(c => c.IsVisible).ToList();
        if (children.Count == 0)
        {
            return finalSize;
        }

        var cardWidth = children.Max(c => c.DesiredSize.Width);
        var rowHeight = children.Max(c => c.DesiredSize.Height);
        var columns = Columns(finalSize.Width, cardWidth, children.Count);
        for (var i = 0; i < children.Count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            children[i].Arrange(new Rect(column * cardWidth, row * rowHeight, cardWidth, rowHeight));
        }

        return finalSize;
    }

    private static int Columns(double width, double cardWidth, int count)
        => cardWidth <= 0 || double.IsInfinity(width)
            ? Math.Max(1, count)
            : Math.Clamp((int)Math.Floor((width + 0.5) / cardWidth), 1, Math.Max(1, count));
}
