using System;

namespace Agnes.App.Desktop.Views;

/// <summary>Where a message-indexed scrollbar should sit for a given transcript, in *rows*.</summary>
/// <param name="Maximum">Highest reachable first-visible row — the last page's first row.</param>
/// <param name="ViewportSize">How many rows a page holds; drives the thumb's size.</param>
/// <param name="Value">The row currently at the top of the viewport.</param>
public readonly record struct IndexScrollRange(double Maximum, double ViewportSize, double Value);

/// <summary>
/// The transcript scrollbar's second gear. A pixel scrollbar over a virtualizing list is only as
/// stable as the list's <i>extent</i>, and that extent is an estimate: Avalonia infers the height of
/// the rows it hasn't realized from the ones it has, so it re-estimates every time a differently-sized
/// row scrolls into view (and again when backscroll rehydrates after a reconnect). Under a few hundred
/// rows the correction is invisible; over a large session it moves the thumb out from under the cursor
/// mid-drag, which is what makes dragging feel unreliable.
///
/// Past <see cref="Threshold"/> rows the desktop transcript therefore hides the pixel scrollbar and
/// drives one whose unit is a <i>message index</i>. That range is the item count, which changes only
/// when a message is appended — never through re-measurement — so a drag lands where it was aimed.
/// The wheel and keyboard keep scrolling in pixels; only the bar changes units.
/// </summary>
public static class TranscriptIndexScroll
{
    /// <summary>Row count at which the scrollbar switches to message indices.</summary>
    public const int Threshold = 500;

    /// <summary>Whether a transcript of this size is scrolled by index rather than by pixel.</summary>
    public static bool IsActive(int itemCount) => itemCount >= Threshold;

    /// <summary>
    /// Project a transcript onto the bar. <paramref name="visibleRows"/> is how many rows the viewport
    /// currently holds — rows vary wildly in height, so the thumb's size is measured, not assumed.
    /// </summary>
    public static IndexScrollRange Range(int itemCount, int visibleRows, int firstVisibleIndex)
    {
        var count = Math.Max(0, itemCount);
        var viewport = Math.Clamp(visibleRows, 1, Math.Max(1, count));
        var maximum = Math.Max(0, count - viewport);
        var value = Math.Clamp(firstVisibleIndex, 0, maximum);
        return new IndexScrollRange(maximum, viewport, value);
    }

    /// <summary>True when a bar at this value is showing the last page, i.e. the live tail.</summary>
    public static bool IsAtEnd(double value, double maximum) => value >= maximum - 0.5;
}
