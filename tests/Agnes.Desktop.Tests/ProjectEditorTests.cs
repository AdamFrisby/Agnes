using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The Projects page's editor header says whether the edit is saved, so the flag behind it has to move
/// with every field and list the editor owns — it is computed, and a computed property only updates when
/// something announces it. The list pane marks the project that is open, and the one default the editor
/// did not expose (a graphical sandbox) round-trips like the others.
/// </summary>
public class ProjectEditorTests
{
    private static MainWindowViewModel NewVm()
    {
        var id = Guid.NewGuid().ToString("n");
        return new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-tabs-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-arch-{id}.json")));
    }

    private static ProjectDto Project(string id, string name, string repo, bool graphical = false) => new(
        id, name, repo,
        new SandboxImageDto("images:ubuntu/24.04/cloud", "agnes-baseline", true, ["git"], [], [], []),
        [], null, new ProjectDefaultsDto(Graphical: graphical));

    [Fact]
    public void Unsaved_changes_follows_every_field_and_list_the_editor_owns()
    {
        var vm = NewVm();
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsProjectDirty)) raised++; };

        vm.Projects.Add(Project("p1", "Agnes", "github.com/AdamFrisby/Agnes"));
        vm.SelectProjectCommand.Execute(vm.Projects[0]);
        Assert.False(vm.IsProjectDirty);

        vm.ProjDiskGiB = "40";
        Assert.True(vm.IsProjectDirty);
        vm.ProjDiskGiB = string.Empty;
        Assert.False(vm.IsProjectDirty);

        vm.ProjGraphical = true;
        Assert.True(vm.IsProjectDirty);
        vm.ProjGraphical = false;

        vm.ProjectUsb.Add(new UsbDeviceDto("0e8d", "201c"));
        Assert.True(vm.IsProjectDirty);
        vm.ProjectUsb.Clear();
        Assert.False(vm.IsProjectDirty);

        Assert.True(raised >= 6, $"the flag announced itself {raised} times");
    }

    [Fact]
    public void The_list_marks_the_open_project_and_names_the_fallback()
    {
        var vm = NewVm();
        vm.Projects.Add(Project("p0", "Default", ""));
        vm.Projects.Add(Project("p1", "Agnes", "github.com/AdamFrisby/Agnes"));

        vm.SelectProjectCommand.Execute(vm.Projects[1]);
        Assert.Equal([false, true], vm.ProjectRows.Select(r => r.IsSelected));
        Assert.True(vm.ProjectRows[0].IsDefault);
        Assert.Equal("github.com/AdamFrisby/Agnes", vm.SelectedProjectSubtitle);

        vm.SelectProjectCommand.Execute(vm.Projects[0]);
        Assert.Equal([true, false], vm.ProjectRows.Select(r => r.IsSelected));
        Assert.Contains("falls back", vm.SelectedProjectSubtitle);
    }

    [Fact]
    public void A_graphical_default_loads_into_the_editor()
    {
        var vm = NewVm();
        vm.Projects.Add(Project("p1", "UI work", "github.com/x/ui", graphical: true));
        vm.SelectProjectCommand.Execute(vm.Projects[0]);

        Assert.True(vm.ProjGraphical);
        Assert.False(vm.IsProjectDirty);
    }
}
