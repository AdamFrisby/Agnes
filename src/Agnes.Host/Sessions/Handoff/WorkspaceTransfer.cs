using System.Text;
using Agnes.Host.Files;

namespace Agnes.Host.Sessions.Handoff;

/// <summary>What to do when the target working directory already contains files (session-handoff AC3).</summary>
public enum WorkspaceConflictPolicy
{
    /// <summary>Refuse the transfer unless the destination is empty (or absent) — never touch existing files.</summary>
    RequireEmpty,

    /// <summary>Clear the destination's existing contents first, then write the incoming workspace over it.</summary>
    Replace,

    /// <summary>Leave the occupied destination untouched and write into a fresh, non-existing sibling directory.</summary>
    SiblingCopy,
}

/// <summary>
/// Streams a session's working directory between hosts over an <see cref="IHandoffChannel"/> byte pipe — the
/// explicit, separate-from-the-conversation workspace-transfer step. Uses a tiny length-prefixed framing (no
/// external deps): per file <c>relPath | length | bytes</c>, terminated by an empty path. Every path — enumerated
/// on send, and applied on receive — is routed through <see cref="WorkspacePaths.ResolveWithin"/>, and the source
/// root is checked with <see cref="WorkspacePaths.IsSafeWorkspaceRoot"/> so a handoff can never stream (or write)
/// anything outside the project directory.
/// </summary>
public static class WorkspaceTransfer
{
    /// <summary>
    /// Packs <paramref name="sourceRoot"/> and writes it to <paramref name="destination"/>. Refuses an unsafe
    /// source root (home dir, filesystem root, or one reached via <c>..</c>) BEFORE writing a single byte, so
    /// the transfer is never partially executed (AC4). Directory symlinks are skipped (their contents are not
    /// followed), so a link inside the workspace can't smuggle files from outside it.
    /// </summary>
    public static async Task SendAsync(string sourceRoot, Stream destination, CancellationToken ct = default)
    {
        if (!WorkspacePaths.IsSafeWorkspaceRoot(sourceRoot))
        {
            throw new UnauthorizedAccessException(
                $"Refusing to transfer an unsafe workspace root: '{sourceRoot}'. The source may not be the home directory or filesystem root.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Source workspace does not exist: {root}");
        }

        await using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        foreach (var file in EnumerateFiles(new DirectoryInfo(root), root, ct))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            // Defence in depth: never emit an entry that would resolve outside the root.
            if (WorkspacePaths.ResolveWithin(root, rel) is null)
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            writer.Write(rel);
            writer.Write((long)bytes.Length);
            writer.Write(bytes);
        }

        writer.Write(string.Empty); // end marker
        writer.Flush();
    }

    /// <summary>
    /// Reads a workspace packed by <see cref="SendAsync"/> from <paramref name="source"/> into
    /// <paramref name="targetRoot"/>, honouring <paramref name="policy"/> when the destination already has files.
    /// Returns the directory actually written (equal to <paramref name="targetRoot"/> except for
    /// <see cref="WorkspaceConflictPolicy.SiblingCopy"/>). Each incoming entry is re-validated against the effective
    /// root, so a malicious archive with a <c>..</c> entry can't escape.
    /// </summary>
    public static async Task<string> ReceiveAsync(
        Stream source, string targetRoot, WorkspaceConflictPolicy policy, CancellationToken ct = default)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));
        var effective = ResolveTarget(target, policy);
        Directory.CreateDirectory(effective);

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rel = reader.ReadString();
            if (rel.Length == 0)
            {
                break; // end marker
            }

            var length = reader.ReadInt64();
            var buffer = ReadExactly(reader, length);
            var dest = WorkspacePaths.ResolveWithin(effective, rel)
                ?? throw new UnauthorizedAccessException($"Workspace entry '{rel}' escapes the target root.");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllBytesAsync(dest, buffer, ct).ConfigureAwait(false);
        }

        return effective;
    }

    private static string ResolveTarget(string target, WorkspaceConflictPolicy policy)
    {
        var occupied = Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any();
        if (!occupied)
        {
            return target;
        }

        switch (policy)
        {
            case WorkspaceConflictPolicy.RequireEmpty:
                throw new IOException(
                    $"Handoff target '{target}' already contains files and the conflict policy is RequireEmpty.");
            case WorkspaceConflictPolicy.Replace:
                ClearDirectory(target);
                return target;
            case WorkspaceConflictPolicy.SiblingCopy:
                return ForkNaming.Propose(target);
            default:
                throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown workspace conflict policy.");
        }
    }

    private static void ClearDirectory(string target)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
        {
            if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    // Recursively enumerate files, skipping symlinked directories (their targets may lie outside the workspace).
    private static IEnumerable<string> EnumerateFiles(DirectoryInfo dir, string root, CancellationToken ct)
    {
        foreach (var file in dir.GetFiles())
        {
            ct.ThrowIfCancellationRequested();
            yield return file.FullName;
        }

        foreach (var sub in dir.GetDirectories())
        {
            ct.ThrowIfCancellationRequested();
            if (sub.LinkTarget is not null)
            {
                continue;
            }

            foreach (var file in EnumerateFiles(sub, root, ct))
            {
                yield return file;
            }
        }
    }

    private static byte[] ReadExactly(BinaryReader reader, long length)
    {
        if (length < 0 || length > int.MaxValue)
        {
            throw new InvalidDataException($"Workspace entry length {length} is out of range.");
        }

        var buffer = reader.ReadBytes((int)length);
        if (buffer.Length != length)
        {
            throw new EndOfStreamException("Workspace transfer stream ended mid-entry.");
        }

        return buffer;
    }
}
