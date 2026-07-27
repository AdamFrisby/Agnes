using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Registries.GitHub;

/// <summary>
/// Reads skill bundles out of a public GitHub repository. Shared machinery rather than a plugin in its own
/// right — the same way <c>Agnes.Acp</c> backs every ACP agent plugin — because more than one registry needs
/// it: this package's own <see cref="GitHubSkillsRegistryProvider"/> browses a repo directly, and
/// <c>Agnes.Registries.SkillsHub</c> indexes GitHub-hosted skills and comes back here to download the
/// complete bundle.
///
/// Downloading the <em>complete</em> bundle is the point. A skill is a directory: <c>SKILL.md</c> routinely
/// says "see REFERENCE.md" or "read FORMS.md and follow its instructions", so a fetch that took only the one
/// markdown file would install something that reads as complete and then dead-ends the agent at first use.
///
/// One tree call lists the whole repository, which keeps discovery inside GitHub's unauthenticated rate limit
/// (60/hour/IP); file contents come from raw.githubusercontent.com, which that limit doesn't apply to. A
/// token, when the host has one, is used only to raise the API limit.
/// </summary>
public sealed class GitHubSkillBundles
{
    private const string Api = "https://api.github.com";
    private const string Raw = "https://raw.githubusercontent.com";

    /// <summary>How many SKILL.md files a listing will open to read titles/descriptions from frontmatter.
    /// A repository with hundreds of bundles is listed by directory name rather than fanning out to hundreds
    /// of requests; the cap is reported by <see cref="SkillTree.Truncated"/> instead of being silent.</summary>
    public const int FrontmatterBudget = 60;

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string?>>? _token;

    /// <param name="token">Resolves a github.com token to raise the API rate limit, or null to stay anonymous.</param>
    public GitHubSkillBundles(HttpClient http, Func<CancellationToken, Task<string?>>? token = null)
    {
        _http = http;
        _token = token;
        // GitHub rejects requests with no User-Agent outright.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Agnes", "1.0"));
        }
    }

    /// <summary>Every skill bundle in a repository, as directory paths plus whatever frontmatter was read.</summary>
    /// <param name="Truncated">True when more bundles exist than <see cref="FrontmatterBudget"/> allows
    /// frontmatter to be read for; the extras are still listed, by directory name.</param>
    public sealed record SkillTree(IReadOnlyList<RegistrySkillEntry> Entries, bool Truncated);

    /// <summary>
    /// Lists the bundles in <paramref name="owner"/>/<paramref name="repo"/> — every directory holding a
    /// <c>SKILL.md</c>, at any depth. The entry id is that directory's path, which is all
    /// <see cref="FetchAsync"/> needs to fetch it again.
    /// </summary>
    public async Task<SkillTree> ListAsync(string owner, string repo, string branch, CancellationToken ct = default)
    {
        var tree = await GetTreeAsync(owner, repo, branch, ct).ConfigureAwait(false);
        var skillDirs = tree
            .Where(e => e.Type == "blob" && e.Path.EndsWith("/" + SkillFileName, StringComparison.Ordinal))
            .Select(e => e.Path[..^(SkillFileName.Length + 1)])
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        var withFrontmatter = skillDirs.Take(FrontmatterBudget).ToArray();
        var read = await Task.WhenAll(withFrontmatter.Select(async dir =>
        {
            var text = await TryGetStringAsync($"{Raw}/{owner}/{repo}/{branch}/{dir}/{SkillFileName}", ct).ConfigureAwait(false);
            var (name, description) = text is null ? (null, null) : ParseFrontmatter(text);
            return Entry(owner, repo, dir, name, description);
        })).ConfigureAwait(false);

        var rest = skillDirs.Skip(FrontmatterBudget).Select(dir => Entry(owner, repo, dir, null, null));
        return new SkillTree([.. read, .. rest], skillDirs.Length > FrontmatterBudget);
    }

    /// <summary>
    /// Downloads the bundle at <paramref name="skillPath"/> into <paramref name="destinationDir"/> — the
    /// <c>SKILL.md</c> plus every other file in that directory and below it, so relative references inside
    /// the skill still resolve once installed.
    /// </summary>
    public async Task<LibrarySkill> FetchAsync(
        string owner, string repo, string branch, string skillPath, string destinationDir, CancellationToken ct = default)
    {
        var prefix = skillPath.TrimEnd('/') + "/";
        var tree = await GetTreeAsync(owner, repo, branch, ct).ConfigureAwait(false);
        var files = tree
            .Where(e => e.Type == "blob" && e.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(e => e.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        if (!files.Any(p => p.Equals(prefix + SkillFileName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"{owner}/{repo} has no skill at '{skillPath}'.");
        }

        Directory.CreateDirectory(destinationDir);
        var supporting = new List<string>();
        string? skillMdPath = null;
        foreach (var path in files)
        {
            var relative = path[prefix.Length..];
            var destination = Path.Combine(destinationDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var bytes = await _http.GetByteArrayAsync($"{Raw}/{owner}/{repo}/{branch}/{Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal)}", ct)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(destination, bytes, ct).ConfigureAwait(false);

            if (relative.Equals(SkillFileName, StringComparison.Ordinal))
            {
                skillMdPath = destination;
            }
            else
            {
                supporting.Add(destination);
            }
        }

        var (name, _) = ParseFrontmatter(await File.ReadAllTextAsync(skillMdPath!, ct).ConfigureAwait(false));
        var title = name is { Length: > 0 } ? name : LastSegment(skillPath);
        return new LibrarySkill(skillPath, title, skillMdPath!, supporting);
    }

    /// <summary>The <c>name</c> and <c>description</c> from a SKILL.md YAML frontmatter block, if it has one.
    /// Deliberately not a YAML parser: the convention is two flat scalar keys, and taking a YAML dependency to
    /// read them would be a supply-chain cost for no gain.</summary>
    public static (string? Name, string? Description) ParseFrontmatter(string markdown)
    {
        var text = markdown.TrimStart('﻿', '\n', '\r', ' ');
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return (null, null);
        }

        string? name = null;
        string? description = null;
        foreach (var raw in text[3..end].Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                name = Unquote(line[5..]);
            }
            else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                description = Unquote(line[12..]);
            }
        }

        return (name, description);
    }

    private const string SkillFileName = "SKILL.md";

    private static string Unquote(string value) => value.Trim().Trim('"', '\'');

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static RegistrySkillEntry Entry(string owner, string repo, string dir, string? name, string? description)
        => new(dir, name is { Length: > 0 } ? name : LastSegment(dir), description, $"github.com/{owner}/{repo}/{dir}")
        {
            Publisher = owner,
        };

    private async Task<IReadOnlyList<TreeEntry>> GetTreeAsync(string owner, string repo, string branch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{Api}/repos/{owner}/{repo}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (_token is not null && await _token(ct).ConfigureAwait(false) is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub returned {(int)response.StatusCode} for {owner}/{repo}@{branch}"
                + (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? " — likely the anonymous rate limit; link a GitHub account on this host to raise it."
                    : "."));
        }

        var tree = await response.Content.ReadFromJsonAsync<TreeResponse>(ct).ConfigureAwait(false);
        return tree?.Tree ?? [];
    }

    private async Task<string?> TryGetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
                : null;
        }
        catch (HttpRequestException)
        {
            return null; // one unreadable SKILL.md shouldn't lose the rest of the listing.
        }
    }

    // The slice of GitHub's tree API we read, typed at the boundary rather than traversed as JsonElement.
    private sealed record TreeResponse([property: JsonPropertyName("tree")] IReadOnlyList<TreeEntry>? Tree);

    private sealed record TreeEntry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] string Type);
}
