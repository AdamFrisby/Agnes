using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

public class MultiColumnTests
{
    private static SessionViewModel Build(out SessionView view)
    {
        view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, 0), [], 0));
        return new SessionViewModel(new SimulatedHost(), view, ImmediateDispatcher.Instance, "OpenCode");
    }

    private static SessionEvent Seq(SessionEvent e, long n) => e with { Sequence = n };

    private static SessionEvent FileTool(string id, string title, string diff)
        => new ToolCallEvent(id, title, ToolKind.Edit, ToolCallStatus.Completed, [new TextContent(diff)]);

    [Fact]
    public void Plan_and_files_populate_the_left_panel()
    {
        var vm = Build(out var view);
        Assert.False(vm.HasSidebarContent);
        Assert.False(vm.ShowLeftPanel);

        view.Apply(Seq(new PlanEvent([new PlanEntry("Investigate", "in_progress")]), 1));
        view.Apply(Seq(FileTool("tc1", "output.txt", "--- a\n+++ b\n+x"), 2));

        Assert.NotNull(vm.Plan);
        var file = Assert.Single(vm.ModifiedFiles);
        Assert.Equal("output.txt", file.Name);
        Assert.True(vm.HasSidebarContent);
        Assert.True(vm.ShowLeftPanel);
    }

    [Fact]
    public void Tapping_a_tool_opens_the_full_preview()
    {
        var vm = Build(out var view);
        view.Apply(Seq(FileTool("tc1", "output.txt", "--- a/output.txt\n+++ b/output.txt\n+alpha"), 1));
        Assert.False(vm.ShowRightPanel);

        var tool = vm.Items.OfType<ToolCallItem>().Single();
        vm.ShowToolPreviewCommand.Execute(tool);

        Assert.True(vm.ShowRightPanel);
        Assert.Contains("+alpha", vm.SelectedPreview!.Body);

        vm.ClosePreviewCommand.Execute(null);
        Assert.False(vm.ShowRightPanel);
    }

    [Fact]
    public void Files_panel_entry_opens_its_diff_in_the_preview()
    {
        var vm = Build(out var view);
        view.Apply(Seq(FileTool("tc1", "output.txt", "--- a\n+++ b\n+beta"), 1));

        vm.ShowFilePreviewCommand.Execute(vm.ModifiedFiles[0]);

        Assert.True(vm.ShowRightPanel);
        Assert.Contains("+beta", vm.SelectedPreview!.Body);
    }

    [Fact]
    public void Toggle_hides_the_left_panel_even_with_content()
    {
        var vm = Build(out var view);
        view.Apply(Seq(FileTool("tc1", "f", string.Empty), 1));
        Assert.True(vm.ShowLeftPanel);

        vm.ToggleLeftCommand.Execute(null);

        Assert.True(vm.HasSidebarContent);
        Assert.False(vm.ShowLeftPanel);
    }
}
