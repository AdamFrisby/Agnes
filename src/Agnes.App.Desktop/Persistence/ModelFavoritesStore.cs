using System.Text.Json;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.App.Desktop.Persistence;

/// <summary>
/// Persists the user's favorite models — pure client-side state keyed by <c>(AgentId, ModelId)</c>, with no
/// host involvement (see <c>.ideas/providers/05-model-and-engine-selection.md</c>). A small JSON file under
/// the app data dir, best-effort like the other local prefs (<see cref="SettingsStore"/>).
/// </summary>
public sealed class ModelFavoritesStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly List<ModelFavorite> _favorites;

    public ModelFavoritesStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        _favorites = Load();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Agnes");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "model-favorites.json");
    }

    /// <summary>All favorites across every agent (the reconciler filters per agent).</summary>
    public IReadOnlyList<ModelFavorite> All => _favorites;

    public bool IsFavorite(string agentId, string modelId) =>
        _favorites.Any(f => f.AgentId == agentId && f.ModelId == modelId);

    /// <summary>Toggles a favorite and persists. Returns the new state (true = now favorited).</summary>
    public bool Toggle(string agentId, string modelId)
    {
        var existing = _favorites.FindIndex(f => f.AgentId == agentId && f.ModelId == modelId);
        bool nowFavorite;
        if (existing >= 0)
        {
            _favorites.RemoveAt(existing);
            nowFavorite = false;
        }
        else
        {
            _favorites.Add(new ModelFavorite(agentId, modelId));
            nowFavorite = true;
        }

        Save();
        return nowFavorite;
    }

    private List<ModelFavorite> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<List<ModelFavorite>>(File.ReadAllText(_path), Options) ?? [];
            }
        }
        catch
        {
            // fall through to an empty list
        }

        return [];
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_favorites, Options));
        }
        catch
        {
            // best-effort
        }
    }
}
