using System.Text.Json;

namespace Agnes.App.Desktop.Persistence;

/// <summary>Everything needed to restore (reconnect to) a session tab after relaunch.</summary>
public sealed record SessionDescriptor(
    string HostName,
    string HostUrl,
    string Token,
    string SessionId,
    string AdapterId,
    string Title,
    bool Pinned = false,
    IReadOnlyList<string>? Tags = null,
    string? ScreenId = null)
{
    /// <summary>A plugin screen tab (CodeyBox, say) rather than a session: it names the screen and nothing
    /// else, and comes back on restore when a loaded plugin still offers that screen. Older tab files have
    /// no such field and read as sessions, as they always did.</summary>
    public bool IsScreen => !string.IsNullOrEmpty(ScreenId);

    /// <summary>The descriptor a plugin screen saves as.</summary>
    public static SessionDescriptor ForScreen(string screenId, string title)
        => new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, title, ScreenId: screenId);
}

/// <summary>Persists the set of open session tabs so they auto-reconnect on relaunch.</summary>
public sealed class SessionStateStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public SessionStateStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Agnes");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "desktop-tabs.json");
    }

    public IReadOnlyList<SessionDescriptor> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<SessionDescriptor>>(File.ReadAllText(_path), Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<SessionDescriptor> tabs)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(tabs, Options));
        }
        catch
        {
            // Persistence is best-effort; ignore IO failures.
        }
    }
}
