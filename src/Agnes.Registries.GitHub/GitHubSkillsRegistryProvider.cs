using Agnes.Abstractions;

namespace Agnes.Registries.GitHub;

/// <summary>
/// A skill registry that is simply a public GitHub repository of bundles. Defaults to
/// <c>anthropics/skills</c> — the official, first-party set — and points at any other repo with one setting,
/// which is what most teams actually want: their own repo of internal skills, browsable and installable in the
/// same place as everything else.
///
/// The whole repository is listed in one API call, so browsing costs a single request; the tree is cached
/// briefly because a user searching, then installing, then searching again shouldn't spend three of GitHub's
/// sixty anonymous requests an hour on the same answer.
/// </summary>
public sealed class GitHubSkillsRegistryProvider : IPromptRegistryProvider
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly GitHubSkillBundles _bundles;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _branch;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GitHubSkillBundles.SkillTree? _cached;
    private DateTimeOffset _cachedAt;

    public GitHubSkillsRegistryProvider(
        GitHubSkillBundles bundles,
        string owner = "anthropics",
        string repo = "skills",
        string branch = "main",
        string? id = null,
        TimeProvider? timeProvider = null)
    {
        _bundles = bundles;
        _owner = owner;
        _repo = repo;
        _branch = branch;
        _time = timeProvider ?? TimeProvider.System;
        Id = id ?? $"github:{owner}/{repo}";
    }

    public string Id { get; }

    public string DisplayName => $"GitHub — {_owner}/{_repo}";

    /// <summary>Searching is a filter over the one tree call, not a second round trip.</summary>
    public bool SupportsSearch => true;

    public async Task<IReadOnlyList<RegistrySkillEntry>> ListAsync(CancellationToken ct = default)
        => (await TreeAsync(ct).ConfigureAwait(false)).Entries;

    /// <summary>
    /// Filters the repository's bundles by title, description and path. The whole tree is already in hand, so
    /// this is a local match rather than a request — GitHub has no search over a single repo's file contents
    /// that would be worth the extra call.
    /// </summary>
    public async Task<IReadOnlyList<RegistrySkillEntry>> SearchAsync(string query, CancellationToken ct = default)
    {
        var entries = await ListAsync(ct).ConfigureAwait(false);
        var q = query?.Trim() ?? string.Empty;
        if (q.Length == 0)
        {
            return entries;
        }

        return entries.Where(e =>
                e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (e.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();
    }

    public Task<LibrarySkill> FetchAsync(string entryId, string destinationDir, CancellationToken ct = default)
        => _bundles.FetchAsync(_owner, _repo, _branch, entryId, destinationDir, ct);

    private async Task<GitHubSkillBundles.SkillTree> TreeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } cached && _time.GetUtcNow() - _cachedAt < CacheFor)
            {
                return cached;
            }

            _cached = await _bundles.ListAsync(_owner, _repo, _branch, ct).ConfigureAwait(false);
            _cachedAt = _time.GetUtcNow();
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
