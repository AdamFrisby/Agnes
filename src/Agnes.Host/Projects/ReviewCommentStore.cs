using System.Text.Json;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Projects;

/// <summary>
/// Persists the host's review comments (<c>~/.agnes/review-comments.json</c>), scoped by project so a
/// comment survives past the session it was created in. Mirrors the other host stores (atomic tmp-move,
/// a single lock, load-tolerant of a missing/corrupt file).
/// </summary>
public sealed class ReviewCommentStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<ReviewCommentStore>? _logger;
    private List<ReviewComment> _comments = new();

    public ReviewCommentStore(string path, ILogger<ReviewCommentStore>? logger = null)
    {
        _path = path;
        _logger = logger;
        Load();
    }

    /// <summary>The comments left on a project (oldest first), never null.</summary>
    public IReadOnlyList<ReviewComment> ListForProject(string projectId)
    {
        lock (_gate)
        {
            return _comments.Where(c => c.ProjectId == projectId).ToArray();
        }
    }

    /// <summary>Adds a comment (assigning it an id + creation time) and persists it.</summary>
    public ReviewComment Add(string projectId, string filePath, int lineNumber, string lineHash, string text)
    {
        var comment = new ReviewComment(
            Guid.NewGuid().ToString("n"), projectId, filePath, lineNumber, lineHash, text, DateTimeOffset.UtcNow);

        lock (_gate)
        {
            _comments.Add(comment);
            Persist();
            return comment;
        }
    }

    /// <summary>Removes a comment by id; returns true if one was removed.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var comment = _comments.FirstOrDefault(c => c.Id == id);
            if (comment is null)
            {
                return false;
            }

            _comments.Remove(comment);
            Persist();
            return true;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _comments = JsonSerializer.Deserialize<List<ReviewComment>>(File.ReadAllText(_path), Options) ?? new List<ReviewComment>();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load review comments; starting empty.");
            _comments = new List<ReviewComment>();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_comments, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist review comments.");
        }
    }
}
