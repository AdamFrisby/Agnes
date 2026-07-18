using Agnes.Abstractions;
using Agnes.Ui.Core.Diff;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public class DiffTests
{
    [Fact]
    public void Unified_diff_from_texts_is_parseable_and_shows_changes()
    {
        var text = UnifiedDiff.Format("f.txt", "a\nb\nc\n", "a\nB\nc\nd\n");

        Assert.True(DiffParser.LooksLikeDiff(text));
        var lines = DiffParser.Parse(text);
        Assert.Contains(lines, l => l.IsRemoved && l.Text == "b");
        Assert.Contains(lines, l => l.IsAdded && l.Text == "B");
        Assert.Contains(lines, l => l.IsAdded && l.Text == "d");
    }

    [Fact]
    public void Structured_diff_content_renders_as_a_diff_in_the_transcript()
    {
        var t = new TranscriptBuilder();
        t.Apply(new ToolCallEvent("tc1", "f.txt", ToolKind.Edit, ToolCallStatus.Completed,
            [new DiffContent("f.txt", "a\nb\n", "a\nB\n")]));

        var tool = t.Items.OfType<ToolCallItem>().Single();
        Assert.True(DiffParser.LooksLikeDiff(tool.Detail));
    }

    [Fact]
    public void Split_rows_pair_old_removals_and_new_additions()
    {
        var lines = DiffParser.Parse(UnifiedDiff.Format("f", "a\nb\n", "a\nB\n"));
        var rows = DiffParser.ToSplit(lines);

        Assert.Contains(rows, r => r.LeftRemoved && r.LeftText == "b");
        Assert.Contains(rows, r => r.RightAdded && r.RightText == "B");
    }

    [Fact]
    public void Preview_toggles_between_unified_and_split()
    {
        var p = new PreviewViewModel("f", UnifiedDiff.Format("f", "a\nb\n", "a\nB\n"));
        Assert.True(p.IsDiff);
        Assert.True(p.ShowUnified);
        Assert.False(p.ShowSplit);

        p.ToggleSplitCommand.Execute(null);
        Assert.True(p.ShowSplit);
        Assert.False(p.ShowUnified);
        Assert.NotNull(p.SplitRows);
    }
}
