using System.Text.Json;

namespace Agnes.App.Desktop.Persistence;

/// <summary>Everything needed to restore (reconnect to) a session tab after relaunch.</summary>
public sealed record SessionDescriptor(
    string HostName,
    string HostUrl,
    string Token,
    string SessionId,
    string AdapterId,
    string Title);

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
