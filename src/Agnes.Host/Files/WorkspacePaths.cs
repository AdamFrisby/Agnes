namespace Agnes.Host.Files;

/// <summary>What to do when an uploaded attachment's filename already exists in the target directory.</summary>
public enum AttachmentConflict
{
    /// <summary>Write under a distinct "name (n).ext" so both the original and the new file survive.</summary>
    KeepBoth,

    /// <summary>Overwrite the existing file.</summary>
    Replace,

    /// <summary>Keep the existing file and write nothing.</summary>
    Skip,
}

/// <summary>
/// The single path-safety guard for anything that resolves a client-supplied path against a session's
/// workspace root — attachment uploads, and (later) the file browser and session-handoff's workspace
/// transfer. Every such operation MUST route through <see cref="ResolveWithin"/> so a <c>..</c> segment,
/// an absolute path, or any other traversal can never touch a file outside the workspace.
/// </summary>
public static class WorkspacePaths
{
    /// <summary>
    /// Resolves <paramref name="candidate"/> (a workspace-relative path or a bare filename) to an absolute
    /// path that is provably inside <paramref name="workspaceRoot"/>, or returns null if it would escape.
    /// Rejects <c>..</c> traversal and absolute candidates: <see cref="Path.GetFullPath(string, string)"/>
    /// normalizes the combined path, and the result is accepted only when it is the root itself or lies
    /// under it (compared with a trailing separator so a sibling like <c>/work-evil</c> can't masquerade as
    /// being under <c>/work</c>).
    /// </summary>
    public static string? ResolveWithin(string workspaceRoot, string candidate)
    {
        if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        // GetFullPath(candidate, root) resolves `candidate` relative to `root` and normalizes `..`; an
        // absolute `candidate` is resolved as-is (and will then fail the containment check below).
        var resolved = Path.GetFullPath(candidate, root);

        if (string.Equals(resolved, root, StringComparison.Ordinal))
        {
            return resolved;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return resolved.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? resolved : null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is safe to stream wholesale as a workspace ROOT (session-handoff AC4):
    /// a real, rooted directory that is NOT the filesystem root, the user's home directory, or any ancestor of
    /// home (so <c>/</c>, <c>/home</c>, <c>/Users</c> are all refused). Normalizes first, so an unsafe path
    /// reached via <c>..</c> (e.g. a project folder plus <c>../..</c> landing on home) is caught too. Guards a
    /// handoff from accidentally streaming far more than the intended project directory.
    /// </summary>
    public static bool IsSafeWorkspaceRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // The filesystem root itself (e.g. "/", "C:\").
        var fsRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty);
        if (full.Length == 0 || string.Equals(full, fsRoot, StringComparison.Ordinal))
        {
            return false;
        }

        // The user's home directory, or any ancestor of it.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
            if (string.Equals(full, home, StringComparison.Ordinal))
            {
                return false;
            }

            var homeWithSeparator = home + Path.DirectorySeparatorChar;
            var fullWithSeparator = full + Path.DirectorySeparatorChar;
            if (homeWithSeparator.StartsWith(fullWithSeparator, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
