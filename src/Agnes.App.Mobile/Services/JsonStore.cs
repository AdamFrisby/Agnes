using System.Text.Json;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// A tiny load/save-a-record-to-JSON helper for the app's own local state. Every write is
/// best-effort: losing a preference is never worth crashing a phone app, and Android can revoke
/// storage under us during a low-memory kill.
/// </summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();

    private static string? _overrideDirectory;

    /// <summary>Redirects local state elsewhere (the headless preview harness points it at a temp
    /// directory so a render never touches real device state).</summary>
    public static void UseDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        _overrideDirectory = path;
    }

    /// <summary>The app's private data directory. On Android this is the per-app sandbox, so nothing here
    /// is readable by other apps and it's removed with the app.</summary>
    public static string Directory
    {
        get
        {
            if (_overrideDirectory is { } overridden)
            {
                return overridden;
            }

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(string.IsNullOrEmpty(appData) ? Path.GetTempPath() : appData, "Agnes");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return Path.GetTempPath();
            }
        }
    }

    public static string PathFor(string fileName) => Path.Combine(Directory, fileName);

    public static T Load<T>(string fileName, T fallback)
    {
        try
        {
            var path = PathFor(fileName);
            if (!File.Exists(path))
            {
                return fallback;
            }

            lock (Gate)
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback;
            }
        }
        catch
        {
            return fallback;
        }
    }

    public static void Save<T>(string fileName, T value)
    {
        try
        {
            lock (Gate)
            {
                File.WriteAllText(PathFor(fileName), JsonSerializer.Serialize(value, Options));
            }
        }
        catch
        {
            // Persisting local UI state is best-effort by design.
        }
    }
}
