using System.Text.Json;

namespace Agnes.App.Desktop.Persistence;

/// <summary>User accessibility / UI preferences, window geometry, theme and density.</summary>
public sealed record AppSettings(
    bool ReducedMotion = false,
    double WindowWidth = 1180,
    double WindowHeight = 760,
    int WindowX = int.MinValue,
    int WindowY = int.MinValue,
    bool WindowMaximized = false,
    string Theme = "System",
    double FontScale = 1.0,
    string WorkingDirectory = "",
    string McpApproval = "Ask",
    bool GitHubPromptShown = false);

/// <summary>Persists <see cref="AppSettings"/> to a JSON file (best-effort).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string? path = null) => _path = path ?? DefaultPath();

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Agnes");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
            }
        }
        catch
        {
            // fall through to defaults
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // best-effort
        }
    }
}
