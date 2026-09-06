namespace Agnes.Host.Files;

/// <summary>
/// The per-session <c>.agnes/</c> directory inside a workspace: where Agnes materializes files that belong to
/// the session rather than to the project — a client's uploaded attachments, and the copies an agent sends the
/// user (<see cref="Agnes.Abstractions.FileSharedEvent"/>).
/// <para>
/// It self-ignores: a <c>.gitignore</c> containing <c>*</c> is written inside <c>.agnes/</c> itself, rather
/// than a line appended to the project's own <c>.gitignore</c>. The workspace is somebody's repository and
/// Agnes must not author commits in it, a session may not even be a git checkout, and a self-ignoring
/// directory needs no knowledge of what else is already ignored. Written once, best-effort — a workspace on a
/// read-only mount still gets its files; it just doesn't get the courtesy.
/// </para>
/// </summary>
public static class AgnesDirectory
{
    /// <summary>The directory's name inside a session workspace.</summary>
    public const string Name = ".agnes";

    /// <summary>
    /// Resolves (and creates) <c>&lt;workspaceRoot&gt;/.agnes/&lt;segments…&gt;</c>, ensuring the ignore
    /// marker exists. Every segment is resolved through <see cref="WorkspacePaths.ResolveWithin"/>, so a
    /// caller passing a crafted segment cannot land outside the workspace.
    /// </summary>
    /// <returns>The absolute path of the created directory.</returns>
    /// <exception cref="InvalidOperationException">The path would escape the workspace.</exception>
    public static string EnsureIn(string workspaceRoot, params string[] segments)
    {
        var relative = Path.Combine([Name, .. segments]);
        var directory = WorkspacePaths.ResolveWithin(workspaceRoot, relative)
            ?? throw new InvalidOperationException($"Could not resolve '{relative}' within the workspace.");

        Directory.CreateDirectory(directory);
        EnsureIgnored(workspaceRoot);
        return directory;
    }

    /// <summary>Writes <c>.agnes/.gitignore</c> (<c>*</c>) when it isn't already there. Best-effort.</summary>
    private static void EnsureIgnored(string workspaceRoot)
    {
        var root = WorkspacePaths.ResolveWithin(workspaceRoot, Name);
        if (root is null)
        {
            return;
        }

        var marker = Path.Combine(root, ".gitignore");
        if (File.Exists(marker))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(root);
            // "*" ignores everything in here including this file, which is exactly right: none of it is the
            // project's, and a stray ".gitignore" showing up as an untracked file would itself be noise.
            File.WriteAllText(marker, "*\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or otherwise hostile workspace must not stop the file itself from being written.
        }
    }
}
