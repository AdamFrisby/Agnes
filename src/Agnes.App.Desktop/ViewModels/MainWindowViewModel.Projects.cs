using System.Collections.Specialized;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// The Projects page's own state: the one default the editor did not expose (a graphical sandbox by
/// default), and the bookkeeping that keeps "Unsaved changes" honest — <see cref="IsProjectDirty"/> is
/// computed, so every field the editor owns announces it when it moves.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Open this project's sessions in a graphical sandbox (a VM with a screen the agent can drive)
    /// by default. Honoured only where the operator allows graphical sandboxes; a project can raise the
    /// floor, never the ceiling.</summary>
    [ObservableProperty]
    private bool _projGraphical;

    /// <summary>The repository this project is keyed to, as the editor's subtitle — or, for the catch-all
    /// project, what it is instead.</summary>
    public string SelectedProjectSubtitle => SelectedProject is { } p
        ? (p.RepoKey.Length == 0 ? "The project every repository without one of its own falls back to." : p.RepoKey)
        : string.Empty;

    /// <summary>The list pane's rows: one per project, the selected one marked. Rebuilt from
    /// <see cref="Projects"/> whenever it changes, so the page binds rows and the rest of the app keeps
    /// binding the DTOs.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProjectRowVm> ProjectRows { get; } = [];

    private bool _projectCollectionsHooked;

    private void HookProjectCollections()
    {
        if (_projectCollectionsHooked)
        {
            return;
        }

        // The MCP and USB lists are part of what "dirty" compares, so a row added or removed moves the flag.
        _projectCollectionsHooked = true;
        ProjectMcp.CollectionChanged += OnProjectListChanged;
        ProjectUsb.CollectionChanged += OnProjectListChanged;
        Projects.CollectionChanged += (_, _) => RebuildProjectRows();
        RebuildProjectRows();
    }

    private void RebuildProjectRows()
    {
        ProjectRows.Clear();
        foreach (var p in Projects)
        {
            ProjectRows.Add(new ProjectRowVm(p) { IsSelected = p.Id == SelectedProject?.Id });
        }
    }

    partial void OnSelectedProjectChanged(ProjectDto? value)
    {
        HookProjectCollections();
        foreach (var row in ProjectRows)
        {
            row.IsSelected = row.Project.Id == value?.Id;
        }

        OnPropertyChanged(nameof(SelectedProjectSubtitle));
        OnPropertyChanged(nameof(IsProjectDirty));
    }

    /// <summary>Whether the project has servers of its own — the help line under the list says so otherwise.</summary>
    public bool HasProjectMcp => ProjectMcp.Count > 0;

    private void OnProjectListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsProjectDirty));
        OnPropertyChanged(nameof(HasProjectMcp));
    }

    partial void OnProjNameChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjNodeChanged(bool value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjAptChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjNpmChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjPipChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjCpuChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjMemoryGiBChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjDiskGiBChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjGitModeChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjSkipPermissionsChanged(bool value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjMcpApprovalChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjAccountChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjRepoChanged(string value) => OnPropertyChanged(nameof(IsProjectDirty));
    partial void OnProjGraphicalChanged(bool value) => OnPropertyChanged(nameof(IsProjectDirty));
}

/// <summary>One project in the list pane: the DTO, plus whether it is the one open in the editor.</summary>
public sealed partial class ProjectRowVm(ProjectDto project) : ObservableObject
{
    public ProjectDto Project { get; } = project;

    public string Name => Project.Name;

    /// <summary>The repository key, or nothing for the catch-all project (which wears a tag instead).</summary>
    public string RepoKey => Project.RepoKey;

    public bool IsDefault => Project.RepoKey.Length == 0;

    [ObservableProperty]
    private bool _isSelected;
}
