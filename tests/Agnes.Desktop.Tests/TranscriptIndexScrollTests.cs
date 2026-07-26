using Agnes.App.Desktop.Views;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The message-indexed scrollbar's arithmetic. The point of the mode is that the range moves only when
/// the transcript does — never through re-measurement — so these cover the mapping that guarantees it.
/// </summary>
public sealed class TranscriptIndexScrollTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(12_000, true)]
    public void ActivatesOnlyPastTheThreshold(int itemCount, bool expected)
        => Assert.Equal(expected, TranscriptIndexScroll.IsActive(itemCount));

    [Fact]
    public void MaximumIsTheFirstRowOfTheLastPage()
    {
        var range = TranscriptIndexScroll.Range(itemCount: 1000, visibleRows: 12, firstVisibleIndex: 0);

        Assert.Equal(988, range.Maximum); // dragging to the end shows rows 988..999
        Assert.Equal(12, range.ViewportSize);
        Assert.Equal(0, range.Value);
    }

    [Fact]
    public void ValueIsTheRowAtTheTopOfTheViewport()
    {
        var range = TranscriptIndexScroll.Range(itemCount: 1000, visibleRows: 10, firstVisibleIndex: 640);

        Assert.Equal(640, range.Value);
        Assert.True(TranscriptIndexScroll.IsAtEnd(range.Maximum, range.Maximum));
        Assert.False(TranscriptIndexScroll.IsAtEnd(range.Value, range.Maximum));
    }

    [Fact]
    public void ValueIsClampedIntoTheReachableRange()
    {
        // The last page's rows are all "first visible" at the bottom; none of them may exceed the max,
        // or the thumb would sit past the end of its own track.
        var range = TranscriptIndexScroll.Range(itemCount: 600, visibleRows: 8, firstVisibleIndex: 599);

        Assert.Equal(592, range.Maximum);
        Assert.Equal(592, range.Value);
        Assert.True(TranscriptIndexScroll.IsAtEnd(range.Value, range.Maximum));
    }

    [Fact]
    public void OneTallRowFillingTheViewportStillLeavesADraggableRange()
    {
        // A single huge diff can be the only visible row. The bar must still span the transcript.
        var range = TranscriptIndexScroll.Range(itemCount: 900, visibleRows: 1, firstVisibleIndex: 300);

        Assert.Equal(899, range.Maximum);
        Assert.Equal(1, range.ViewportSize);
        Assert.Equal(300, range.Value);
    }

    [Fact]
    public void DegenerateMeasurementsDoNotProduceAnInvalidRange()
    {
        // Before the first layout pass nothing is realized: viewport 0 rows, no first index.
        var empty = TranscriptIndexScroll.Range(itemCount: 0, visibleRows: 0, firstVisibleIndex: 0);
        Assert.Equal(0, empty.Maximum);
        Assert.Equal(1, empty.ViewportSize);
        Assert.Equal(0, empty.Value);

        // A viewport measured as taller than the transcript can never yield a negative range.
        var overshoot = TranscriptIndexScroll.Range(itemCount: 5, visibleRows: 40, firstVisibleIndex: -3);
        Assert.Equal(0, overshoot.Maximum);
        Assert.Equal(5, overshoot.ViewportSize);
        Assert.Equal(0, overshoot.Value);
    }

    [Fact]
    public void GrowingTheTranscriptMovesTheEndButNotThePositionYouAreReading()
    {
        var before = TranscriptIndexScroll.Range(itemCount: 800, visibleRows: 10, firstVisibleIndex: 200);
        var after = TranscriptIndexScroll.Range(itemCount: 850, visibleRows: 10, firstVisibleIndex: 200);

        // This is the property the whole mode exists for: appending 50 rows leaves the thumb's value
        // exactly where it was. A pixel bar would also have re-estimated everything below it.
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.Maximum + 50, after.Maximum);
    }
}
