using System.Text.Json;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The host's library of saved prompts and slash-token templates, persisted to
/// <c>~/.agnes/prompt-library.json</c>. Mirrors the other host stores (single lock, atomic tmp-move,
/// load-tolerant of a missing/corrupt file). Prompts are the reusable text; a template maps a slash token
/// (e.g. <c>/review</c>) to a prompt plus an <see cref="TemplateBehavior"/>. The library is the single source
/// of truth — a template referencing a deleted prompt resolves to a <em>broken</em> state, never to nothing.
/// </summary>
public sealed class PromptLibrary
{
    /// <summary>The file name under the data directory.</summary>
    public const string FileName = "prompt-library.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger<PromptLibrary>? _logger;
    private State _state = new();

    /// <summary>The default data directory (<c>~/.agnes</c>) hosts persist under.</summary>
    public static string DefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes");

    /// <param name="directory">
    /// Directory to persist the library under (production passes <see cref="DefaultDirectory"/>). When null or
    /// blank the store is in-memory only and never touches disk — used by tests.
    /// </param>
    public PromptLibrary(string? directory = null, ILogger<PromptLibrary>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
        _logger = logger;
        Load();
    }

    // ---- prompts ----

    /// <summary>Saved prompts, ordered by title (never null).</summary>
    public IReadOnlyList<LibraryPrompt> List()
    {
        lock (_gate)
        {
            return _state.Prompts.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>The enabled system-prompt additions, ordered by title — the standing instructions to prepend
    /// to a session's system prompt at open. A prompt with <see cref="LibraryPrompt.IsSystemPromptAddition"/>
    /// false is excluded.</summary>
    public IReadOnlyList<LibraryPrompt> ListSystemPromptAdditions()
    {
        lock (_gate)
        {
            return _state.Prompts
                .Where(p => p.IsSystemPromptAddition)
                .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>
    /// Assembles the system-prompt text to prepend at session open: the bodies of every enabled system-prompt
    /// addition, in order, joined by blank lines. Returns null when there are no additions (so the session-open
    /// path can leave the effective system prompt untouched). Pure over the current state, hence unit-testable.
    /// </summary>
    public string? AssembleSystemPromptAdditions()
    {
        var additions = ListSystemPromptAdditions();
        if (additions.Count == 0)
        {
            return null;
        }

        return string.Join("\n\n", additions.Select(p => p.MarkdownBody));
    }

    /// <summary>Upserts a prompt (assigning an id when blank) and persists it; returns the stored prompt.</summary>
    public LibraryPrompt Save(LibraryPrompt prompt)
    {
        lock (_gate)
        {
            var stored = string.IsNullOrWhiteSpace(prompt.Id)
                ? prompt with { Id = Guid.NewGuid().ToString("n") }
                : prompt;

            _state.Prompts.RemoveAll(p => p.Id == stored.Id);
            _state.Prompts.Add(stored);
            Persist();
            return stored;
        }
    }

    /// <summary>Deletes a prompt by id; returns true if one was removed. Templates referencing it are left in
    /// place (they become broken on resolve) rather than silently cascade-deleted.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_state.Prompts.RemoveAll(p => p.Id == id) == 0)
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    // ---- templates ----

    /// <summary>Saved templates, ordered by slash token (never null).</summary>
    public IReadOnlyList<PromptTemplate> ListTemplates()
    {
        lock (_gate)
        {
            return _state.Templates.OrderBy(t => t.SlashToken, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>Upserts a template keyed by its (normalized) slash token and persists it.</summary>
    public PromptTemplate SaveTemplate(PromptTemplate template)
    {
        lock (_gate)
        {
            var stored = template with { SlashToken = Normalize(template.SlashToken) };
            _state.Templates.RemoveAll(t => Normalize(t.SlashToken) == stored.SlashToken);
            _state.Templates.Add(stored);
            Persist();
            return stored;
        }
    }

    /// <summary>Deletes a template by slash token; returns true if one was removed.</summary>
    public bool DeleteTemplate(string token)
    {
        lock (_gate)
        {
            var normalized = Normalize(token);
            if (_state.Templates.RemoveAll(t => Normalize(t.SlashToken) == normalized) == 0)
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    /// <summary>
    /// Resolves a template's slash token to its referenced prompt. Returns the prompt when it exists;
    /// <c>Broken == true</c> when a template with that token exists but its prompt is gone (deleted). An
    /// unknown token that matches no template returns <c>(null, false)</c> — nothing to resolve, not broken.
    /// Pure over the current state, so it's unit-testable without any UI.
    /// </summary>
    public (LibraryPrompt? Prompt, bool Broken) Resolve(string token)
    {
        lock (_gate)
        {
            var normalized = Normalize(token);
            var template = _state.Templates.FirstOrDefault(t => Normalize(t.SlashToken) == normalized);
            if (template is null)
            {
                return (null, false);
            }

            var prompt = _state.Prompts.FirstOrDefault(p => p.Id == template.PromptId);
            return (prompt, prompt is null);
        }
    }

    /// <summary>Strips a single leading slash and trims, so <c>/review</c> and <c>review</c> are one token.</summary>
    private static string Normalize(string token)
        => (token ?? string.Empty).Trim().TrimStart('/');

    private void Load()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_path))
            {
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Options) ?? new State();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load prompt library from {Path}; starting empty.", _path);
            _state = new State();
        }
    }

    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist prompt library to {Path}.", _path);
        }
    }

    private sealed class State
    {
        public List<LibraryPrompt> Prompts { get; init; } = [];
        public List<PromptTemplate> Templates { get; init; } = [];
    }
}
