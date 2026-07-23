using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

public class SkillLibraryTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), $"agnes-skill-lib-{Guid.NewGuid():n}");

    private static (string SkillMd, string F1, string F2) WriteSourceBundle(string dir, string frontmatter)
    {
        Directory.CreateDirectory(dir);
        var skillMd = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(skillMd, frontmatter);
        var f1 = Path.Combine(dir, "reference.md");
        File.WriteAllText(f1, "reference material");
        var f2 = Path.Combine(dir, "helper.sh");
        File.WriteAllText(f2, "echo hello");
        return (skillMd, f1, f2);
    }

    [Fact]
    public void Skill_bundle_round_trips_save_list_load_and_delete_removes_all()
    {
        var dir = NewTempDir();
        var src = NewTempDir();
        try
        {
            var (skillMd, f1, f2) = WriteSourceBundle(src, "---\nname: API Helper\ndescription: Work with the internal API\n---\nInstructions here.");

            var library = new SkillLibrary(dir);
            var saved = library.Save(id: null, title: string.Empty, sourceSkillMdPath: skillMd, supportingFiles: [f1, f2]);

            // Title falls back to the SKILL.md frontmatter name; all files copied into managed storage.
            Assert.Equal("API Helper", saved.Title);
            Assert.Equal(2, saved.SupportingFiles.Count);
            Assert.True(File.Exists(saved.SkillMdPath));
            Assert.All(saved.SupportingFiles, f => Assert.True(File.Exists(f)));

            // A fresh instance over the same directory loads the bundle as a unit.
            var reloaded = new SkillLibrary(dir);
            var listed = Assert.Single(reloaded.List());
            Assert.Equal(saved.Id, listed.Id);
            var got = reloaded.Get(saved.Id);
            Assert.NotNull(got);
            Assert.Equal(2, got!.SupportingFiles.Count);
            Assert.True(File.Exists(got.SkillMdPath));
            Assert.All(got.SupportingFiles, f => Assert.True(File.Exists(f)));

            // Delete removes the record AND every managed file for the bundle.
            var managedDir = Path.GetDirectoryName(saved.SkillMdPath)!;
            Assert.True(reloaded.Delete(saved.Id));
            Assert.Empty(reloaded.List());
            Assert.False(File.Exists(saved.SkillMdPath));
            Assert.False(Directory.Exists(managedDir));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
            if (Directory.Exists(src)) { Directory.Delete(src, recursive: true); }
        }
    }

    [Fact]
    public void An_explicit_title_overrides_the_frontmatter_name()
    {
        var dir = NewTempDir();
        var src = NewTempDir();
        try
        {
            var (skillMd, _, _) = WriteSourceBundle(src, "---\nname: frontmatter-name\ndescription: d\n---\nbody");
            var library = new SkillLibrary(dir);
            var saved = library.Save(id: null, title: "Chosen Title", sourceSkillMdPath: skillMd, supportingFiles: []);
            Assert.Equal("Chosen Title", saved.Title);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
            if (Directory.Exists(src)) { Directory.Delete(src, recursive: true); }
        }
    }

    [Fact]
    public void A_blank_directory_disables_the_skill_library()
    {
        var disabled = new SkillLibrary(directory: null);
        Assert.Empty(disabled.List());
        Assert.Throws<InvalidOperationException>(() => disabled.Save(null, "t", "SKILL.md", []));
    }

    [Fact]
    public void SkillMarkdown_parses_name_and_description_frontmatter()
    {
        var parsed = SkillMarkdown.ParseFrontmatter("---\nname: My Skill\ndescription: does useful things\n---\nBody text.");
        Assert.Equal("My Skill", parsed.Name);
        Assert.Equal("does useful things", parsed.Description);

        // Quoted values are unwrapped.
        var quoted = SkillMarkdown.ParseFrontmatter("---\nname: \"Quoted Name\"\n---\n");
        Assert.Equal("Quoted Name", quoted.Name);

        // No frontmatter yields empty fields rather than throwing.
        var none = SkillMarkdown.ParseFrontmatter("just some markdown, no frontmatter");
        Assert.Null(none.Name);
        Assert.Null(none.Description);
    }
}
