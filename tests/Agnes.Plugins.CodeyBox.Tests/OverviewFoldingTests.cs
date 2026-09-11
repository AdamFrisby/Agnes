namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The attention band shows what needs a look and folds what does not. The operator's own test: items
/// waiting for an auditor are not moving and are not his to move, so they are one line, not twenty.
/// </summary>
public class OverviewFoldingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static ItemTrace Trace(string id, string state, Motion motion, string why, Convergence shape = Convergence.Converging,
        bool needsPerson = false, bool nearCeiling = false, TimeSpan? quiet = null)
        => new(
            new WorkItemRow(id, id, state, "copilot", "p", 0, Now - (quiet ?? TimeSpan.FromHours(2)), null),
            [], 25, motion, shape, why, nearCeiling, needsPerson, quiet ?? TimeSpan.FromHours(2), 0);

    [Fact]
    public void Items_stopped_at_a_phase_boundary_fold_into_one_line_per_boundary()
    {
        var (folded, attention) = OverviewModel.Fold(
        [
            Trace("a", "WorkComplete", Motion.Wedged, "waiting for an audit slot, quiet for 3h", quiet: TimeSpan.FromHours(3)),
            Trace("b", "WorkComplete", Motion.Wedged, "waiting for an audit slot, quiet for 9h", quiet: TimeSpan.FromHours(9)),
            Trace("c", "Merged", Motion.Wedged, "waiting to push, quiet for 1h"),
            Trace("d", "Working", Motion.Wedged, "quiet for 2h"),
        ]);

        var audit = Assert.Single(folded, g => g.Title == "2 items waiting for an audit slot");
        Assert.Equal(["b", "a"], audit.Items.Select(t => t.Item.Id));
        Assert.StartsWith("quiet up to 9h", audit.Detail, StringComparison.Ordinal);
        Assert.Contains(folded, g => g.Title == "1 item merged, waiting to push");
        // A silent agent mid-phase is a real wedge: it stays on the list.
        Assert.Equal(["d"], attention.Select(t => t.Item.Id));
    }

    [Fact]
    public void Dependency_and_quota_waits_fold_too_because_the_pipeline_releases_them()
    {
        var (folded, attention) = OverviewModel.Fold(
        [
            Trace("a", "Queued", Motion.Blocked, "waiting on 1 dependency"),
            Trace("b", "Queued", Motion.Blocked, "waiting on 2 dependencies"),
            Trace("c", "WaitingForQuota", Motion.Parked, "resumes 18:10"),
        ]);

        Assert.Empty(attention);
        Assert.Equal(["2 items waiting on a dependency", "1 item parked until quota or a retry"], folded.Select(g => g.Title));
        Assert.Contains("resumes 18:10", folded[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Anything_the_operator_can_act_on_stays_on_the_list()
    {
        var (folded, attention) = OverviewModel.Fold(
        [
            Trace("person", "NeedsOperatorInput", Motion.Blocked, "1 open question", needsPerson: true),
            Trace("failed", "MergeConflictResolutionFailed", Motion.Blocked, "failed: merge", needsPerson: true),
            Trace("stuck", "WorkComplete", Motion.Wedged, "waiting for an audit slot", Convergence.Stuck),
            Trace("sawtooth", "WorkComplete", Motion.Wedged, "waiting for an audit slot", Convergence.Oscillating),
            Trace("ceiling", "Queued", Motion.Blocked, "waiting on 1 dependency", nearCeiling: true),
        ]);

        Assert.Empty(folded);
        Assert.Equal(5, attention.Count);
    }

    [Fact]
    public void Folding_keeps_the_attention_order_of_what_remains()
    {
        var (_, attention) = OverviewModel.Fold(
        [
            Trace("first", "Working", Motion.Wedged, "quiet for 5h"),
            Trace("folded", "WorkComplete", Motion.Wedged, "waiting for an audit slot"),
            Trace("second", "NeedsOperatorInput", Motion.Blocked, "needs a decision", needsPerson: true),
        ]);

        Assert.Equal(["first", "second"], attention.Select(t => t.Item.Id));
    }
}
