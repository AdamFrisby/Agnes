using System.Security.Cryptography;
using Agnes.Abstractions;

namespace Agnes.Host.Hosting;

/// <summary>The outcome of an <see cref="SkillInstaller.InstallAsync"/> run.</summary>
/// <param name="Installed">Absolute target paths freshly written (copied or linked).</param>
/// <param name="Unchanged">Absolute target paths already identical to the source (a no-op, not re-written).</param>
/// <param name="Conflicts">Refused overwrites: an existing target whose content differs from the source.</param>
/// <param name="LinkFellBackToCopy">True when a <see cref="SyncMode.Symlink"/> install couldn't create a link
/// on this platform and copied instead (so the caller/UI can explain why the file isn't a live link).</param>
public sealed record SkillSyncResult(
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<SkillSyncConflict> Conflicts,
    bool LinkFellBackToCopy)
{
    /// <summary>True when no file was blocked by a differing-content conflict.</summary>
    public bool Succeeded => Conflicts.Count == 0;
}

/// <summary>
/// Installs a <see cref="LibrarySkill"/>'s files into an agent-visible target directory, by copy or symlink,
/// with content-digest conflict detection. Agnes's own library is the source of truth: before overwriting an
/// existing target file, both sides are SHA-256 hashed; identical content is a no-op, differing content is
/// surfaced as a <see cref="SkillSyncConflict"/> and left untouched rather than silently clobbered (the
/// "never silently destroy a user's edit" acceptance criterion). Pure over the filesystem inputs and free of
/// shared state, so the sync logic is unit-testable end-to-end.
/// </summary>
public static class SkillInstaller
{
    /// <summary>Installs <paramref name="skill"/>'s <c>SKILL.md</c> and supporting files into
    /// <paramref name="targetDir"/> (created if missing) using <paramref name="mode"/>.</summary>
    public static async Task<SkillSyncResult> InstallAsync(
        LibrarySkill skill, string targetDir, SyncMode mode, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);

        var installed = new List<string>();
        var unchanged = new List<string>();
        var conflicts = new List<SkillSyncConflict>();
        var fellBack = false;

        foreach (var source in EnumerateSourceFiles(skill))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                continue; // a missing managed file contributes nothing rather than throwing.
            }

            var target = Path.Combine(targetDir, Path.GetFileName(source));

            if (File.Exists(target))
            {
                var existing = await ComputeDigestAsync(target, ct).ConfigureAwait(false);
                var incoming = await ComputeDigestAsync(source, ct).ConfigureAwait(false);
                if (string.Equals(existing, incoming, StringComparison.Ordinal))
                {
                    unchanged.Add(target); // identical content — no-op.
                }
                else
                {
                    conflicts.Add(new SkillSyncConflict(target, existing, incoming)); // differ — do NOT clobber.
                }

                continue;
            }

            if (mode == SyncMode.Symlink)
            {
                fellBack |= !TryCreateSymlink(source, target);
            }
            else
            {
                File.Copy(source, target, overwrite: false);
            }

            installed.Add(target);
        }

        return new SkillSyncResult(installed, unchanged, conflicts, fellBack);
    }

    /// <summary>The lowercase hex SHA-256 of a file's bytes — the content digest used for conflict detection.</summary>
    public static async Task<string> ComputeDigestAsync(string path, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static IEnumerable<string> EnumerateSourceFiles(LibrarySkill skill)
    {
        yield return skill.SkillMdPath;
        foreach (var file in skill.SupportingFiles)
        {
            yield return file;
        }
    }

    /// <summary>Creates a symlink at <paramref name="target"/> pointing at <paramref name="source"/>; returns
    /// false (and copies instead) when the platform/permissions won't allow a link.</summary>
    private static bool TryCreateSymlink(string source, string target)
    {
        try
        {
            File.CreateSymbolicLink(target, source);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            File.Copy(source, target, overwrite: false); // fall back to a durable copy.
            return false;
        }
    }
}
