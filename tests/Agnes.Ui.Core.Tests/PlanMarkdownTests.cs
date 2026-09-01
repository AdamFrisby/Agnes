using Agnes.Abstractions;
using Agnes.Ui.Core.Transcript;

namespace Agnes.Ui.Core.Tests;

public sealed class PlanMarkdownTests
{
    [Fact]
    public void Export_preserves_every_entry_state_priority_and_multiline_content()
    {
        var markdown = PlanMarkdown.Format(
        [
            new PlanEntry("Investigate", "completed"),
            new PlanEntry("Implement\nthe queue", "in_progress", "high"),
            new PlanEntry("Verify", "pending"),
            new PlanEntry("Discard old approach", "cancelled"),
        ]);

        Assert.Equal(
            """
            # Plan

            - [x] Investigate
            - [ ] Implement _(In progress · Priority: high)_
              the queue
            - [ ] Verify
            - [ ] Discard old approach _(Cancelled)_

            """.ReplaceLineEndings("\n"),
            markdown);
    }

    [Fact]
    public void Empty_plan_still_exports_a_valid_markdown_document()
        => Assert.Equal("# Plan\n", PlanMarkdown.Format([]));
}
