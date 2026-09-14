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

    /// <summary>
    /// Builds this shape's serializer converters now, by reading a throwaway document of it.
    ///
    /// <para>There is no source-generated context here, so the first <c>Deserialize&lt;T&gt;</c> of a
    /// shape reflects over it, builds converters for every member, and JITs the lot. On a tablet that
    /// first read cost 363 ms — spent in the middle of the shell's constructor, because that is where
    /// the first real read happened to be. Doing it early and off-thread does not make the work smaller;
    /// it makes it overlap with the platform's own start-up instead of queueing behind it.</para>
    /// </summary>
    public static void Prewarm<T>(string emptyDocument)
    {
        try
        {
            JsonSerializer.Deserialize<T>(emptyDocument, Options);
        }
        catch
        {
            // Warming is an optimisation. If a shape can't be read from an empty document, the real read
            // will say so properly, in the place that can do something about it.
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
