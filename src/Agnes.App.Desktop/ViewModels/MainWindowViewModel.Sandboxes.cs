using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>The Sandboxes page's own state: the list in the order a person reads it, and the two things
/// the page is for beyond one row at a time — clearing every stopped VM, and finding the untracked ones.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Replaces the list with the host's answer: running first (the ones costing something), then
    /// paused, then stopped, most recently used first within each — and marks the rows this app has open.</summary>
    private void ReplaceSandboxes(IReadOnlyList<SandboxRecordDto> list)
    {
        var open = OpenTabs().Select(d => d.Session?.SessionId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        Sandboxes.Clear();
        foreach (var s in list
                     .OrderBy(s => string.Equals(s.State, "running", StringComparison.OrdinalIgnoreCase) ? 0
                         : string.Equals(s.State, "paused", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                     .ThenByDescending(s => s.LastUsedAt))
        {
            Sandboxes.Add(new SandboxRowVm(s) { IsOpenHere = open.Contains(s.SessionId) });
        }

        IsConfirmingDeleteStopped = false;
        OnPropertyChanged(nameof(HasSandboxes));
        OnPropertyChanged(nameof(HasStoppedSandboxes));
        OnPropertyChanged(nameof(DeleteStoppedLabel));
        SandboxesStatus = SandboxesSummary(list, ActiveHostName);
    }

    /// <summary>"7 sandboxes on AIPC25 · 3 running · 4 stopped" — the count where a person looks first.</summary>
    internal static string SandboxesSummary(IReadOnlyList<SandboxRecordDto> list, string hostName)
    {
        if (list.Count == 0)
        {
            return $"No sandboxes on {hostName} yet. A sandboxed session makes one, and it stays here after the session closes until you delete it.";
        }

        var running = list.Count(s => string.Equals(s.State, "running", StringComparison.OrdinalIgnoreCase));
        var paused = list.Count(s => string.Equals(s.State, "paused", StringComparison.OrdinalIgnoreCase));
        var stopped = list.Count - running - paused;
        var parts = new List<string> { list.Count == 1 ? $"One sandbox on {hostName}" : $"{list.Count} sandboxes on {hostName}" };
        if (running > 0) { parts.Add($"{running} running"); }
        if (paused > 0) { parts.Add($"{paused} paused"); }
        if (stopped > 0) { parts.Add($"{stopped} stopped"); }
        return string.Join(" · ", parts);
    }

    public bool HasStoppedSandboxes => Sandboxes.Any(s => !s.IsRunning);

    /// <summary>Two-step, like every delete here: the first click arms, the second destroys.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteStoppedLabel))]
    private bool _isConfirmingDeleteStopped;

    public string DeleteStoppedLabel
    {
        get
        {
            var n = Sandboxes.Count(s => !s.IsRunning);
            return IsConfirmingDeleteStopped
                ? (n == 1 ? "Delete it for good?" : $"Delete {n} for good?")
                : (n == 1 ? "Delete the stopped one" : $"Delete {n} stopped");
        }
    }

    public IAsyncRelayCommand DeleteStoppedSandboxesCommand => _deleteStoppedSandboxes ??= new AsyncRelayCommand(DeleteStoppedSandboxesAsync);
    private AsyncRelayCommand? _deleteStoppedSandboxes;

    /// <summary>Deletes every VM that is not running. Each is its own request, so one failure leaves the
    /// rest done, and the list re-read from the host says exactly what remains.</summary>
    private async Task DeleteStoppedSandboxesAsync()
    {
        var target = ActiveHttpHost();
        if (target is null) { return; }

        var stopped = Sandboxes.Where(s => !s.IsRunning).ToList();
        if (stopped.Count == 0) { return; }

        if (!IsConfirmingDeleteStopped)
        {
            foreach (var row in Sandboxes) { row.IsConfirmingDelete = false; }
            IsConfirmingDeleteStopped = true;
            SandboxesStatus = stopped.Count == 1
                ? $"This destroys the stopped VM for '{stopped[0].Title}' and everything in it. Click again to confirm."
                : $"This destroys {stopped.Count} stopped VMs and everything in them, permanently. Click again to confirm.";
            return;
        }

        var deleted = 0;
        string? failure = null;
        IReadOnlyList<SandboxRecordDto>? latest = null;
        foreach (var row in stopped)
        {
            try
            {
                latest = await SandboxManagement.DeleteAsync(target.Url, target.Token, row.SessionId, target.Http);
                deleted++;
            }
            catch (Exception ex)
            {
                failure ??= $"'{row.Title}': {Explain(ex)}";
            }
        }

        _dispatcher.Post(() =>
        {
            if (latest is not null) { ReplaceSandboxes(latest); }
            SandboxesStatus = failure is null
                ? (deleted == 1 ? "Deleted the stopped sandbox." : $"Deleted {deleted} stopped sandboxes.")
                : $"Deleted {deleted}; one could not be — {failure}";
        });
    }

    /// <summary>Whether a scan has run, so the orphan section can say "none found" rather than nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrphansNote))]
    private bool _orphansScanned;

    public string OrphansNote => !OrphansScanned
        ? "Scan the host for Agnes VMs no session tracks — left behind by an earlier daemon run, for example. Nothing is deleted until you confirm."
        : HasOrphans
            ? (OrphanVmNames.Count == 1 ? "One VM no session tracks. Delete it if you are sure." : $"{OrphanVmNames.Count} VMs no session tracks. Delete them if you are sure.")
            : "Every Agnes VM on the host belongs to a session. Nothing to clean up.";
}
