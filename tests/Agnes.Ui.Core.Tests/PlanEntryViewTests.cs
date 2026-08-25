using Agnes.Abstractions;
using Agnes.Ui.Core.Transcript;
using FluentIcons.Common;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// A plan entry's status arrives as the wire value the agent sent — <c>in_progress</c> — and the sidebar
/// used to print it, spending a column of a narrow panel on protocol vocabulary. It resolves to an icon
/// and a hue here instead, once, so no view has to know the strings.
/// </summary>
public class PlanEntryViewTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("COMPLETED")]
    [InlineData("done")]
    public void A_finished_entry_is_a_filled_tick(string status)
    {
        var entry = PlanEntryView.Of(new PlanEntry("Add a test", status));
        Assert.True(entry.IsDone);
        Assert.Equal(Symbol.CheckmarkCircle, entry.Symbol);
        Assert.Equal(IconVariant.Filled, entry.Variant);
        Assert.Equal("Completed", entry.StatusLabel);
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("in-progress")]
    [InlineData("running")]
    public void Work_in_flight_reads_as_in_motion(string status)
    {
        var entry = PlanEntryView.Of(new PlanEntry("Run the suite", status));
        Assert.True(entry.IsRunning);
        Assert.False(entry.IsDone);
        Assert.Equal(Symbol.CircleHalfFill, entry.Symbol);
        Assert.Equal("In progress", entry.StatusLabel);
    }

    [Fact]
    public void Anything_unrecognized_reads_as_not_started_rather_than_throwing()
    {
        // The status set belongs to the agent, not to us: a value we've never seen must degrade to the
        // one reading that is safe to be wrong about, not crash a panel.
        var odd = PlanEntryView.Of(new PlanEntry("Something new", "deferred"));
        Assert.True(odd.IsPending);
        Assert.Equal(Symbol.CircleSmall, odd.Symbol);
        Assert.Equal(IconVariant.Regular, odd.Variant);
        Assert.Equal("Pending", odd.StatusLabel);
    }

    [Fact]
    public void The_raw_status_is_still_there_for_anyone_who_needs_it()
    {
        var entry = PlanEntryView.Of(new PlanEntry("Push", "in_progress"));
        Assert.Equal("in_progress", entry.Status);
        Assert.Equal("Push", entry.Content);
    }

    [Fact]
    public void A_plan_hands_the_panel_resolved_entries_not_raw_ones()
    {
        var plan = new PlanItemView
        {
            Entries = [new PlanEntry("Read the code", "completed"), new PlanEntry("Write the fix", "in_progress")],
        };

        Assert.Collection(
            plan.EntryViews,
            e => Assert.True(e.IsDone),
            e => Assert.True(e.IsRunning));

        // The folded view resolves the same way — the sidebar and the transcript card agree.
        Assert.Equal(["Read the code", "Write the fix"], plan.VisibleEntries.Select(e => e.Content));
    }
}
