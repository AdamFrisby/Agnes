using System.Text.Json;

namespace Agnes.Recording;

/// <summary>Reads/writes <see cref="SessionRecording"/> fixtures as JSON.</summary>
public static class RecordingStore
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static void Save(string path, SessionRecording recording)
        => File.WriteAllText(path, JsonSerializer.Serialize(recording, Options));

    public static SessionRecording Load(string path)
        => JsonSerializer.Deserialize<SessionRecording>(File.ReadAllText(path), Options)
           ?? throw new InvalidOperationException($"Empty or invalid recording: {path}");

    /// <summary>Loads every <c>*.json</c> recording in a directory (empty list if it doesn't exist).</summary>
    public static IReadOnlyList<SessionRecording> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var recordings = new List<SessionRecording>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f))
        {
            try
            {
                recordings.Add(Load(file));
            }
            catch
            {
                // Skip malformed files.
            }
        }

        return recordings;
    }
}
