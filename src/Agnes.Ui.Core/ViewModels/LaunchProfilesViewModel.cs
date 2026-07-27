using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives the launch-profiles management surface (settings): the host's saved, reusable new-session launch
/// configs. Host-agnostic — it talks to whatever <see cref="IAgnesHost"/> the accessor returns, so it drives a
/// real SignalR host and the offline simulation identically, and every change goes over the wire. Creating a
/// profile happens in the new-session surface ("Save current as profile…"); this surface lists them, renames
/// them, and deletes them. Deleting a profile never affects a session already launched from it.
/// </summary>
public sealed class LaunchProfilesViewModel : ObservableObject
{
    private readonly Func<IAgnesHost?> _host;
    private readonly IUiDispatcher _dispatcher;

    public LaunchProfilesViewModel(Func<IAgnesHost?> host, IUiDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        DeleteCommand = new AsyncRelayCommand<LaunchProfileRowVm>(DeleteAsync);
        BeginRenameCommand = new RelayCommand<LaunchProfileRowVm>(BeginRename);
        CommitRenameCommand = new AsyncRelayCommand<LaunchProfileRowVm>(CommitRenameAsync);
        CancelRenameCommand = new RelayCommand<LaunchProfileRowVm>(row => { if (row is not null) { row.IsRenaming = false; } });
    }

    /// <summary>The host's saved launch profiles, each as a row that can describe and rename itself.</summary>
    public ObservableCollection<LaunchProfileRowVm> Profiles { get; } = [];

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public bool HasProfiles => Profiles.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>Puts one row into rename mode (its name becomes an editable draft).</summary>
    public ICommand BeginRenameCommand { get; }

    /// <summary>Saves a renamed profile over the wire; every other captured option is preserved.</summary>
    public IAsyncRelayCommand<LaunchProfileRowVm> CommitRenameCommand { get; }

    public ICommand CancelRenameCommand { get; }

    /// <summary>Loads the profiles from the host and rebuilds the list.</summary>
    public async Task RefreshAsync()
    {
        var host = _host();
        if (host is null)
        {
            _dispatcher.Post(() => { Profiles.Clear(); OnPropertyChanged(nameof(HasProfiles)); Status = "Connect to a host to manage launch profiles."; });
            return;
        }

        try
        {
            var profiles = await host.GetLaunchProfilesAsync().ConfigureAwait(false);
            _dispatcher.Post(() => Rebuild(profiles));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't load launch profiles: " + ex.Message);
        }
    }

    private void Rebuild(IReadOnlyList<LaunchProfile> profiles)
    {
        Profiles.Clear();
        foreach (var p in profiles)
        {
            Profiles.Add(new LaunchProfileRowVm(p));
        }

        OnPropertyChanged(nameof(HasProfiles));
        Status = $"{Profiles.Count} launch profile(s).";
    }

    private static void BeginRename(LaunchProfileRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        row.DraftName = row.Name;
        row.IsRenaming = true;
    }

    private async Task CommitRenameAsync(LaunchProfileRowVm? row)
    {
        var host = _host();
        var name = row?.DraftName?.Trim();
        if (host is null || row is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (string.Equals(name, row.Name, StringComparison.Ordinal))
        {
            _dispatcher.Post(() => row.IsRenaming = false);
            return;
        }

        try
        {
            // Save the same profile with a new name — the captured launch options ride along untouched.
            await host.SaveLaunchProfileAsync(row.Profile with { Name = name }).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                row.IsRenaming = false;
                Status = "Couldn't rename the profile: " + ex.Message;
            });
        }
    }

    private async Task DeleteAsync(LaunchProfileRowVm? row)
    {
        var host = _host();
        if (host is null || row is null)
        {
            return;
        }

        try
        {
            await host.DeleteLaunchProfileAsync(row.Profile.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't delete the profile: " + ex.Message);
        }
    }
}

/// <summary>
/// One saved launch profile as a bindable row. A <see cref="LaunchProfile"/> captures eleven decisions about
/// how a session starts; a list that shows only its name and adapter id can't answer the one question anyone
/// brings to this page — "what would picking this actually do?" — so the row spells the captured options out
/// and lets the name be edited in place.
/// </summary>
public sealed class LaunchProfileRowVm : ObservableObject
{
    public LaunchProfileRowVm(LaunchProfile profile)
    {
        Profile = profile;
        Where = DescribeWhere(profile);
        Posture = DescribePosture(profile);
    }

    public LaunchProfile Profile { get; }

    public string Name => Profile.Name;

    /// <summary>Which agent, in which directory, in a sandbox or on the host.</summary>
    public string Where { get; }

    /// <summary>The permission/credential decisions the profile pins, and the model if it names one.</summary>
    public string Posture { get; }

    private bool _isRenaming;
    public bool IsRenaming { get => _isRenaming; set => SetProperty(ref _isRenaming, value); }

    private string _draftName = string.Empty;
    public string DraftName { get => _draftName; set => SetProperty(ref _draftName, value); }

    private static string DescribeWhere(LaunchProfile p)
    {
        var parts = new List<string> { p.AdapterId };
        parts.Add(string.IsNullOrWhiteSpace(p.WorkingDirectory) ? "any directory (asked at launch)" : p.WorkingDirectory);
        parts.Add(p.UseSandbox ? "in a sandbox VM" : "on the host");
        if (p.UseWorktree)
        {
            parts.Add("own git worktree");
        }

        return string.Join(" · ", parts);
    }

    private static string DescribePosture(LaunchProfile p)
    {
        var parts = new List<string>
        {
            p.SkipPermissions ? "autonomous — no tool prompts" : "asks before each tool",
            $"MCP tools: {p.McpApproval}",
            $"git credentials: {p.GitCredentialMode}",
        };
        if (!string.IsNullOrWhiteSpace(p.ModelId))
        {
            parts.Add($"model {p.ModelId}");
        }

        return string.Join(" · ", parts);
    }
}
