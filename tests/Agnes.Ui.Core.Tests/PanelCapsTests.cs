using Agnes.Abstractions;
using Agnes.Protocol;
using Agnes.Client;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The side-panel lists are bounded. A session that has touched 549 files or carries a 171-entry plan is
/// real; a panel that builds a row for each is what made its tab take a second to attach. The newest
/// files and the head of the plan are what a person looks at, and a note says what is held back.
/// </summary>
public class PanelCapsTests
{
    private sealed class Host : StubAgnesHost;

    private static (SessionViewModel vm, SessionView view) Open()
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "claude-code", string.Empty, 0), [], 0));
        return (new SessionViewModel(new Host(), view, ImmediateDispatcher.Instance, "Claude Code"), view);
    }

    [Fact]
    public void The_files_panel_lists_the_newest_fifty_and_a_page_after_show_all()
    {
        var (vm, view) = Open();
        for (var i = 0; i < 260; i++)
        {
            view.Apply(new ToolCallEvent($"tc{i}", $"Edit src/file{i:000}.cs", ToolKind.Edit, ToolCallStatus.Completed,
                [new DiffContent($"src/file{i:000}.cs", "a", "b")]) { Sequence = i + 1 });
        }

        Assert.Equal(260, vm.ModifiedFiles.Count);
        var shown = vm.VisibleModifiedFiles.ToList();
        Assert.Equal(SessionViewModel.FileDisplayLimit, shown.Count);
        Assert.EndsWith("src/file259.cs", shown[^1].Name);
        Assert.True(vm.HasMoreFiles);
        Assert.Equal("Show all 260", vm.MoreFilesLabel);

        vm.ShowAllFilesCommand.Execute(null);
        Assert.Equal(SessionViewModel.FilePageLimit, vm.VisibleModifiedFiles.Count());
        Assert.Equal("Latest 200 of 260", vm.FilesNote);
        Assert.False(vm.HasMoreFiles);
    }

    [Fact]
    public void Show_all_tools_is_a_page_not_the_whole_history()
    {
        var (vm, view) = Open();
        for (var i = 0; i < 1_000; i++)
        {
            view.Apply(new ToolCallEvent($"tc{i}", $"Run step {i}", ToolKind.Execute, ToolCallStatus.Completed, []) { Sequence = i + 1 });
        }

        Assert.Equal(SessionViewModel.ToolDisplayLimit, vm.VisibleToolActivity.Count());
        Assert.True(vm.HasMoreTools);

        vm.ShowAllTools = true;
        Assert.Equal(SessionViewModel.ToolPageLimit, vm.VisibleToolActivity.Count());
        Assert.Equal("Latest 200 of 1,000", vm.ToolsNote);
        Assert.EndsWith("step 999", vm.VisibleToolActivity.Last().Name);
    }

    [Fact]
    public void A_plan_shows_its_head_and_says_how_much_more_there_is()
    {
        var entries = Enumerable.Range(1, 171).Select(i => new PlanEntry($"step {i}", "pending", "medium")).ToList();
        var plan = new PlanItemView { Entries = entries };

        Assert.Equal(PlanItemView.PageLimit, plan.VisibleEntries.Count);
        Assert.Equal("step 1", plan.VisibleEntries[0].Content);
        Assert.Equal("First 30 of 171", plan.OverflowNote);

        plan.Entries = entries.Take(10).ToList();
        Assert.Equal(10, plan.VisibleEntries.Count);
        Assert.Equal(string.Empty, plan.OverflowNote);
    }
}
