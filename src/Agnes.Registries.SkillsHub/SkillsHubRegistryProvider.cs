using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Agnes.Registries.GitHub;

namespace Agnes.Registries.SkillsHub;

/// <summary>
/// The <see href="https://skillshub.wtf">skillshub.wtf</see> skill registry: an index across thousands of
/// GitHub-published skills, searchable by name, description and tag, with no account or key needed.
///
/// Two things shape this implementation:
///
/// Its size. Fourteen thousand entries cannot be listed, so <see cref="ListAsync"/> asks for the trending
/// front page and <see cref="SearchAsync"/> is the real entry point — the registry runs the query, we don't.
/// A search for "pdf" returns hundreds of hits, most of them named exactly "pdf", so each entry carries its
/// publisher, stars and download count: without those the result list is unpickable.
///
/// Where the skills actually live. The index is metadata; every entry names a GitHub owner, repo and slug, and
/// the bundle itself is a directory in that repository. Fetching therefore goes to GitHub via
/// <see cref="GitHubSkillBundles"/> and takes the whole directory. skillshub's own
/// <c>/{owner}/{repo}/{slug}?format=md</c> would hand back a single markdown file, and a SKILL.md that says
/// "see REFERENCE.md" is worse than useless without the REFERENCE.md beside it.
/// </summary>
public sealed class SkillsHubRegistryProvider : IPromptRegistryProvider
{
    private const string DefaultBaseUrl = "https://skillshub.wtf";
    private const int PageSize = 30;

    private readonly HttpClient _http;
    private readonly GitHubSkillBundles _bundles;
    private readonly string _baseUrl;

    public SkillsHubRegistryProvider(HttpClient http, GitHubSkillBundles bundles, string? baseUrl = null)
    {
        _http = http;
        _bundles = bundles;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public string Id => "skillshub";

    public string DisplayName => "SkillsHub (skillshub.wtf)";

    public bool SupportsSearch => true;

    /// <summary>The registry's trending skills — the only sensible answer to "show me what's here" for an
    /// index this size.</summary>
    public async Task<IReadOnlyList<RegistrySkillEntry>> ListAsync(CancellationToken ct = default)
        => await GetAsync($"{_baseUrl}/api/v1/skills/trending?limit={PageSize}", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<RegistrySkillEntry>> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? string.Empty;
        if (q.Length == 0)
        {
            return await ListAsync(ct).ConfigureAwait(false);
        }

        // sort=stars puts the well-known publisher's version of a common name (there are many "pdf" skills)
        // at the top, which is nearly always the one someone means.
        return await GetAsync(
            $"{_baseUrl}/api/v1/skills/search?q={Uri.EscapeDataString(q)}&sort=stars&limit={PageSize}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the complete bundle from the GitHub repository the index points at. <paramref name="entryId"/>
    /// is the <c>owner/repo/slug</c> triple <see cref="EntryId"/> built when the entry was listed — everything
    /// needed to fetch it, so no lookup call is required first.
    /// </summary>
    public Task<LibrarySkill> FetchAsync(string entryId, string destinationDir, CancellationToken ct = default)
    {
        var parts = entryId.Split('/');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException(
                $"'{entryId}' isn't a SkillsHub entry id (expected owner/repo/slug).");
        }

        var (owner, repo, slug) = (parts[0], parts[1], parts[2]);
        return FetchFromGitHubAsync(owner, repo, slug, destinationDir, ct);
    }

    private async Task<LibrarySkill> FetchFromGitHubAsync(
        string owner, string repo, string slug, string destinationDir, CancellationToken ct)
    {
        // The index records which repository a skill came from but not where in it, and layouts differ
        // (anthropics/skills keeps them under skills/, others at the root). One tree call settles it.
        var tree = await _bundles.ListAsync(owner, repo, "main", ct).ConfigureAwait(false);
        var match = tree.Entries.FirstOrDefault(e => LastSegment(e.Id).Equals(slug, StringComparison.OrdinalIgnoreCase))
                    ?? tree.Entries.FirstOrDefault(e => e.Title.Equals(slug, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"{owner}/{repo} no longer contains a skill called '{slug}'.");

        return await _bundles.FetchAsync(owner, repo, "main", match.Id, destinationDir, ct).ConfigureAwait(false);
    }

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    private async Task<IReadOnlyList<RegistrySkillEntry>> GetAsync(string url, CancellationToken ct)
    {
        var page = await _http.GetFromJsonAsync<SkillsHubPage>(url, ct).ConfigureAwait(false);
        return (page?.Data ?? [])
            .Where(s => s.Repo?.GitHubOwner is { Length: > 0 } && s.Repo.GitHubRepoName is { Length: > 0 })
            .Select(ToEntry)
            .ToArray();
    }

    private static RegistrySkillEntry ToEntry(SkillsHubSkill skill)
    {
        var owner = skill.Repo!.GitHubOwner!;
        var repo = skill.Repo.GitHubRepoName!;
        var slug = skill.Slug is { Length: > 0 } ? skill.Slug : skill.Name;
        return new RegistrySkillEntry(
            EntryId(owner, repo, slug),
            skill.Name,
            skill.Description,
            $"github.com/{owner}/{repo}")
        {
            Publisher = skill.Owner?.DisplayName is { Length: > 0 } display ? display : owner,
            Tags = skill.Tags ?? [],
            Stars = skill.Repo.StarCount,
            Downloads = skill.Repo.DownloadCount,
        };
    }

    /// <summary>The id an entry travels under: everything a fetch needs, and stable across listings.</summary>
    public static string EntryId(string owner, string repo, string slug) => $"{owner}/{repo}/{slug}";

    // The slice of skillshub's search/trending response we read, typed at the boundary. Its published API
    // documentation describes owner/repo as plain strings; the service actually returns objects, so this
    // follows the service.
    private sealed record SkillsHubPage([property: JsonPropertyName("data")] IReadOnlyList<SkillsHubSkill>? Data);

    private sealed record SkillsHubSkill(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
        [property: JsonPropertyName("repo")] SkillsHubRepo? Repo,
        [property: JsonPropertyName("owner")] SkillsHubOwner? Owner);

    private sealed record SkillsHubRepo(
        [property: JsonPropertyName("githubOwner")] string? GitHubOwner,
        [property: JsonPropertyName("githubRepoName")] string? GitHubRepoName,
        [property: JsonPropertyName("starCount")] int? StarCount,
        [property: JsonPropertyName("downloadCount")] int? DownloadCount);

    private sealed record SkillsHubOwner(
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("displayName")] string? DisplayName);
}
