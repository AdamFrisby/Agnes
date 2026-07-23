using System.Text.Json;

namespace Agnes.Ui.Core.Onboarding;

/// <summary>
/// Persists <see cref="OnboardingState"/> to a JSON file under the app data dir, mirroring the other
/// client-local stores (prompts, settings, hosts). Reads and writes are best-effort: a missing, corrupt, or
/// unwritable file falls back to defaults rather than throwing, so a broken onboarding file never blocks launch.
/// </summary>
public sealed class FileOnboardingStore : IOnboardingStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public FileOnboardingStore(string? path = null) => _path = path ?? DefaultPath();

    public static string DefaultPath()
    {
        // Resilient to sandboxed/virtual file systems (e.g. WASM), where ApplicationData may be empty and
        // directory creation can throw — fall back to temp, matching FilePromptStore.
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(string.IsNullOrEmpty(appData) ? Path.GetTempPath() : appData, "Agnes");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "onboarding.json");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "agnes-onboarding.json");
        }
    }

    public OnboardingState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<OnboardingState>(File.ReadAllText(_path), Options) ?? new OnboardingState();
            }
        }
        catch
        {
            // Corrupt/unreadable file: start clean rather than crash.
        }

        return new OnboardingState();
    }

    public void Save(OnboardingState state)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(state, Options));
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
