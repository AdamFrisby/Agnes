using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

public class LocalDirectoryRegistryProviderTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), $"agnes-skill-registry-{Guid.NewGuid():n}");

    private static void WriteSkillDir(string root, string id, string frontmatter)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), frontmatter);
        File.WriteAllText(Path.Combine(dir, "notes.md"), "supporting notes");
    }

    [Fact]
    public async Task Lists_skills_and_is_selectable_through_the_plugin_registry()
    {
        var root = NewTempDir();
        try
        {
            WriteSkillDir(root, "api-helper", "---\nname: API Helper\ndescription: work with the API\n---\nbody");
            WriteSkillDir(root, "not-a-skill-missing-md", "ignored");
            // Remove the SKILL.md from the second dir so it isn't a bundle.
            File.Delete(Path.Combine(root, "not-a-skill-missing-md", "SKILL.md"));

            var provider = new LocalDirectoryRegistryProvider(root);
            // Selectable by id via the same generic plugin registry every plugin point uses.
            var registry = new PluginRegistry<IPromptRegistryProvider>([provider], p => p.Id);
            var selected = registry.Find("local-dir");
            Assert.NotNull(selected);

            var entries = await selected!.ListAsync();
            var entry = Assert.Single(entries);
            Assert.Equal("api-helper", entry.Id);
            Assert.Equal("API Helper", entry.Title);
            Assert.Equal("work with the API", entry.Description);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public async Task Fetch_materializes_a_skill_into_a_destination_without_network()
    {
        var root = NewTempDir();
        var dest = NewTempDir();
        try
        {
            WriteSkillDir(root, "api-helper", "---\nname: API Helper\ndescription: d\n---\nbody");

            var provider = new LocalDirectoryRegistryProvider(root);
            var skill = await provider.FetchAsync("api-helper", dest);

            Assert.Equal("api-helper", skill.Id);
            Assert.Equal("API Helper", skill.Title);
            Assert.True(File.Exists(skill.SkillMdPath));
            Assert.StartsWith(dest, skill.SkillMdPath, StringComparison.Ordinal);
            var support = Assert.Single(skill.SupportingFiles);
            Assert.True(File.Exists(support));
            Assert.Equal("supporting notes", File.ReadAllText(support));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            if (Directory.Exists(dest)) { Directory.Delete(dest, recursive: true); }
        }
    }

    [Fact]
    public async Task Fetching_an_unknown_entry_throws()
    {
        var root = NewTempDir();
        var dest = NewTempDir();
        try
        {
            Directory.CreateDirectory(root);
            var provider = new LocalDirectoryRegistryProvider(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FetchAsync("nope", dest));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            if (Directory.Exists(dest)) { Directory.Delete(dest, recursive: true); }
        }
    }
}
