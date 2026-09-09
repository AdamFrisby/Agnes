using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The gate summary and iteration progress, against shapes taken from the live orchestrator: gate names
/// that all end in the same segment, seven gates that never fail, and items that run past their budget.
/// </summary>
public sealed class AuditSummaryTests
{
    private static AgentRun Run(string phase, int? iteration, string outcome, int minute)
        => new(
            Id: $"{phase}-{iteration}-{minute}",
            AgentKind: "claude",
            ModelId: "claude-opus-4-8",
            Phase: phase,
            StartedAt: new DateTimeOffset(2026, 7, 16, 0, minute, 0, TimeSpan.Zero),
            EndedAt: new DateTimeOffset(2026, 7, 16, 0, minute + 1, 0, TimeSpan.Zero),
            Iteration: iteration,
            Outcome: outcome);

    [Fact]
    public void Gates_are_ranked_by_what_blocks_most()
    {
        var gates = AuditSummary.Gates([
            Run("audit:security:llm-review", 1, "success", 1),
            Run("audit:completeness:llm-review", 1, "failure:audit", 2),
            Run("audit:completeness:llm-review", 2, "failure:audit", 3),
            Run("audit:quality:llm-review", 1, "failure:audit", 4),
            Run("work", null, "success", 5),
        ]);

        Assert.Equal(["completeness:llm-review", "quality:llm-review", "security:llm-review"],
                     gates.Select(g => g.Gate));
        Assert.Equal(2, gates[0].Blocks);
        Assert.Equal(2, gates[0].Runs);
    }

    [Fact]
    public void Non_audit_phases_are_excluded()
    {
        // work, rework, merge and conflict_rework are phases, not gates; counting them as gates would
        // make every item look like it had failed an audit it never ran.
        var gates = AuditSummary.Gates([Run("work", null, "failure:agent", 1), Run("merge", null, "success", 2)]);

        Assert.Empty(gates);
    }

    [Fact]
    public void A_gate_whose_last_run_completed_is_reported_as_such()
    {
        // The difference between "nearly there" and "stuck in a loop", which a total alone cannot say.
        var gates = AuditSummary.Gates([
            Run("audit:tests:meaningfulness-review", 1, "failure:audit", 1),
            Run("audit:tests:meaningfulness-review", 2, "success", 2),
        ]);

        var gate = Assert.Single(gates);
        Assert.Equal(1, gate.Blocks);
        Assert.False(gate.LastFailed);
        Assert.Equal("last run ok", gate.LastLabel);
    }

    [Fact]
    public void A_gate_whose_runs_all_completed_says_so_rather_than_showing_a_zero_bar()
    {
        // Seven of the sixteen gates here have never failed in 24,038 runs.
        var gate = Assert.Single(AuditSummary.Gates([Run("audit:security:gitleaks", 1, "success", 1)]));

        Assert.False(gate.EverBlocked);
        Assert.Equal(0, gate.BarWidth);
        Assert.Contains("all completed", gate.Counts);
    }

    [Fact]
    public void The_summary_never_claims_an_audit_verdict()
    {
        // The orchestrator's outcome field has no value meaning "the auditor rejected the work" — it
        // reports whether the RUN completed. These are the real failure values it uses. Nothing this
        // type produces may describe them as an audit outcome.
        var gates = AuditSummary.Gates([
            Run("audit:security:llm-review", 1, "failure:quota", 1),
            Run("audit:security:llm-review", 2, "failure:timeout", 2),
        ]);

        var gate = Assert.Single(gates);
        Assert.Equal("2 of 2 runs failed to complete", gate.Counts);
        Assert.DoesNotContain("block", gate.Counts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audit", gate.LastLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Progress_reports_depth_against_the_projects_ceiling()
    {
        var progress = AuditSummary.Progress([Run("audit:quality:llm-review", 12, "success", 1)], ceiling: 25);

        Assert.Equal(12, progress.Current);
        Assert.False(progress.OverBudget);
        Assert.Equal("iteration 12 of 25", progress.Label);
    }

    [Fact]
    public void Running_past_the_budget_is_reported_not_clamped_away()
    {
        // Observed live: an item reached iteration 52 against a configured ceiling of 25. The ceiling is
        // raised in place rather than enforced, so this is the normal way a grinding item looks.
        var progress = AuditSummary.Progress([Run("audit:quality:llm-review", 52, "failure:audit", 1)], ceiling: 25);

        Assert.True(progress.OverBudget);
        Assert.Equal(1.0, progress.Fraction);          // the bar saturates
        Assert.Contains("past the 25-iteration budget", progress.Label);
    }

    [Fact]
    public void Progress_is_unknown_rather_than_zero_when_nothing_has_iterated()
    {
        Assert.False(AuditSummary.Progress([Run("work", null, "success", 1)], 25).IsKnown);
    }

    [Fact]
    public void Notable_keeps_failures_and_the_newest_iteration_and_drops_settled_successes()
    {
        var runs = new[]
        {
            Run("audit:security:gitleaks", 1, "success", 1),        // settled success, older iteration
            Run("audit:completeness:llm-review", 1, "failure:audit", 2),
            Run("audit:security:gitleaks", 3, "success", 3),        // newest iteration
            Run("work", null, "success", 4),                        // phase run, always kept
        };

        var notable = AuditSummary.Notable(runs);

        Assert.DoesNotContain(runs[0], notable);
        Assert.Contains(runs[1], notable);
        Assert.Contains(runs[2], notable);
        Assert.Contains(runs[3], notable);
    }

    [Fact]
    public void Notable_on_an_empty_history_is_empty_not_an_error()
        => Assert.Empty(AuditSummary.Notable([]));
}
