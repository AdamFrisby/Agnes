using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

public class SkillInstallerTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), $"agnes-skill-sync-{Guid.NewGuid():n}");

    private static LibrarySkill MakeSkill(string sourceDir, string skillBody = "SKILL body", string supportBody = "support body")
    {
        Directory.CreateDirectory(sourceDir);
        var skillMd = Path.Combine(sourceDir, "SKILL.md");
        File.WriteAllText(skillMd, skillBody);
        var support = Path.Combine(sourceDir, "notes.md");
        File.WriteAllText(support, supportBody);
        return new LibrarySkill("s1", "Skill", skillMd, [support]);
    }

    [Fact]
    public async Task Copy_installs_files_into_the_target_directory()
    {
        var src = NewTempDir();
        var target = NewTempDir();
        try
        {
            var skill = MakeSkill(src);
            var result = await SkillInstaller.InstallAsync(skill, target, SyncMode.Copy);

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.Installed.Count);
            Assert.Empty(result.Conflicts);

            var installedSkillMd = Path.Combine(target, "SKILL.md");
            var installedNotes = Path.Combine(target, "notes.md");
            Assert.True(File.Exists(installedSkillMd));
            Assert.True(File.Exists(installedNotes));
            Assert.Equal("SKILL body", File.ReadAllText(installedSkillMd));
            // A copy is an independent file, not a link.
            Assert.Null(new FileInfo(installedSkillMd).LinkTarget);
        }
        finally
        {
            if (Directory.Exists(src)) { Directory.Delete(src, recursive: true); }
            if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
        }
    }

    [Fact]
    public async Task Symlink_creates_links_or_falls_back_to_copy_on_unsupported_platforms()
    {
        var src = NewTempDir();
        var target = NewTempDir();
        try
        {
            var skill = MakeSkill(src);
            var result = await SkillInstaller.InstallAsync(skill, target, SyncMode.Symlink);

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.Installed.Count);

            var installedSkillMd = Path.Combine(target, "SKILL.md");
            Assert.True(File.Exists(installedSkillMd));
            // Reading through the link (or copy) yields the source content either way.
            Assert.Equal("SKILL body", File.ReadAllText(installedSkillMd));

            if (result.LinkFellBackToCopy)
            {
                // Platform couldn't link — assert the durable copy fallback took effect.
                Assert.Null(new FileInfo(installedSkillMd).LinkTarget);
            }
            else
            {
                // A real link points back at the managed original.
                Assert.NotNull(new FileInfo(installedSkillMd).LinkTarget);
            }
        }
        finally
        {
            if (Directory.Exists(src)) { Directory.Delete(src, recursive: true); }
            if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
        }
    }

    [Fact]
    public async Task An_existing_file_with_a_different_digest_yields_a_conflict_and_is_not_overwritten()
    {
        var src = NewTempDir();
        var target = NewTempDir();
        try
        {
            var skill = MakeSkill(src);
            Directory.CreateDirectory(target);
            // A user (or other tool) has since edited the installed SKILL.md.
            var existingSkillMd = Path.Combine(target, "SKILL.md");
            File.WriteAllText(existingSkillMd, "locally edited — must not be clobbered");

            var result = await SkillInstaller.InstallAsync(skill, target, SyncMode.Copy);

            Assert.False(result.Succeeded);
            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal(existingSkillMd, conflict.Path);
            Assert.NotEqual(conflict.ExistingDigest, conflict.IncomingDigest);
            Assert.Equal(await SkillInstaller.ComputeDigestAsync(existingSkillMd), conflict.ExistingDigest);

            // The edit is preserved — the differing file was NOT overwritten.
            Assert.Equal("locally edited — must not be clobbered", File.ReadAllText(existingSkillMd));
            // The non-conflicting supporting file still installs.
            Assert.Contains(Path.Combine(target, "notes.md"), result.Installed);
            Assert.True(File.Exists(Path.Combine(target, "notes.md")));
        }
        finally
        {
            if (Directory.Exists(src)) { Directory.Delete(src, recursive: true); }
            if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
        }
    }

    [Fact]
    public async Task An_existing_file_with_an_identical_digest_is_a_noop()
    {
        var src = NewTempDir();
        var target = NewTempDir();
        try
        {
            var skill = MakeSkill(src);
            Directory.CreateDirectory(target);
            // Pre-place byte-identical copies of both files.
            File.WriteAllText(Path.Combine(target, "SKILL.md"), "SKILL body");
            File.WriteAllText(Path.Combine(target, "notes.md"), "support body");

            var result = await SkillInstaller.InstallAsync(skill, target, SyncMode.Copy);

            Assert.True(result.Succeeded);
            Assert.Empty(result.Installed);
            Assert.Empty(result.Conflicts);
            Assert.Equal(2, result.Unchanged.Count);
        }
        finally
        {
            if (Directory.Exists(src)) { Directory.Delete(src, recursive: true); }
            if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
        }
    }
}
