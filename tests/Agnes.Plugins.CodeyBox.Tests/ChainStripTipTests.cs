using Agnes.Plugins.CodeyBox.Views.Controls;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>The strip's tooltip is per pip: what THIS step is, its state, and what is known about it.</summary>
public class ChainStripTipTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static Step Step(int index, string title, StepState state, string series = "", string? error = null)
        => new(new WorkItemRow($"id-{index}", title, state == StepState.Done ? "Done" : "Working", "copilot", "p", 0,
            Now.AddMinutes(-5), error), index, state, series);

    [Fact]
    public void A_pips_tip_names_the_step_its_state_its_title_and_its_facts()
    {
        var steps = new[] { Step(0, "Build foowidget", StepState.Done), Step(1, "Ship foowidget", StepState.Running) };

        var lines = ChainStrip.TipLines(steps[0], steps);

        Assert.Equal("Step 1 of 2 · done", lines[0]);
        Assert.Equal("Build foowidget", lines[1]);
        Assert.StartsWith("Done · copilot · ", lines[2], StringComparison.Ordinal);
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void The_authors_own_numbering_wins_and_an_error_is_the_last_line()
    {
        var steps = new[] { Step(0, "Wire the broker", StepState.Failed, series: "3/7", error: "merge failed against main") };

        var lines = ChainStrip.TipLines(steps[0], steps);

        Assert.Equal("Step 3/7 · failed", lines[0]);
        Assert.Contains("merge failed against main", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_step_under_a_position_is_found_by_walking_the_slots()
    {
        var steps = Enumerable.Range(0, 3).Select(i => Step(i, $"s{i}", StepState.Ready)).ToArray();

        Assert.Same(steps[0], ChainStrip.StepAt(steps, 2));
        Assert.Same(steps[1], ChainStrip.StepAt(steps, 12));
        Assert.Null(ChainStrip.StepAt(steps, 500));
    }
}
