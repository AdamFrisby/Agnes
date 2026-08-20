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

    [Fact]
    public void New_input_cancels_an_older_deferred_seek()
    {
        var policy = new TranscriptScrollPolicy();
        policy.RequestIndexTarget(400, 790);
        Assert.True(policy.TryTakeLatestIndexTarget(out var oldSeek));

        var gesture = policy.BeginGesture();

        Assert.False(policy.IsCurrent(oldSeek.Generation));
        Assert.Equal(TranscriptScrollState.GestureScrolling, policy.State);
        Assert.True(policy.FinishGesture(gesture, isGenuinelyAtBottom: false));
        Assert.Equal(TranscriptScrollState.ReadingHistory, policy.State);
    }

    [Fact]
    public void Rapid_direction_changes_settle_from_only_the_latest_gesture()
    {
        var policy = new TranscriptScrollPolicy();
        var up = policy.BeginGesture();
        var down = policy.BeginGesture();

        Assert.False(policy.FinishGesture(up, isGenuinelyAtBottom: false));
        Assert.True(policy.FinishGesture(down, isGenuinelyAtBottom: true));
        Assert.Equal(TranscriptScrollState.FollowingTail, policy.State);
    }

    [Fact]
    public void Repeated_thumb_targets_coalesce_to_the_latest_row()
    {
        var policy = new TranscriptScrollPolicy();
        policy.BeginIndexDrag(790);
        var first = policy.RequestIndexTarget(100, 790);
        policy.RequestIndexTarget(650, 790);
        var latest = policy.RequestIndexTarget(240, 790);

        Assert.True(policy.TryTakeLatestIndexTarget(out var request));
        Assert.Equal(240, request.Row);
        Assert.Equal(latest, request.Generation);
        Assert.False(policy.IsCurrent(first));
        Assert.False(policy.TryTakeLatestIndexTarget(out _));
    }

    [Fact]
    public void Appends_follow_only_at_the_live_tail()
    {
        var policy = new TranscriptScrollPolicy();
        Assert.True(policy.ShouldFollowAppend);

        var gesture = policy.BeginGesture();
        policy.FinishGesture(gesture, isGenuinelyAtBottom: false);
        Assert.False(policy.ShouldFollowAppend);

        policy.FollowTail();
        Assert.True(policy.ShouldFollowAppend);
    }

    [Fact]
    public void Drag_range_is_frozen_and_its_end_tracks_the_streaming_tail()
    {
        var policy = new TranscriptScrollPolicy();
        policy.BeginIndexDrag(maximum: 790);
        policy.RequestIndexTarget(value: 790, currentMaximum: 840);

        Assert.Equal(790, policy.FrozenIndexMaximum);
        Assert.True(policy.ShouldFollowAppend);
        Assert.True(policy.TryTakeLatestIndexTarget(out var request));
        Assert.True(request.FollowTail);

        policy.EndIndexDrag();
        Assert.Equal(TranscriptScrollState.FollowingTail, policy.State);
        Assert.True(policy.ShouldFollowAppend);
    }
}
