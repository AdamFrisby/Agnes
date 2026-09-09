using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>
/// Every list on the session sidebar folds. Approvals was the exception, and the one that most needed it:
/// it is the only section with no cap and no "show all", so a long autonomous session accrued a hundred
/// "allow the sandboxed agent to…" rows that pushed the plan, the files and the tools off the panel with
/// no way to get them back.
/// </summary>
public class SidebarFoldingTests
{
    private static SessionViewModel Build(out SessionView view)
    {
        view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, 0), [], 0));
        return new SessionViewModel(new SimulatedHost(), view, ImmediateDispatcher.Instance, "OpenCode");
    }

    private static SessionEvent Seq(SessionEvent e, long n) => e with { Sequence = n };

    [Fact]
    public void Approvals_starts_open_and_folds_away()
    {
        var vm = Build(out var view);
        Assert.False(vm.HasApprovals);
        // Open by default: an audit trail nobody opened is an audit trail nobody read.
        Assert.True(vm.ApprovalsExpanded);

        view.Apply(Seq(new PermissionRequestedEvent("r1", "tc1", "Access paths outside trusted directories", []), 1));
        view.Apply(Seq(new PermissionResolvedEvent("r1", "allow", PermissionOutcome.Allowed), 2));
        Assert.True(vm.HasApprovals);

        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionViewModel.ApprovalsExpanded)) { raised++; } };

        vm.ToggleApprovalsCommand.Execute(null);
        Assert.False(vm.ApprovalsExpanded);
        Assert.Equal(1, raised);

        // Folding hides the rows, never discards them — the section is still there to open again.
        Assert.True(vm.HasApprovals);
        vm.ToggleApprovalsCommand.Execute(null);
        Assert.True(vm.ApprovalsExpanded);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void The_other_audit_sections_fold_the_same_way()
    {
        var vm = Build(out _);

        Assert.True(vm.McpCallsExpanded);
        vm.ToggleMcpCallsCommand.Execute(null);
        Assert.False(vm.McpCallsExpanded);

        Assert.True(vm.ReviewCommentsExpanded);
        vm.ToggleReviewCommentsCommand.Execute(null);
        Assert.False(vm.ReviewCommentsExpanded);
    }
}
