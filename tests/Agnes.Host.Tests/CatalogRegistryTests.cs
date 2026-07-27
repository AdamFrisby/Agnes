using System.Net;
using System.Text;
using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Registries.GitHub;
using Agnes.Registries.McpRegistry;
using Agnes.Registries.SkillsHub;

namespace Agnes.Host.Tests;

/// <summary>
/// The catalogue registry plugins, driven against recorded responses rather than the live services — the
/// payloads below are real shapes captured from skillshub.wtf, registry.modelcontextprotocol.io and GitHub, so
/// these tests catch a schema we read wrongly without needing a network at build time.
/// </summary>
public class CatalogRegistryTests
{
    // ---- the official MCP registry ----

    [Fact]
    public void A_hosted_remote_is_preferred_over_a_package_because_it_needs_nothing_installed()
    {
        var server = new OfficialMcpRegistryProvider.RegistryServer(
            "com.example/thing",
            Description: "Does a thing.",
            Packages: [new OfficialMcpRegistryProvider.RegistryPackage(RegistryType: "npm", Identifier: "thing-mcp")],
            Remotes: [new OfficialMcpRegistryProvider.RegistryRemote("streamable-http", "https://example.com/mcp")]);

        var entry = OfficialMcpRegistryProvider.Map(server);

        Assert.NotNull(entry);
        Assert.Equal(McpCatalogTransport.Http, entry.Transport);
        Assert.Equal("https://example.com/mcp", entry.Url);
    }

    [Theory]
    [InlineData("npm", "npx")]
    [InlineData("pypi", "uvx")]
    [InlineData("nuget", "dnx")]
    public void A_published_package_maps_to_the_launcher_for_its_registry(string registryType, string expectedCommand)
    {
        var server = new OfficialMcpRegistryProvider.RegistryServer(
            "com.example/thing",
            Packages: [new OfficialMcpRegistryProvider.RegistryPackage(RegistryType: registryType, Identifier: "thing-mcp", Version: "1.2.3")]);

        var entry = OfficialMcpRegistryProvider.Map(server);

        Assert.NotNull(entry);
        Assert.Equal(McpCatalogTransport.Stdio, entry.Transport);
        Assert.Equal(expectedCommand, entry.Command);
        Assert.Contains("thing-mcp", string.Join(' ', entry.LaunchArgs), StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_published_only_in_a_form_we_cannot_launch_is_skipped_not_mangled()
    {
        var server = new OfficialMcpRegistryProvider.RegistryServer(
            "com.example/container-only",
            Packages: [new OfficialMcpRegistryProvider.RegistryPackage(RegistryType: "oci", Identifier: "example/thing:1")]);

        Assert.Null(OfficialMcpRegistryProvider.Map(server));
    }

    [Fact]
    public async Task The_official_registry_response_maps_to_installable_entries()
    {
        var provider = new OfficialMcpRegistryProvider(Http(McpRegistryPayload), "https://registry.example");

        var entries = await provider.SearchAsync("filesystem");

        var entry = Assert.Single(entries);
        Assert.Equal("com.pulsemcp/remote-filesystem", entry.Id);
        Assert.Equal("remote-filesystem", entry.Name);           // the last segment, not the reverse-DNS name
        Assert.Equal("com.pulsemcp", entry.Publisher);
        Assert.Equal("npx", entry.Command);
        Assert.Equal(["-y", "remote-filesystem-mcp-server@0.1.2"], entry.LaunchArgs);
        Assert.Equal("https://github.com/pulsemcp/mcp-servers", entry.Homepage);

        // The one variable the server can't run without is flagged; the optional seven aren't.
        var required = Assert.Single(entry.RequiredEnvironment);
        Assert.Equal("GCS_BUCKET", required.Name);
        Assert.True(entry.Environment.Single(v => v.Name == "GCS_PRIVATE_KEY").IsSecret);
    }

    [Fact]
    public void Installing_a_catalogued_server_carries_its_required_variables_across_empty()
    {
        var entry = new McpCatalogEntry(
            "com.example/thing", "thing", Transport: McpCatalogTransport.Stdio, Command: "npx", Args: ["-y", "thing"],
            EnvironmentVariables:
            [
                new McpCatalogEnvVar("TOKEN", IsRequired: true, IsSecret: true),
                new McpCatalogEnvVar("REGION", Default: "eu-west-1"),
                new McpCatalogEnvVar("OPTIONAL_THING"),
            ]);

        var request = McpCatalogMapping.ToRequest(entry);

        Assert.Equal("stdio", request.Transport);
        Assert.Equal("npx", request.Command);
        // Required variables are present but blank — waiting to be filled in, not silently missing.
        Assert.Equal(string.Empty, request.Env!["TOKEN"]);
        Assert.Equal("eu-west-1", request.Env["REGION"]);
        Assert.DoesNotContain("OPTIONAL_THING", request.Env.Keys);
    }

    [Fact]
    public void A_hosted_entry_installs_as_an_http_server_with_no_command()
    {
        var entry = new McpCatalogEntry("x", "Hosted", Transport: McpCatalogTransport.Http, Url: "https://example.com/mcp");

        var request = McpCatalogMapping.ToRequest(entry);

        Assert.Equal("http", request.Transport);
        Assert.Equal("https://example.com/mcp", request.Url);
        Assert.Null(request.Command);
    }

    // ---- skillshub.wtf ----

    [Fact]
    public async Task A_skillshub_search_carries_the_facts_that_separate_identically_named_skills()
    {
        var provider = new SkillsHubRegistryProvider(Http(SkillsHubPayload), Bundles(_ => "{}"), "https://skills.example");

        var entries = await provider.SearchAsync("pdf");

        Assert.Equal(2, entries.Count);
        var official = entries[0];
        Assert.Equal("anthropics/skills/pdf", official.Id);       // owner/repo/slug — everything a fetch needs
        Assert.Equal("Anthropic", official.Publisher);
        Assert.Equal(96108, official.Stars);
        Assert.Equal(5081, official.Downloads);
        Assert.Equal("github.com/anthropics/skills", official.Source);
    }

    [Fact]
    public async Task A_skillshub_entry_with_no_source_repository_is_dropped_rather_than_offered_unfetchable()
    {
        const string payload = """{"data":[{"name":"orphan","slug":"orphan","description":null,"tags":[]}]}""";
        var provider = new SkillsHubRegistryProvider(Http(payload), Bundles(_ => "{}"), "https://skills.example");

        Assert.Empty(await provider.SearchAsync("orphan"));
    }

    [Fact]
    public async Task Fetching_a_skillshub_entry_with_a_malformed_id_says_so()
    {
        var provider = new SkillsHubRegistryProvider(Http(SkillsHubPayload), Bundles(_ => "{}"), "https://skills.example");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.FetchAsync("not-an-id", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))));
        Assert.Contains("owner/repo/slug", ex.Message, StringComparison.Ordinal);
    }

    // ---- GitHub repositories of skill bundles ----

    [Theory]
    [InlineData("---\nname: pdf\ndescription: Work with PDFs.\n---\n# PDF\n", "pdf", "Work with PDFs.")]
    [InlineData("---\nname: \"quoted\"\n---\nbody", "quoted", null)]
    [InlineData("# no frontmatter\n", null, null)]
    public void Skill_frontmatter_is_read_without_taking_a_yaml_dependency(string markdown, string? name, string? description)
    {
        var (parsedName, parsedDescription) = GitHubSkillBundles.ParseFrontmatter(markdown);

        Assert.Equal(name, parsedName);
        Assert.Equal(description, parsedDescription);
    }

    [Fact]
    public async Task A_github_repository_lists_every_directory_holding_a_skill_file()
    {
        var provider = new GitHubSkillsRegistryProvider(Bundles(url =>
            url.Contains("git/trees", StringComparison.Ordinal)
                ? GitHubTreePayload
                : "---\nname: PDF tools\ndescription: Read and write PDFs.\n---\n"));

        var entries = await provider.ListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal(["skills/docx", "skills/pdf"], entries.Select(e => e.Id));
        // The title comes from the bundle's own frontmatter, not from the folder name.
        Assert.All(entries, e => Assert.Equal("PDF tools", e.Title));
        Assert.All(entries, e => Assert.Equal("anthropics", e.Publisher));
    }

    [Fact]
    public async Task Searching_a_github_repository_filters_what_the_one_tree_call_already_returned()
    {
        var calls = 0;
        var provider = new GitHubSkillsRegistryProvider(Bundles(url =>
        {
            if (url.Contains("git/trees", StringComparison.Ordinal))
            {
                calls++;
                return GitHubTreePayload;
            }

            return "---\nname: " + (url.Contains("/pdf/", StringComparison.Ordinal) ? "pdf" : "docx") + "\n---\n";
        }));

        await provider.ListAsync();
        var hits = await provider.SearchAsync("pdf");

        Assert.Equal("skills/pdf", Assert.Single(hits).Id);
        Assert.Equal(1, calls); // the tree is cached; searching doesn't spend another of GitHub's 60/hour.
    }

    [Fact]
    public async Task A_rate_limited_github_says_what_to_do_about_it()
    {
        var provider = new GitHubSkillsRegistryProvider(new GitHubSkillBundles(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListAsync());

        Assert.Contains("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("link a GitHub account", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- fanning out across catalogues ----

    [Fact]
    public async Task One_unreachable_registry_costs_its_own_results_and_nothing_else()
    {
        var results = await CatalogSearch.SearchAsync<McpCatalogEntry>(
            [new CuratedMcpCatalogProvider(), new BrokenCatalog()], "playwright");

        Assert.Contains(results.Hits, h => h.Entry.Name == "Playwright");
        Assert.Contains("429 rate limited", Assert.Single(results.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_hit_names_the_catalogue_it_came_from()
    {
        var results = await CatalogSearch.ListAsync<McpCatalogEntry>([new CuratedMcpCatalogProvider()]);

        Assert.All(results.Hits, h =>
        {
            Assert.Equal("curated", h.CatalogId);
            Assert.Equal("Curated (built in)", h.CatalogName);
        });
        Assert.Empty(results.Failures);
    }

    [Fact]
    public void A_source_that_cannot_really_search_says_so_so_a_ui_can_hide_the_box()
    {
        var sources = CatalogSearch.Sources<McpCatalogEntry>([new CuratedMcpCatalogProvider()]);

        Assert.False(Assert.Single(sources).SupportsSearch);
    }

    private sealed class BrokenCatalog : IMcpCatalogProvider
    {
        public string Id => "broken";
        public string DisplayName => "Broken registry";
        public bool SupportsSearch => true;

        public Task<IReadOnlyList<McpCatalogEntry>> ListAsync(CancellationToken ct = default)
            => throw new HttpRequestException("429 rate limited");
    }

    // ---- helpers ----

    private static HttpClient Http(string body)
        => new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    private static GitHubSkillBundles Bundles(Func<string, string> body)
        => new(new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body(request.RequestUri!.ToString()), Encoding.UTF8, "application/json"),
        })));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    // Real response shapes, trimmed to the fields we read.

    private const string McpRegistryPayload = """
    {"servers":[{"server":{
      "$schema":"https://static.modelcontextprotocol.io/schemas/2025-09-29/server.schema.json",
      "name":"com.pulsemcp/remote-filesystem",
      "description":"MCP server for remote filesystem operations on cloud storage (Google Cloud Storage).",
      "repository":{"url":"https://github.com/pulsemcp/mcp-servers","source":"github"},
      "version":"0.1.2",
      "packages":[{
        "registryType":"npm","registryBaseUrl":"https://registry.npmjs.org",
        "identifier":"remote-filesystem-mcp-server","version":"0.1.2","runtimeHint":"npx",
        "transport":{"type":"stdio"},
        "runtimeArguments":[{"value":"-y","type":"positional"}],
        "environmentVariables":[
          {"description":"Google Cloud Storage bucket name.","isRequired":true,"name":"GCS_BUCKET"},
          {"description":"Google Cloud project ID.","name":"GCS_PROJECT_ID"},
          {"description":"Service account private key.","isSecret":true,"name":"GCS_PRIVATE_KEY"}
        ]}]}}],
      "metadata":{"nextCursor":"com.pulsemcp/remote-filesystem:0.1.2","count":1}}
    """;

    private const string SkillsHubPayload = """
    {"data":[
      {"id":"c83422b7","slug":"pdf","name":"pdf","description":"Anything with PDF files.","tags":["pdf"],
       "repo":{"id":"6485","starCount":96108,"downloadCount":5081,"githubOwner":"anthropics","githubRepoName":"skills"},
       "owner":{"id":"02d5","username":"anthropics","displayName":"Anthropic"}},
      {"id":"29bca417","slug":"pdf","name":"pdf","description":"Scientific PDFs.","tags":[],
       "repo":{"id":"8402","starCount":15532,"downloadCount":1107,"githubOwner":"K-Dense-AI","githubRepoName":"claude-scientific-skills"},
       "owner":{"id":"6ea7","username":"K-Dense-AI","displayName":"K-Dense-AI"}}],
     "total":357,"page":1,"limit":2,"hasMore":true}
    """;

    private const string GitHubTreePayload = """
    {"sha":"abc","url":"https://api.github.com/repos/anthropics/skills/git/trees/abc","tree":[
      {"path":"README.md","mode":"100644","type":"blob"},
      {"path":"skills","mode":"040000","type":"tree"},
      {"path":"skills/pdf","mode":"040000","type":"tree"},
      {"path":"skills/pdf/SKILL.md","mode":"100644","type":"blob"},
      {"path":"skills/pdf/REFERENCE.md","mode":"100644","type":"blob"},
      {"path":"skills/docx","mode":"040000","type":"tree"},
      {"path":"skills/docx/SKILL.md","mode":"100644","type":"blob"}]}
    """;
}
