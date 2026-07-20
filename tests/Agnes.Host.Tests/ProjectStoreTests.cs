using Agnes.Host.Projects;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

public class ProjectStoreTests
{
    private static ProjectStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():n}.json");
        return new ProjectStore(path);
    }

    [Fact]
    public void A_default_project_always_exists()
    {
        var store = NewStore(out var path);
        try
        {
            Assert.Contains(store.List(), p => p.IsDefault);
            Assert.True(store.Default().IsDefault);
            Assert.Empty(store.Default().RepoKey);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolve_empty_key_returns_the_default()
    {
        var store = NewStore(out var path);
        try
        {
            Assert.True(store.Resolve("").IsDefault);
            Assert.True(store.Resolve(null).IsDefault);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolve_auto_creates_a_named_project_for_an_unseen_repo_seeded_from_default()
    {
        var store = NewStore(out var path);
        try
        {
            // Configure the default so we can prove seeding.
            var def = store.Default() with
            {
                McpServers = [new McpServerInfo("m1", "rust-analyzer", "host", true, "stdio", "ra", [], new Dictionary<string, string>(), null, null)],
                Defaults = new ProjectDefaults(SkipPermissions: true, GitCredentialMode: "Ask", McpApproval: "Trust"),
            };
            store.Save(def);

            var p = store.Resolve("github.com/AdamFrisby/Agnes");

            Assert.False(p.IsDefault);
            Assert.Equal("Agnes", p.Name);                 // named after the repo
            Assert.Equal("github.com/AdamFrisby/Agnes", p.RepoKey);
            Assert.Single(p.McpServers);                    // seeded from the default
            Assert.True(p.Defaults.SkipPermissions);
            Assert.Equal("Trust", p.Defaults.McpApproval);

            // Persisted + stable: resolving again returns the same project (no duplicate).
            var again = store.Resolve("github.com/AdamFrisby/Agnes");
            Assert.Equal(p.Id, again.Id);
            Assert.Single(store.List(), x => !x.IsDefault);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_upserts_and_persists_across_reload()
    {
        var store = NewStore(out var path);
        try
        {
            var p = store.Resolve("github.com/me/repo") with { Name = "Renamed" };
            store.Save(p);

            var reloaded = new ProjectStore(path);
            Assert.Equal("Renamed", reloaded.Get(p.Id)!.Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_default_project_cannot_be_removed()
    {
        var store = NewStore(out var path);
        try
        {
            Assert.False(store.Remove(store.Default().Id));
            var p = store.Resolve("github.com/x/y");
            Assert.True(store.Remove(p.Id));
            Assert.Null(store.Get(p.Id));
        }
        finally { File.Delete(path); }
    }
}
