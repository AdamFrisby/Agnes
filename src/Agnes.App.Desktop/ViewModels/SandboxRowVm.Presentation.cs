using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// How a sandbox row reads on the Sandboxes page. The record says where the VM is and what it is called;
/// this says what a person needs to decide between seven of them: is it running, is it the one I have
/// open, how old is it, and which folder is it for — in a form that fits on one line.
/// </summary>
public sealed partial class SandboxRowVm
{
    /// <summary>The host says "running" / "stopped" / "paused"; a running VM is the one costing CPU and RAM.</summary>
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    public bool IsPaused => string.Equals(State, "paused", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this app has a tab on the sandbox's session right now — then "Open" is "Go to tab".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenLabel))]
    [NotifyPropertyChangedFor(nameof(OpenTooltip))]
    private bool _isOpenHere;

    public string OpenLabel => IsOpenHere ? "Go to tab" : "Open";

    public string OpenTooltip => IsOpenHere
        ? "Switch to this session's tab"
        : IsRunning
            ? "Open this session in a tab"
            : "Start the VM again and open its session in a tab";

    /// <summary>The agent the session runs ("claude-code-native"), which tells two same-named sandboxes apart.</summary>
    public string Agent => Record.AdapterId;

    /// <summary>"~/Projects/dawn2", or the last two segments of a long scratch path. The full path is the tooltip.</summary>
    public string ShortDirectory => Shorten(WorkingDirectory);

    /// <summary>"created 2 d ago · used 16 h ago" — created answers "how old", used answers "is anyone still on it".</summary>
    public string Age
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var created = Ago(now - Record.CreatedAt);
            var used = Ago(now - Record.LastUsedAt);
            return created == used ? $"created {created}" : $"created {created} · used {used}";
        }
    }

    internal static string Shorten(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1 && path.StartsWith(home, StringComparison.Ordinal))
        {
            return "~" + path[home.Length..];
        }

        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 4 ? "…/" + string.Join('/', parts[^2..]) : path;
    }

    internal static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 2 } => "just now",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} h ago",
        { TotalDays: < 30 } => $"{(int)span.TotalDays} d ago",
        _ => $"{(int)(span.TotalDays / 30)} mo ago",
    };
}
